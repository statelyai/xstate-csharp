using Xunit;

namespace XState.Tests;

/// <summary>
/// The durable-execution contract: what a host must be able to rely on when it journals a
/// machine's effects and replays them — canonical event spellings, timers the machine owns,
/// child identity that survives id reuse, and the boundary rules for a terminal actor.
/// </summary>
public class DurableContractTests
{
    private static StateMachine<Unit> Child() =>
        Machine.Create("kid").Initial("i").State("i").Build();

    // --- Canonical event spellings (v6 constants.ts) ---

    [Fact]
    public void Lifecycle_events_use_the_reserved_at_xstate_namespace()
    {
        Assert.Equal("@xstate.init", new InitEvent().Type);
        Assert.Equal("@xstate.stop", new StopEvent().Type);
        Assert.Equal("xstate.timer", new TimerEvent("t").Type);
    }

    [Fact]
    public void Effect_kinds_match_the_v6_effect_descriptor_discriminants()
    {
        Assert.Equal("@xstate.spawn", new SpawnEffect("a", Child(), null, null, "0").Kind);
        Assert.Equal("@xstate.start", new StartEffect("a").Kind);
        Assert.Equal("@xstate.raise", new RaiseEffect(new Ping(), "t", TimeSpan.Zero).Kind);
        Assert.Equal("@xstate.sendTo", new SendToEffect("a", new Ping()).Kind);
        Assert.Equal("@xstate.cancel", new CancelEffect("t").Kind);
        Assert.Equal("@xstate.stop", new StopChildEffect("a").Kind);
        Assert.Equal("@xstate.terminate", new TerminateEffect(SnapshotStatus.Done, null, null).Kind);
        Assert.Equal("@xstate.deadLetter", new DeadLetterEffect(new Ping(), "stopped").Kind);
        Assert.Equal("emit", new EmitEffect(new Ping()).Kind);
        Assert.Equal("action", new ActionEffect(null, null, () => { }).Kind);
    }

    // --- Timers ---

    [Fact]
    public void A_timer_that_is_no_longer_on_the_ledger_fires_into_the_void()
    {
        var machine = Machine.Create("timers")
            .Initial("waiting")
            .State("waiting", s => s
                .After(TimeSpan.FromSeconds(1), t => t.Target("late"))
                .On<Go>(t => t.Target("elsewhere")))
            .State("late")
            .State("elsewhere")
            .Build();

        var initial = machine.GetInitialState();
        var timerId = Assert.Single(initial.Effects.OfType<RaiseEffect>()).Id!;
        Assert.True(initial.State.Timers.ContainsKey(timerId));

        // Leaving the state cancels its `after` timer, on the ledger and at the host.
        var left = machine.Transition(initial.State, new Go());
        Assert.Empty(left.State.Timers);
        Assert.Contains(left.Effects, e => e is CancelEffect c && c.SendId == timerId);

        // A cancel always races the firing: the host may report a timer it has already been told
        // to drop. The machine is the authority, so the stale firing changes nothing.
        var stale = machine.Transition(left.State, new TimerEvent(timerId));
        Assert.Same(left.State, stale.State);
        Assert.Empty(stale.Effects);
        Assert.True(stale.State.Matches("elsewhere"));
    }

    [Fact]
    public void A_live_timer_releases_its_event_when_the_host_reports_it()
    {
        var machine = Machine.Create("timers")
            .Initial("waiting")
            .State("waiting", s => s.After(TimeSpan.FromSeconds(1), t => t.Target("late")))
            .State("late")
            .Build();

        var initial = machine.GetInitialState();
        var timerId = Assert.Single(initial.Effects.OfType<RaiseEffect>()).Id!;

        var fired = machine.Transition(initial.State, new TimerEvent(timerId));

        Assert.True(fired.State.Matches("late"));
        Assert.Empty(fired.State.Timers);
    }

    [Fact]
    public void A_delayed_send_to_another_actor_becomes_an_immediate_send_when_its_timer_fires()
    {
        var machine = Machine.Create("timers")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.SendTo(
                "peer",
                _ => new Ping(),
                delay: TimeSpan.FromSeconds(1),
                id: "later")))
            .Build();

        var scheduled = machine.Transition(machine.GetInitialState().State, new Go());

        var timer = Assert.Single(scheduled.State.Timers.Values);
        Assert.Equal(TimerKind.SendTo, timer.Kind);
        Assert.Equal("peer", timer.Target);

        var fired = machine.Transition(scheduled.State, new TimerEvent("later"));

        var send = Assert.Single(fired.Effects.OfType<SendToEffect>());
        Assert.Equal("peer", send.Target);
        Assert.Null(send.Delay);
        Assert.IsType<Ping>(send.Event);
        Assert.Empty(fired.State.Timers);
    }

    // --- Incarnation ---

    [Fact]
    public void A_completion_from_a_previous_incarnation_of_a_reused_id_is_dropped()
    {
        var machine = Machine.Create("supervisor")
            .Initial("a")
            .State("a", s => s
                .On<Ping>(t => t.Do(new SpawnAction<Unit>(Child(), Id: "worker")))
                .On<Go>(t => t
                    .Do(new StopChildAction<Unit>(_ => "worker"))
                    .Do(new SpawnAction<Unit>(Child(), Id: "worker")))
                .On("xstate.done.actor", t => t.Target("b")))
            .State("b")
            .Build();

        var first = machine.Transition(machine.GetInitialState().State, new Ping());
        var firstIncarnation = Assert.Single(first.Effects.OfType<SpawnEffect>()).Incarnation;

        var restarted = machine.Transition(first.State, new Go());
        var secondIncarnation = Assert.Single(restarted.Effects.OfType<SpawnEffect>()).Incarnation;

        Assert.NotEqual(firstIncarnation, secondIncarnation);
        Assert.Equal(secondIncarnation, Assert.Single(restarted.State.Children).Incarnation);

        // The stopped child's completion arrives late. It names a live id, but not this child.
        var stale = machine.Transition(restarted.State, new DoneActorEvent("worker", null, firstIncarnation));
        Assert.Same(restarted.State, stale.State);
        Assert.Empty(stale.Effects);

        // The live child's own completion is taken, and so is one from a host that does not
        // track incarnations at all (v6 `matchesActorSession`: no token means no objection).
        Assert.True(machine.Transition(restarted.State, new DoneActorEvent("worker", null, secondIncarnation)).State.Matches("b"));
        Assert.True(machine.Transition(restarted.State, new DoneActorEvent("worker", null, null)).State.Matches("b"));
    }

    // --- Deferred starts ---

    [Fact]
    public void Children_are_started_at_the_end_of_the_macrostep_in_spawn_order()
    {
        var machine = Machine.Create("starts")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Target("b")))
            .State("b", s => s
                .Invoke(Child(), "first")
                .Invoke(Child(), "second")
                .Entry(new SendToAction<Unit>(_ => "first", _ => new Ping())))
            .Build();

        var effects = machine.Transition(machine.GetInitialState().State, new Go()).Effects;

        Assert.Equal(
            ["@xstate.spawn", "@xstate.spawn", "@xstate.sendTo", "@xstate.start", "@xstate.start"],
            effects.Select(e => e.Kind));

        Assert.Equal(["first", "second"], effects.OfType<StartEffect>().Select(e => e.Id));
    }

    [Fact]
    public void A_child_spawned_and_stopped_in_the_same_macrostep_is_never_started()
    {
        // Entering `b` invokes the child; the eventless transition leaves `b` in the same
        // macrostep, which stops it — so the deferred start must be dropped, not emitted.
        var machine = Machine.Create("spawn-stop")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Target("b")))
            .State("b", s => s
                .Invoke(Child(), "kid")
                .Always(t => t.Target("c")))
            .State("c")
            .Build();

        var result = machine.Transition(machine.GetInitialState().State, new Go());

        Assert.Equal(["@xstate.spawn", "@xstate.stop"], result.Effects.Select(e => e.Kind));
        Assert.Empty(result.Effects.OfType<StartEffect>());
        Assert.Empty(result.State.Children);
    }

    // --- Termination ---

    [Fact]
    public void Reaching_a_final_state_appends_exactly_one_termination_effect()
    {
        var machine = Machine.Create("done")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Target("fin")))
            .Final("fin", s => s.Output(_ => 42))
            .Build();

        var initial = machine.GetInitialState();
        Assert.Empty(initial.Effects.OfType<TerminateEffect>());

        var done = machine.Transition(initial.State, new Go());

        var terminate = Assert.Single(done.Effects.OfType<TerminateEffect>());
        Assert.Equal(SnapshotStatus.Done, terminate.Status);
        Assert.Equal(42, terminate.Output);
        Assert.Null(terminate.Error);

        // It is the last effect: everything the macrostep released comes first.
        Assert.Same(terminate, done.Effects[^1]);
    }

    [Fact]
    public void An_initial_transition_straight_into_a_final_state_terminates_too()
    {
        var machine = Machine.Create("instant").Initial("fin").Final("fin").Build();

        var initial = machine.GetInitialState();

        Assert.Equal(SnapshotStatus.Done, initial.State.Status);
        Assert.Equal(SnapshotStatus.Done, Assert.Single(initial.Effects.OfType<TerminateEffect>()).Status);
    }

    // --- Boundary ---

    [Fact]
    public void A_stopped_actor_dead_letters_every_further_event()
    {
        var machine = Machine.Create("boundary").Initial("a").State("a").Build();

        var stopped = machine.Stop(machine.GetInitialState().State);
        Assert.Equal(SnapshotStatus.Stopped, stopped.State.Status);

        var evt = new Go();
        var rejected = machine.Transition(stopped.State, evt);

        Assert.Same(stopped.State, rejected.State);
        var dead = Assert.IsType<DeadLetterEffect>(Assert.Single(rejected.Effects));
        Assert.Equal("stopped", dead.Reason);
        Assert.Same(evt, dead.Event);
    }

    [Fact]
    public void An_event_the_validator_rejects_is_never_delivered()
    {
        var boom = new ArgumentException("unknown event");

        var machine = Machine.Create("boundary")
            .ValidateEvents(evt => evt is Go ? boom : null)
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Target("b")))
            .State("b")
            .Build();

        var initial = machine.GetInitialState().State;
        var rejected = machine.Transition(initial, new Go());

        Assert.Same(initial, rejected.State);
        Assert.True(rejected.State.Matches("a"));

        var dead = Assert.IsType<DeadLetterEffect>(Assert.Single(rejected.Effects));
        Assert.Equal("invalidEvent", dead.Reason);
        Assert.Same(boom, dead.Error);
    }

    [Fact]
    public void The_validator_never_sees_the_host_s_own_protocol_events()
    {
        var machine = Machine.Create("boundary")
            .ValidateEvents(_ => new ArgumentException("everything is invalid"))
            .Initial("waiting")
            .State("waiting", s => s.After(TimeSpan.FromSeconds(1), t => t.Target("late")))
            .State("late")
            .Build();

        var initial = machine.GetInitialState();
        var timerId = Assert.Single(initial.Effects.OfType<RaiseEffect>()).Id!;

        // `xstate.timer` is the host telling the machine its own timer elapsed; rejecting it
        // would strand the timer on the ledger forever.
        var fired = machine.Transition(initial.State, new TimerEvent(timerId));

        Assert.True(fired.State.Matches("late"));
        Assert.Empty(fired.Effects.OfType<DeadLetterEffect>());
    }

    // --- Stop ---

    [Fact]
    public void Stopping_releases_children_and_timers_and_runs_nothing_else()
    {
        var exits = new List<string>();

        var machine = Machine.Create("stop")
            .Initial("a")
            .State("a", s => s
                .Invoke(Child(), "kid")
                .After(TimeSpan.FromSeconds(1), t => t.Target("b"))
                .Exit(_ => exits.Add("a")))
            .State("b")
            .Build();

        var initial = machine.GetInitialState();
        var timerId = Assert.Single(initial.Effects.OfType<RaiseEffect>()).Id!;

        var stopped = machine.Stop(initial.State);

        Assert.Equal(SnapshotStatus.Stopped, stopped.State.Status);
        Assert.Empty(exits);
        Assert.Empty(stopped.State.Children);
        Assert.Empty(stopped.State.Timers);

        Assert.Contains(stopped.Effects, e => e is CancelEffect c && c.SendId == timerId);
        Assert.Contains(stopped.Effects, e => e is StopChildEffect { ChildId: "kid" });

        // Stopping is not a terminal *outcome*: there is nothing to report to a parent.
        Assert.Empty(stopped.Effects.OfType<TerminateEffect>());
    }

    // --- Actor sources ---

    [Fact]
    public void A_child_invoked_by_source_key_records_the_key_that_makes_it_persistable()
    {
        var machine = Machine.Create("sources")
            .Actor("fetchUser", Child())
            .Initial("loading")
            .State("loading", s => s.Invoke("fetchUser", id: "fetch"))
            .Build();

        var spawn = Assert.Single(machine.GetInitialState().Effects.OfType<SpawnEffect>());

        Assert.Equal("fetchUser", spawn.Src);
        Assert.Equal("fetchUser", Assert.Single(machine.GetInitialState().State.Children).Src);
    }

    [Fact]
    public void Invoking_an_unregistered_source_key_fails_at_build_time()
    {
        var builder = Machine.Create("sources")
            .Initial("loading")
            .State("loading", s => s.Invoke("fetchUser"));

        var error = Assert.Throws<InvalidOperationException>(builder.Build);
        Assert.Contains("fetchUser", error.Message);
    }

    [Fact]
    public void Logic_passed_by_value_still_records_the_key_it_is_registered_under()
    {
        var child = Child();

        var machine = Machine.Create("sources")
            .Actor("fetchUser", child)
            .Initial("loading")
            .State("loading", s => s.Invoke(child, id: "fetch"))
            .Build();

        Assert.Equal(
            "fetchUser",
            Assert.Single(machine.GetInitialState().Effects.OfType<SpawnEffect>()).Src);
    }

    // --- Descriptors ---

    [Fact]
    public void Effect_descriptors_replace_actor_references_with_addresses()
    {
        var spawn = EffectDescriptor.Of(new SpawnEffect("kid", Child(), 7, "kidSrc", "0"), "root/parent");
        Assert.Equal("root/parent/kid", spawn.Actor);
        Assert.Equal("root/parent", spawn.Source);
        Assert.Equal("kidSrc", spawn.Src);
        Assert.Equal(7, spawn.Input);

        var toParent = EffectDescriptor.Of(new SendToEffect(EffectTarget.Parent, new Ping()), "root/parent/kid");
        Assert.Equal("root/parent", toParent.Target);

        var toSelf = EffectDescriptor.Of(new SendToEffect(EffectTarget.Self, new Ping()), "root/parent");
        Assert.Equal("root/parent", toSelf.Target);

        // The non-serializable members are dropped: an action descriptor is (type, params).
        var action = EffectDescriptor.Of(new ActionEffect("notify", 42, () => { }), "root");
        Assert.Equal("action", action.Kind);
        Assert.Equal("notify", action.Type);
        Assert.Equal(42, action.Params);
    }

    // --- Snapshot shape ---

    [Fact]
    public void The_snapshot_exposes_the_nested_state_value_and_the_active_tags()
    {
        var machine = Machine.Create("value")
            .Initial("p")
            .Parallel("p", p => p
                .State("left", r => r.Initial("l1").State("l1", c => c.Tag("busy")).State("l2"))
                .State("right", r => r.Initial("r1").State("r1")))
            .Build();

        var state = machine.GetInitialState().State;
        var value = Assert.IsType<Dictionary<string, object>>(state.Value);
        var regions = Assert.IsType<Dictionary<string, object>>(value["p"]);

        Assert.Equal("l1", regions["left"]);
        Assert.Equal("r1", regions["right"]);

        Assert.True(state.HasTag("busy"));
        Assert.Equal(["busy"], state.Tags);
    }
}
