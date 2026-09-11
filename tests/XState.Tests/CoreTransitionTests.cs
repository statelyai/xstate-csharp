using XState.TestKit;
using Xunit;

namespace XState.Tests;

public sealed record Toggle : MachineEvent;
public sealed record Go : MachineEvent;
public sealed record Back : MachineEvent;
public sealed record Next : MachineEvent;
public sealed record Finish(int Amount = 0) : MachineEvent;
public sealed record Finish1 : MachineEvent;
public sealed record Finish2 : MachineEvent;
public sealed record Ping : MachineEvent;
public sealed record Increment(int By) : MachineEvent;

public sealed record Counter(int Count);

/// <summary>Minimal non-machine actor logic: it holds its input and never moves.</summary>
internal sealed class StubLogic : IActorLogic
{
    private sealed record Snapshot(object? Output) : ISnapshot
    {
        public SnapshotStatus Status => SnapshotStatus.Active;
        public Exception? Error => null;
    }

    public (ISnapshot State, IReadOnlyList<Effect> Effects) GetInitialSnapshot(object? input) =>
        (new Snapshot(input), []);

    public (ISnapshot State, IReadOnlyList<Effect> Effects) Transition(ISnapshot state, MachineEvent evt) =>
        (state, []);
}

public class CoreTransitionTests
{
    private static void RunEffects<T>(TransitionResult<T> result)
    {
        foreach (var effect in result.Effects)
        {
            if (effect is ActionEffect action)
            {
                action.Exec();
            }
        }
    }

    // --- 1. Toggle machine, typed events ---

    [Fact]
    public void Toggles_between_states_on_typed_events()
    {
        var machine = Machine.Create("toggle")
            .Initial("inactive")
            .State("inactive", s => s.On<Toggle>(t => t.Target("active")))
            .State("active", s => s.On<Toggle>(t => t.Target("inactive")))
            .Build();

        var initial = machine.GetInitialState();
        Assert.True(initial.State.Matches("inactive"));
        Assert.Equal(SnapshotStatus.Active, initial.State.Status);

        var active = machine.Transition(initial.State, new Toggle());
        Assert.True(active.State.Matches("active"));

        var inactive = machine.Transition(active.State, new Toggle());
        Assert.True(inactive.State.Matches("inactive"));

        // Unhandled events leave the state alone.
        var unchanged = machine.Transition(inactive.State, new Go());
        Assert.True(unchanged.State.Matches("inactive"));
        Assert.Empty(unchanged.Effects);
    }

    // --- 2. Guards ---

    [Fact]
    public void Guard_blocks_and_allows_transitions()
    {
        var machine = Machine.Create<Counter>("guarded")
            .Context(_ => new Counter(0))
            .Initial("idle")
            .State("idle", s => s
                .On<Increment>(t => t
                    .Guard(a => a.Context.Count + a.Event.By >= 3)
                    .Target("high"))
                .On<Increment>(t => t
                    .Assign(a => a.Context with { Count = a.Context.Count + a.Event.By })))
            .State("high")
            .Build();

        var state = machine.GetInitialState().State;

        var low = machine.Transition(state, new Increment(1));
        Assert.True(low.State.Matches("idle"));
        Assert.Equal(1, low.State.Context.Count);

        var stillLow = machine.Transition(low.State, new Increment(1));
        Assert.True(stillLow.State.Matches("idle"));
        Assert.Equal(2, stillLow.State.Context.Count);

        var high = machine.Transition(stillLow.State, new Increment(1));
        Assert.True(high.State.Matches("high"));
    }

    [Fact]
    public void Guard_receives_In_predicate_for_the_starting_configuration()
    {
        var machine = Machine.Create("in-guard")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t
                .Guard(Guards.In<Unit>("in-guard.a"))
                .Target("b")))
            .State("b", s => s.On<Go>(t => t
                .Guard(Guards.In<Unit>("in-guard.a"))
                .Target("a")))
            .Build();

        var initial = machine.GetInitialState().State;
        var b = machine.Transition(initial, new Go()).State;
        Assert.True(b.Matches("b"));

        // In("a") is false now, so the transition out of b is blocked.
        var stillB = machine.Transition(b, new Go()).State;
        Assert.True(stillB.Matches("b"));
    }

    // --- 3. Entry/exit ordering ---

    [Fact]
    public void Entry_and_exit_actions_run_in_scxml_order()
    {
        var log = new List<string>();

        var machine = Machine.Create("order")
            .Initial("a")
            .State("a", s => s
                .Entry(_ => log.Add("a.entry"))
                .Exit(_ => log.Add("a.exit"))
                .Initial("a1")
                .State("a1", c => c
                    .Entry(_ => log.Add("a1.entry"))
                    .Exit(_ => log.Add("a1.exit"))
                    .On<Go>(t => t.Do(_ => log.Add("transition")).Target("#order.b.b1"))))
            .State("b", s => s
                .Entry(_ => log.Add("b.entry"))
                .Initial("b1")
                .State("b1", c => c.Entry(_ => log.Add("b1.entry"))))
            .Build();

        var initial = machine.GetInitialState();
        RunEffects(initial);
        Assert.Equal(["a.entry", "a1.entry"], log);

        log.Clear();
        var next = machine.Transition(initial.State, new Go());
        RunEffects(next);

        // Exit deepest-first, then transition actions, then entry in document order.
        Assert.Equal(["a1.exit", "a.exit", "transition", "b.entry", "b1.entry"], log);
        Assert.True(next.State.Matches("b.b1"));
    }

    [Fact]
    public void Child_transitions_take_priority_over_ancestors()
    {
        var machine = Machine.Create("priority")
            .Initial("parent")
            .State("parent", s => s
                .On<Go>(t => t.Target("fromParent"))
                .Initial("child")
                .State("child", c => c.On<Go>(t => t.Target("#priority.fromChild"))))
            .State("fromParent")
            .State("fromChild")
            .Build();

        var next = machine.Transition(machine.GetInitialState().State, new Go());
        Assert.True(next.State.Matches("fromChild"));
    }

    // --- 4. Eventless (always) transitions ---

    [Fact]
    public void Always_transitions_settle_within_one_macrostep()
    {
        var machine = Machine.Create<Counter>("always")
            .Context(_ => new Counter(0))
            .Initial("start")
            .State("start", s => s.On<Go>(t => t.Target("middle")))
            .State("middle", s => s
                .EntryAssign(a => a.Context with { Count = a.Context.Count + 1 })
                .Always(t => t.Guard(a => a.Context.Count >= 1).Target("end")))
            .State("end")
            .Build();

        var next = machine.Transition(machine.GetInitialState().State, new Go());
        Assert.True(next.State.Matches("end"));
        Assert.Equal(1, next.State.Context.Count);
    }

    [Fact]
    public void Always_cycles_throw_instead_of_hanging()
    {
        var machine = Machine.Create("loop")
            .Initial("a")
            .State("a", s => s.Always(t => t.Target("b")))
            .State("b", s => s.Always(t => t.Target("a")))
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => machine.GetInitialState());
        Assert.Contains("Infinite loop", ex.Message);
    }

    // --- 5. Raised events ---

    [Fact]
    public void Raised_events_are_processed_before_the_macrostep_ends()
    {
        var machine = Machine.Create("raise")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Raise(new Next()).Target("b")))
            .State("b", s => s.On<Next>(t => t.Target("c")))
            .State("c")
            .Build();

        var next = machine.Transition(machine.GetInitialState().State, new Go());
        Assert.True(next.State.Matches("c"));
    }

    // --- 6. Parallel states ---

    [Fact]
    public void Parallel_regions_raise_done_state_when_all_are_final()
    {
        var machine = Machine.Create("par")
            .Initial("p")
            .Parallel("p", p => p
                .State("one", r => r
                    .Initial("running")
                    .State("running", c => c.On<Finish1>(t => t.Target("done")))
                    .Final("done"))
                .State("two", r => r
                    .Initial("running")
                    .State("running", c => c.On<Finish2>(t => t.Target("done")))
                    .Final("done")))
            .State("finished")
            .Root(r => r.On("xstate.done.state.par.p", t => t.Target(".finished")))
            .Build();

        var state = machine.GetInitialState().State;
        Assert.True(state.Matches("p.one.running"));
        Assert.True(state.Matches("p.two.running"));

        var afterFirst = machine.Transition(state, new Finish1()).State;
        Assert.True(afterFirst.Matches("p.one.done"));
        Assert.True(afterFirst.Matches("p.two.running"));

        var afterSecond = machine.Transition(afterFirst, new Finish2()).State;
        Assert.True(afterSecond.Matches("finished"));
    }

    // --- 7. History ---

    [Fact]
    public void Shallow_history_restores_the_last_active_child()
    {
        var machine = Machine.Create("hist")
            .Initial("a")
            .State("a", s => s
                .Initial("a1")
                .State("a1", c => c.On<Next>(t => t.Target("a2")))
                .State("a2")
                .History("hist")
                .On<Go>(t => t.Target("b")))
            .State("b", s => s.On<Back>(t => t.Target("a.hist")))
            .Build();

        var state = machine.GetInitialState().State;
        Assert.True(state.Matches("a.a1"));

        state = machine.Transition(state, new Next()).State;
        Assert.True(state.Matches("a.a2"));

        state = machine.Transition(state, new Go()).State;
        Assert.True(state.Matches("b"));

        state = machine.Transition(state, new Back()).State;
        Assert.True(state.Matches("a.a2"));
    }

    [Fact]
    public void History_falls_back_to_its_default_target_when_nothing_was_recorded()
    {
        var machine = Machine.Create("hist-default")
            .Initial("b")
            .State("a", s => s
                .Initial("a1")
                .State("a1")
                .State("a2")
                .History("hist", HistoryType.Shallow, defaultTarget: "a2"))
            .State("b", s => s.On<Back>(t => t.Target("a.hist")))
            .Build();

        var state = machine.GetInitialState().State;
        Assert.True(state.Matches("b"));

        // `a` was never entered, so the history node resolves to its default target.
        state = machine.Transition(state, new Back()).State;
        Assert.True(state.Matches("a.a2"));
    }

    [Fact]
    public void History_without_a_default_target_falls_back_to_the_parent_initial_state()
    {
        var machine = Machine.Create("hist-initial")
            .Initial("b")
            .State("a", s => s
                .Initial("a1")
                .State("a1")
                .State("a2")
                .History("hist"))
            .State("b", s => s.On<Back>(t => t.Target("a.hist")))
            .Build();

        var state = machine.Transition(machine.GetInitialState().State, new Back()).State;
        Assert.True(state.Matches("a.a1"));
    }

    [Fact]
    public void Deep_history_restores_nested_leaves()
    {
        var machine = Machine.Create("deep")
            .Initial("a")
            .State("a", s => s
                .Initial("a1")
                .State("a1", c => c
                    .Initial("x")
                    .State("x", g => g.On<Next>(t => t.Target("y")))
                    .State("y"))
                .History("deepHist", HistoryType.Deep)
                .On<Go>(t => t.Target("b")))
            .State("b", s => s.On<Back>(t => t.Target("a.deepHist")))
            .Build();

        var state = machine.GetInitialState().State;
        state = machine.Transition(state, new Next()).State;
        Assert.True(state.Matches("a.a1.y"));

        state = machine.Transition(state, new Go()).State;
        Assert.True(state.Matches("b"));

        state = machine.Transition(state, new Back()).State;
        Assert.True(state.Matches("a.a1.y"));
    }

    // --- 8. Delayed transitions ---

    [Fact]
    public void After_emits_a_scheduled_send_on_entry_and_cancels_on_exit()
    {
        var machine = Machine.Create("timer")
            .Initial("loading")
            .State("loading", s => s.After(TimeSpan.FromMilliseconds(1000), t => t.Target("ready")))
            .State("ready")
            .Build();

        var initial = machine.GetInitialState();
        // v6 compiles `after` into a delayed raise scheduled on entry and cancelled on exit.
        var raise = Assert.IsType<RaiseEffect>(Assert.Single(initial.Effects));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), raise.Delay);
        Assert.Equal("xstate.after.1000.timer.loading", raise.Id);

        var afterEvent = Assert.IsType<AfterEvent>(raise.Event);
        Assert.Equal("timer.loading", afterEvent.StateId);

        // The host reports only that the timer elapsed; the machine releases the event itself.
        Assert.Equal(
            afterEvent,
            Assert.Single(initial.State.Timers.Values).Event);

        var next = machine.Transition(initial.State, new TimerEvent(raise.Id!));
        Assert.True(next.State.Matches("ready"));
        Assert.Contains(next.Effects, e => e is CancelEffect c && c.SendId == "xstate.after.1000.timer.loading");
    }

    // --- 9. Final states and output ---

    [Fact]
    public void Reaching_a_top_level_final_state_completes_the_machine_with_output()
    {
        var machine = Machine.Create<Counter>("final")
            .Context(_ => new Counter(7))
            .Initial("running")
            .State("running", s => s.On<Finish>(t => t.Target("done")))
            .Final("done", s => s.Output(a => a.Context.Count * 2))
            .Build();

        var next = machine.Transition(machine.GetInitialState().State, new Finish());
        Assert.Equal(SnapshotStatus.Done, next.State.Status);
        Assert.Equal(14, next.State.Output);

        // A done machine accepts nothing further: the event is dead-lettered, not delivered.
        var after = machine.Transition(next.State, new Finish());
        Assert.Equal(SnapshotStatus.Done, after.State.Status);
        Assert.Equal("stopped", Assert.IsType<DeadLetterEffect>(Assert.Single(after.Effects)).Reason);
    }

    [Fact]
    public void Nested_final_state_raises_done_state_for_its_parent()
    {
        var machine = Machine.Create("nested-final")
            .Initial("a")
            .State("a", s => s
                .Initial("working")
                .State("working", c => c.On<Finish>(t => t.Target("complete")))
                .Final("complete", c => c.Output(_ => "ok"))
                .On("xstate.done.state.nested-final.a", t => t.Target("b")))
            .State("b")
            .Build();

        var next = machine.Transition(machine.GetInitialState().State, new Finish());
        Assert.True(next.State.Matches("b"));
        Assert.Equal(SnapshotStatus.Active, next.State.Status);
    }

    // --- 10. Invocations ---

    [Fact]
    public void Invoke_spawns_on_entry_stops_on_exit_and_handles_done()
    {
        var machine = Machine.Create("inv")
            .Initial("loading")
            .State("loading", s => s.Invoke(
                new StubLogic(),
                id: "fetch",
                input: _ => "req",
                onDone: t => t.Target("ready")))
            .State("ready")
            .Build();

        var initial = machine.GetInitialState();
        var spawn = Assert.Single(initial.Effects.OfType<SpawnEffect>());

        // The child is created by the spawn and only started once the macrostep settles.
        Assert.Equal("fetch", Assert.Single(initial.Effects.OfType<StartEffect>()).Id);
        Assert.Equal("fetch", spawn.Id);
        Assert.Equal("req", spawn.Input);

        // The logic was passed by value and is registered under no source key, so the child is
        // runnable but not persistable.
        Assert.Null(spawn.Src);

        var next = machine.Transition(initial.State, new DoneActorEvent("fetch", 42, spawn.Incarnation));
        Assert.True(next.State.Matches("ready"));
        Assert.Contains(next.Effects, e => e is StopChildEffect s && s.ChildId == "fetch");

        // A done event from a different invoke id is ignored.
        var other = machine.Transition(initial.State, new DoneActorEvent("other", 42, null));
        Assert.True(other.State.Matches("loading"));
    }

    [Fact]
    public void Invoke_id_defaults_to_the_xstate_invocation_id()
    {
        var machine = Machine.Create("autoid")
            .Initial("loading")
            .State("loading", s => s.Invoke(new StubLogic()))
            .Build();

        var spawn = Assert.Single(machine.GetInitialState().Effects.OfType<SpawnEffect>());
        // xstate v6 `createInvokeId`: `${index}.${stateNodeId}`.
        Assert.Equal("0.autoid.loading", spawn.Id);
    }

    // --- 11. Descriptor specificity + targetless transitions ---

    [Fact]
    public void Exact_event_descriptors_win_over_wildcards()
    {
        var machine = Machine.Create("wildcard")
            .Initial("a")
            .State("a", s => s
                .On("*", t => t.Target("fallback"))
                .On("Ping", t => t.Target("exact")))
            .State("exact")
            .State("fallback")
            .Build();

        var state = machine.GetInitialState().State;
        Assert.True(machine.Transition(state, new Ping()).State.Matches("exact"));
        Assert.True(machine.Transition(state, new Go()).State.Matches("fallback"));
    }

    [Fact]
    public void Targetless_transitions_run_actions_without_re_entering()
    {
        var log = new List<string>();

        var machine = Machine.Create<Counter>("targetless")
            .Context(_ => new Counter(0))
            .Initial("a")
            .State("a", s => s
                .Entry(_ => log.Add("a.entry"))
                .Exit(_ => log.Add("a.exit"))
                .On<Increment>(t => t
                    .Assign(x => x.Context with { Count = x.Context.Count + x.Event.By })
                    .Do(_ => log.Add("bump"))))
            .Build();

        var initial = machine.GetInitialState();
        RunEffects(initial);
        log.Clear();

        var next = machine.Transition(initial.State, new Increment(5));
        RunEffects(next);

        Assert.Equal(["bump"], log);
        Assert.Equal(5, next.State.Context.Count);
        Assert.True(next.State.Matches("a"));
    }

    // --- 12. Self transitions ---

    [Fact]
    public void Self_transition_does_not_reenter_by_default()
    {
        var log = new List<string>();

        var machine = Machine.Create("self")
            .Initial("a")
            .State("a", s => s
                .Entry(_ => log.Add("entry"))
                .Exit(_ => log.Add("exit"))
                .On<Ping>(t => t.Target("a")))
            .Build();

        var initial = machine.GetInitialState();
        RunEffects(initial);
        log.Clear();

        var next = machine.Transition(initial.State, new Ping());
        RunEffects(next);
        Assert.Empty(log);
        Assert.True(next.State.Matches("a"));
    }

    [Fact]
    public void Self_transition_with_reenter_exits_and_reenters()
    {
        var log = new List<string>();

        var machine = Machine.Create("self")
            .Initial("a")
            .State("a", s => s
                .Entry(_ => log.Add("entry"))
                .Exit(_ => log.Add("exit"))
                .On<Ping>(t => t.Target("a").Reenter()))
            .Build();

        var initial = machine.GetInitialState();
        RunEffects(initial);
        log.Clear();

        RunEffects(machine.Transition(initial.State, new Ping()));
        Assert.Equal(["exit", "entry"], log);
    }

    // --- 13. Effect kinds ---

    [Fact]
    public void Actions_surface_as_ordered_effects()
    {
        var machine = Machine.Create("effects")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t
                .Log(_ => "hello", "label")
                .SendTo("other", _ => new Ping())
                .Raise(new Next(), delay: TimeSpan.FromSeconds(1), id: "later")
                .Target("b")))
            .State("b")
            .Build();

        var next = machine.Transition(machine.GetInitialState().State, new Go());

        Assert.Collection(
            next.Effects,
            e =>
            {
                var action = Assert.IsType<ActionEffect>(e);
                Assert.Equal("xstate.log", action.Type);
                var log = Assert.IsType<LogParams>(action.Params);
                Assert.Equal("hello", log.Message);
                Assert.Equal("label", log.Label);
            },
            e =>
            {
                var send = Assert.IsType<SendToEffect>(e);
                Assert.Equal("other", send.Target);
                Assert.IsType<Ping>(send.Event);
                Assert.Null(send.Delay);
            },
            e =>
            {
                var raise = Assert.IsType<RaiseEffect>(e);
                Assert.IsType<Next>(raise.Event);
                Assert.Equal(TimeSpan.FromSeconds(1), raise.Delay);
                Assert.Equal("later", raise.Id);
            });

        Assert.True(next.State.Matches("b"));
    }

    // --- 14. Configuration shape ---

    [Fact]
    public void Configuration_contains_ancestors_and_leaves()
    {
        var machine = Machine.Create("config")
            .Initial("a")
            .State("a", s => s.Initial("a1").State("a1"))
            .Build();

        var state = machine.GetInitialState().State;
        Assert.Equal(["config", "config.a", "config.a.a1"], state.Configuration.OrderBy(x => x));
        Assert.Equal(["config.a.a1"], state.ActiveLeafStates);
    }

    // --- 15. History used as an initial state ---

    [Fact]
    public void History_as_initial_state_without_a_default_is_a_configuration_error()
    {
        var machine = Machine.Create("h")
            .Initial("a")
            .State("a", s => s
                .Initial("hist")
                .History("hist")
                .State("one")
                .State("two"))
            .Build();

        // Without the guard this recursed until StackOverflowException.
        var error = Assert.Throws<InvalidOperationException>(() => machine.GetInitialState());
        Assert.Contains("h.a.hist", error.Message);
    }

    // --- 16. Snapshot equality ---

    [Fact]
    public void Snapshots_compare_structurally()
    {
        var machine = Machine.Create("eq")
            .Initial("a")
            .State("a", s => s.Initial("a1").State("a1").On<Toggle>(t => t.Target("b")))
            .State("b")
            .Build();

        var first = machine.GetInitialState().State;
        var second = machine.GetInitialState().State;

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        var moved = machine.Transition(first, new Toggle()).State;
        Assert.NotEqual(first, moved);

        // Returning to the same configuration produces an equal snapshot again.
        var machineWithLoop = Machine.Create("eq2")
            .Initial("a")
            .State("a", s => s.On<Toggle>(t => t.Target("b")))
            .State("b", s => s.On<Toggle>(t => t.Target("a")))
            .Build();

        var start = machineWithLoop.GetInitialState().State;
        var there = machineWithLoop.Transition(start, new Toggle()).State;
        var back = machineWithLoop.Transition(there, new Toggle()).State;

        Assert.Equal(start, back);
        Assert.Equal(start.GetHashCode(), back.GetHashCode());
    }

    // --- 17. Deterministic, document-ordered configuration ---

    [Fact]
    public void Configuration_is_in_document_order()
    {
        var machine = Machine.Create("p")
            .Initial("zone")
            .State("zone", s => s
                .AsParallel()
                .State("zebra", r => r.Initial("z1").State("z1"))
                .State("alpha", r => r.Initial("a1").State("a1"))
                .State("mid", r => r.Initial("m1").State("m1")))
            .Build();

        var state = machine.GetInitialState().State;

        string[] expected =
        [
            "p",
            "p.zone",
            "p.zone.zebra",
            "p.zone.zebra.z1",
            "p.zone.alpha",
            "p.zone.alpha.a1",
            "p.zone.mid",
            "p.zone.mid.m1"
        ];

        Assert.Equal(expected, state.Configuration);

        Assert.Equal(["p.zone.zebra.z1", "p.zone.alpha.a1", "p.zone.mid.m1"], state.ActiveLeafStates);

        // Deterministic across runs, not hash-ordered.
        Assert.Equal(expected, machine.GetInitialState().State.Configuration);
    }

    // --- 18. Unhandled error events ---

    [Fact]
    public void Unhandled_actor_error_puts_the_machine_in_the_error_state()
    {
        var machine = Machine.Create("err")
            .Initial("a")
            .State("a")
            .Build();

        var boom = new InvalidOperationException("boom");
        var next = machine.Transition(machine.GetInitialState().State, new ErrorActorEvent("child", boom, "x:1"));

        Assert.Equal(SnapshotStatus.Error, next.State.Status);
        Assert.Same(boom, next.State.Error);

        // The error path emits exactly one effect: the termination the host relays upward.
        var terminate = Assert.IsType<TerminateEffect>(Assert.Single(next.Effects));
        Assert.Equal(SnapshotStatus.Error, terminate.Status);
        Assert.Same(boom, terminate.Error);
    }

    [Fact]
    public void Handled_actor_error_transitions_normally()
    {
        var machine = Machine.Create("err")
            .Initial("a")
            .State("a", s => s.On<ErrorActorEvent>(t => t.Target("failed")))
            .State("failed")
            .Build();

        var next = machine.Transition(
            machine.GetInitialState().State,
            new ErrorActorEvent("child", new InvalidOperationException("boom"), "x:1"));

        Assert.Equal(SnapshotStatus.Active, next.State.Status);
        Assert.Null(next.State.Error);
        Assert.True(next.State.Matches("failed"));
    }

    // --- 19. Generated actor ids ---

    /// <summary>A named child machine, so generated ids for it are prefixed with its id.</summary>
    private static StateMachine<Unit> ChildMachine(string id = "child") =>
        Machine.Create(id).Initial("idle").State("idle").Build();

    [Fact]
    public void Generated_spawn_ids_are_prefixed_with_the_logic_id_and_unique_across_macrosteps()
    {
        var machine = Machine.Create("host")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Do(new SpawnAction<Unit>(ChildMachine()))))
            .Build();

        var state = machine.GetInitialState().State;
        var ids = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            var result = machine.Transition(state, new Go());
            state = result.State;
            ids.Add(Assert.Single(result.Effects.OfType<SpawnEffect>()).Id);
        }

        Assert.Equal(["child:0", "child:1", "child:2"], ids);
    }

    [Fact]
    public void Unnamed_logic_falls_back_to_the_x_prefix()
    {
        // xstate v6 `getActorIdPrefix`: only named logic earns a named prefix; everything else
        // (plain actor logic here, an anonymous machine in v6) numbers under `x`.
        var machine = Machine.Create("host")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Do(new SpawnAction<Unit>(new StubLogic()))))
            .Build();

        var state = machine.GetInitialState().State;
        var ids = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            var result = machine.Transition(state, new Go());
            state = result.State;
            ids.Add(Assert.Single(result.Effects.OfType<SpawnEffect>()).Id);
        }

        Assert.Equal(["x:0", "x:1", "x:2"], ids);
    }

    [Fact]
    public void The_anonymous_machine_placeholder_does_not_become_an_id_prefix()
    {
        var machine = Machine.Create("host")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Do(new SpawnAction<Unit>(ChildMachine("(machine)")))))
            .Build();

        var result = machine.Transition(machine.GetInitialState().State, new Go());
        Assert.Equal("x:0", Assert.Single(result.Effects.OfType<SpawnEffect>()).Id);
    }

    [Fact]
    public void Counters_are_kept_per_prefix()
    {
        // The whole point of v6's `_nextActorIds` record: two differently-named logics number
        // independently, so adding a spawn of one cannot renumber the other.
        var machine = Machine.Create("host")
            .Initial("a")
            .State("a", s => s
                .On<Go>(t => t.Do(new SpawnAction<Unit>(ChildMachine())))
                .On<Ping>(t => t.Do(new SpawnAction<Unit>(ChildMachine("worker")))))
            .Build();

        var state = machine.GetInitialState().State;
        var ids = new List<string>();

        foreach (var evt in new MachineEvent[] { new Go(), new Ping(), new Go(), new Ping() })
        {
            var result = machine.Transition(state, evt);
            state = result.State;
            ids.Add(Assert.Single(result.Effects.OfType<SpawnEffect>()).Id);
        }

        Assert.Equal(["child:0", "worker:0", "child:1", "worker:1"], ids);
    }

    [Fact]
    public void Explicit_generated_shaped_ids_reserve_their_index_for_later_generated_spawns()
    {
        // xstate v6 `reserveChildId`: registering `child:5` bumps the `child` counter to 6, so a
        // child restored from a persisted snapshot can never be shadowed by a fresh spawn.
        var machine = Machine.Create("host")
            .Initial("a")
            .State("a", s => s
                .On<Ping>(t => t.Do(new SpawnAction<Unit>(ChildMachine(), Id: "child:5")))
                .On<Go>(t => t.Do(new SpawnAction<Unit>(ChildMachine()))))
            .Build();

        var reserved = machine.Transition(machine.GetInitialState().State, new Ping());
        Assert.Equal("child:5", Assert.Single(reserved.Effects.OfType<SpawnEffect>()).Id);

        var generated = machine.Transition(reserved.State, new Go());
        Assert.Equal("child:6", Assert.Single(generated.Effects.OfType<SpawnEffect>()).Id);
    }

    [Fact]
    public void A_reservation_only_moves_its_own_prefix_and_never_rewinds_a_counter()
    {
        var machine = Machine.Create("host")
            .Initial("a")
            .State("a", s => s
                .On<Ping>(t => t.Do(new SpawnAction<Unit>(ChildMachine(), Id: "worker:9")))
                .On<Back>(t => t.Do(new StopChildAction<Unit>(_ => "child:0")))
                .On<Next>(t => t.Do(new SpawnAction<Unit>(ChildMachine(), Id: "child:0")))
                .On<Go>(t => t.Do(new SpawnAction<Unit>(ChildMachine()))))
            .Build();

        // A `worker:9` reservation must not touch the `child` counter.
        var other = machine.Transition(machine.GetInitialState().State, new Ping());
        Assert.Equal("child:0", Assert.Single(machine.Transition(other.State, new Go()).Effects.OfType<SpawnEffect>()).Id);

        // …and an explicit *low* id never rewinds a counter that has already moved past it: a
        // freed id is not handed out again by generation.
        var first = machine.Transition(machine.GetInitialState().State, new Go());
        var freed = machine.Transition(first.State, new Back());
        var low = machine.Transition(freed.State, new Next());
        Assert.Equal("child:0", Assert.Single(low.Effects.OfType<SpawnEffect>()).Id);
        Assert.Equal("child:1", Assert.Single(machine.Transition(low.State, new Go()).Effects.OfType<SpawnEffect>()).Id);
    }

    [Theory]
    // Split at the LAST ':', so a prefix may itself contain colons.
    [InlineData("a:b:3", "a:b", "a:b:4")]
    // Not generated-shaped: empty prefix, empty suffix, non-digits, negative.
    [InlineData(":3", "child", "child:0")]
    [InlineData("child:", "child", "child:0")]
    [InlineData("child:x1", "child", "child:0")]
    [InlineData("child:-1", "child", "child:0")]
    [InlineData("plain", "child", "child:0")]
    public void Only_generated_shaped_explicit_ids_reserve_an_index(
        string explicitId,
        string generatedPrefix,
        string expectedNextId)
    {
        var machine = Machine.Create("host")
            .Initial("a")
            .State("a", s => s
                .On<Ping>(t => t.Do(new SpawnAction<Unit>(ChildMachine(generatedPrefix), Id: explicitId)))
                .On<Go>(t => t.Do(new SpawnAction<Unit>(ChildMachine(generatedPrefix)))))
            .Build();

        var reserved = machine.Transition(machine.GetInitialState().State, new Ping());
        Assert.Equal(explicitId, Assert.Single(reserved.Effects.OfType<SpawnEffect>()).Id);

        var generated = machine.Transition(reserved.State, new Go());
        Assert.Equal(expectedNextId, Assert.Single(generated.Effects.OfType<SpawnEffect>()).Id);
    }

    [Fact]
    public void Spawning_onto_the_id_of_a_live_child_throws()
    {
        var machine = Machine.Create("host")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Do(new SpawnAction<Unit>(ChildMachine(), Id: "worker"))))
            .Build();

        var first = machine.Transition(machine.GetInitialState().State, new Go());
        Assert.Equal("worker", Assert.Single(first.Effects.OfType<SpawnEffect>()).Id);

        var error = Assert.Throws<InvalidOperationException>(() => machine.Transition(first.State, new Go()));
        Assert.Contains("worker", error.Message);
    }

    [Fact]
    public void An_id_stopped_earlier_in_the_same_transition_can_be_respawned()
    {
        // xstate v6 `assertChildIdFree` / `recordStoppedChild`: the restart pattern — stop a child
        // and immediately spawn its replacement under the same id, in one transition.
        var machine = Machine.Create("host")
            .Initial("a")
            .State("a", s => s
                .On<Ping>(t => t.Do(new SpawnAction<Unit>(ChildMachine(), Id: "worker")))
                .On<Go>(t => t
                    .Do(new StopChildAction<Unit>(_ => "worker"))
                    .Do(new SpawnAction<Unit>(ChildMachine(), Id: "worker"))))
            .Build();

        var started = machine.Transition(machine.GetInitialState().State, new Ping());
        var restarted = machine.Transition(started.State, new Go());

        Assert.Equal("worker", Assert.Single(restarted.Effects.OfType<StopChildEffect>()).ChildId);
        Assert.Equal("worker", Assert.Single(restarted.Effects.OfType<SpawnEffect>()).Id);
        Assert.Equal("worker", Assert.Single(restarted.State.Children).Id);
    }

    [Fact]
    public void Spawning_twice_onto_the_same_id_in_one_transition_still_throws()
    {
        var machine = Machine.Create("host")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t
                .Do(new SpawnAction<Unit>(ChildMachine(), Id: "worker"))
                .Do(new SpawnAction<Unit>(ChildMachine(), Id: "worker"))))
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => machine.Transition(machine.GetInitialState().State, new Go()));
    }

    [Fact]
    public void Restoring_a_snapshot_folds_its_children_into_the_counter_floors()
    {
        // Counters ride on the same snapshot as `Children`, so a snapshot this machine produced
        // is already consistent; the fold defends hand-constructed snapshots, where a generated
        // -shaped child id could otherwise be handed out a second time.
        var machine = Machine.Create("host")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Do(new SpawnAction<Unit>(ChildMachine()))))
            .Build();

        var handBuilt = machine.GetInitialState().State with
        {
            Children = [new ChildRecord("child:3", null, "0"), new ChildRecord("plain", null, "1")]
        };

        Assert.Equal("child:4", Assert.Single(machine.Transition(handBuilt, new Go()).Effects.OfType<SpawnEffect>()).Id);
    }

    // --- 19b. Actor addresses ---

    [Theory]
    [InlineData("child", "child")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("100%", "100%25")]
    // `%` is escaped first, so 'a/b' and 'a%2Fb' cannot encode to the same segment.
    [InlineData("a%2Fb", "a%252Fb")]
    [InlineData("x:0", "x:0")]
    public void Address_segments_are_percent_encoded_injectively(string id, string expected)
    {
        Assert.Equal(expected, ActorAddress.EncodeSegment(id));
        Assert.Equal($"host/{expected}", ActorAddress.Child("host", id));
    }

    [Fact]
    public void The_address_root_is_the_machine_id_unless_it_is_anonymous()
    {
        Assert.Equal("host", ActorAddress.Root("host"));
        Assert.Equal("x:0", ActorAddress.Root("(machine)"));
        Assert.Equal("x:0", ActorAddress.Root(""));
        Assert.Equal("x:0", ActorAddress.Root(null));
    }

    [Fact]
    public void Interpreted_actors_get_a_deterministic_address_chain()
    {
        var grandchild = Machine.Create("gc").Initial("idle").State("idle").Build();
        var child = Machine.Create("kid")
            .Initial("idle")
            .State("idle", s => s.Invoke(grandchild, id: "deep/one"))
            .Build();
        var root = Machine.Create("host")
            .Initial("a")
            .State("a", s => s.Invoke(child, id: "kid"))
            .Build();

        var interpreter = DeterministicInterpreter.Start(root);

        Assert.Equal(
            ["host", "host/kid", "host/kid/deep%2Fone"],
            interpreter.Actors.Select(a => a.Address));

        // The address is the durable identity; the session id is a per-process handle.
        Assert.All(interpreter.Actors, a => Assert.NotEqual(a.Address, a.SessionId));
    }

    // --- 20. Invoked actors spawn before entry actions ---

    [Fact]
    public void Invoked_actors_are_spawned_before_entry_actions_run()
    {
        var child = Machine.Create("kid")
            .Initial("idle")
            .State("idle", s => s.On<Ping>(t => t.Target("pinged")))
            .State("pinged")
            .Build();

        var machine = Machine.Create("parent")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Target("b")))
            .State("b", s => s
                .Invoke(child, "kid")
                .Entry(new SendToAction<Unit>(_ => "kid", _ => new Ping())))
            .Build();

        var effects = machine.Transition(machine.GetInitialState().State, new Go()).Effects;
        var spawnIndex = effects.IndexOf(effects.OfType<SpawnEffect>().Single());
        var sendIndex = effects.IndexOf(effects.OfType<SendToEffect>().Single());

        Assert.True(spawnIndex < sendIndex, "the invoked child must be spawned before the entry action sends to it");

        // The child exists by the time the entry action's send is delivered.
        var interpreter = DeterministicInterpreter.Start(machine);
        interpreter.Send(new Go());

        var childSnapshot = Assert.IsType<State<Unit>>(interpreter.SnapshotOf("kid"));
        Assert.True(childSnapshot.Matches("pinged"));
    }

    // --- 21. Teardown on machine done / stop ---

    [Fact]
    public void Reaching_a_top_level_final_cancels_timers_and_stops_children()
    {
        var machine = Machine.Create("teardown")
            .Initial("a")
            .State("a", s => s
                .Entry(new RaiseAction<Unit>(_ => new Ping(), TimeSpan.FromSeconds(5), "later"))
                .Entry(new SpawnAction<Unit>(new StubLogic()))
                .On<Go>(t => t.Target("finished")))
            .Final("finished")
            .Build();

        var initial = machine.GetInitialState();
        var spawnId = Assert.Single(initial.Effects.OfType<SpawnEffect>()).Id;

        var done = machine.Transition(initial.State, new Go());

        Assert.Equal(SnapshotStatus.Done, done.State.Status);
        Assert.Contains(done.Effects, e => e is CancelEffect { SendId: "later" });
        Assert.Contains(done.Effects, e => e is StopChildEffect stop && stop.ChildId == spawnId);
    }

    [Fact]
    public void Stopping_a_machine_tears_down_without_running_exit_actions()
    {
        var log = new List<string>();

        var machine = Machine.Create("stopped")
            .Initial("a")
            .State("a", s => s
                .Entry(new RaiseAction<Unit>(_ => new Ping(), TimeSpan.FromSeconds(5), "later"))
                .Entry(new SpawnAction<Unit>(new StubLogic(), "child"))
                .Exit(_ => log.Add("exit a"))
                .Initial("a1")
                .State("a1", c => c.Exit(_ => log.Add("exit a1"))))
            .Build();

        var stopped = machine.Stop(machine.GetInitialState().State);
        RunEffects(stopped);

        Assert.Equal(SnapshotStatus.Stopped, stopped.State.Status);

        // v6 `macrostep`'s `@xstate.stop` branch is `stopChildren` + `cancelTimers` and nothing
        // else: stopping is not an exit, so no exit action runs.
        Assert.Empty(log);
        Assert.Contains(stopped.Effects, e => e is CancelEffect { SendId: "later" });
        Assert.Contains(stopped.Effects, e => e is StopChildEffect { ChildId: "child" });

        // A stopped actor accepts nothing: every further event is dead-lettered.
        Assert.Equal(
            "stopped",
            Assert.IsType<DeadLetterEffect>(Assert.Single(machine.Stop(stopped.State).Effects)).Reason);
        Assert.IsType<DeadLetterEffect>(
            Assert.Single(machine.Transition(stopped.State, new Go()).Effects));
    }

    // --- 22. Builder regressions ---

    [Fact]
    public void Two_after_transitions_with_the_same_delay_each_get_their_own_timer()
    {
        var seen = new List<string>();

        var machine = Machine.Create("dup-delay")
            .Initial("a")
            .State("a", s => s
                .After(TimeSpan.FromSeconds(1), t => t.Do(_ => seen.Add("first")))
                .After(TimeSpan.FromSeconds(1), t => t.Do(_ => seen.Add("second"))))
            .Build();

        // Equal durations must not collapse onto one timer key.
        var interpreter = DeterministicInterpreter.Start(machine);
        Assert.Equal(
            ["xstate.after.1000.dup-delay.a", "xstate.after.1000$1.dup-delay.a"],
            interpreter.PendingSends.Select(s => s.Id));

        interpreter.AdvanceTime(TimeSpan.FromSeconds(1));
        Assert.Equal(["first", "second"], seen);
    }

    [Fact]
    public void An_explicit_id_names_one_node_without_re_rooting_its_descendants()
    {
        var machine = Machine.Create("m")
            .Initial("a")
            .State("a", s => s
                .Id("aa")
                .Initial("b")
                .State("b", c => c
                    .On<Go>(t => t.Target("#other"))
                    .On<Ping>(t => t.Guard(Guards.In<Unit>("a.b")).Target("#other"))))
            .State("other", s => s.Id("other").On<Back>(t => t.Target("#aa")))
            .Build();

        var state = machine.GetInitialState().State;

        // Descendant ids compose from the key path, not from the ancestor's explicit id.
        Assert.Equal(["m", "aa", "m.a.b"], state.Configuration);

        // The state is addressable by key path and by explicit id alike.
        Assert.True(state.Matches("a"));
        Assert.True(state.Matches("#aa"));
        Assert.True(state.Matches("a.b"));
        Assert.True(state.Matches("m.a.b"));

        // "#id" targets keep resolving, in both directions.
        var other = machine.Transition(state, new Go()).State;
        Assert.True(other.Matches("other"));
        Assert.True(machine.Transition(other, new Back()).State.Matches("a.b"));

        // In() guards work over the key path of a node under an explicitly-id'd ancestor.
        Assert.True(machine.Transition(state, new Ping()).State.Matches("other"));
    }

    [Fact]
    public void A_guard_lambda_binds_to_the_typed_overload_without_a_cast()
    {
        var machine = Machine.Create<Counter>("guard-lambda")
            .Context(_ => new Counter(100))
            .Initial("a")
            .State("a", s => s
                // Both a bare lambda (typed event) and a Guard<TContext> instance must compile.
                .On<Increment>(t => t.Guard(a => a.Context.Count + a.Event.By >= 150).Target("big"))
                .On<Increment>(t => t.Guard(a => a.Context.Count < 150).Target("small"))
                .On<Ping>(t => t.Guard(Guards.In<Counter>("guard-lambda.a")).Target("big")))
            .State("big")
            .State("small")
            .Build();

        var state = machine.GetInitialState().State;
        Assert.True(machine.Transition(state, new Increment(60)).State.Matches("big"));
        Assert.True(machine.Transition(state, new Increment(1)).State.Matches("small"));
        Assert.True(machine.Transition(state, new Ping()).State.Matches("big"));
    }

    [Fact]
    public void OnDone_takes_a_transition_when_the_states_region_finishes()
    {
        var doneStates = new List<string>();

        var machine = Machine.Create("on-done")
            .Initial("a")
            .State("a", s => s
                .Initial("x")
                .State("x", c => c.On<Next>(t => t.Target("y")))
                .Final("y")
                .OnDone(t => t.Do(a => doneStates.Add(a.Event.StateId)).Target("b")))
            .State("b")
            .Build();

        var state = machine.GetInitialState().State;
        var done = machine.Transition(state, new Next());
        RunEffects(done);

        Assert.True(done.State.Matches("b"));
        Assert.Equal(["on-done.a"], doneStates);
    }

    [Fact]
    public void Actions_see_the_real_In_predicate()
    {
        var seen = new List<bool>();

        var machine = Machine.Create<Counter>("in-actions")
            .Initial("a")
            .Context(_ => new Counter(0))
            .State("a", s => s
                // Targetless: "a" is still active while the transition's actions run.
                .On<Ping>(t => t
                    .Do(a => seen.Add(a.In("in-actions.a")))
                    .Do(a => seen.Add(a.In("b")))
                    .Assign(a => a.Context with { Count = a.In("a") ? 1 : -1 }))
                .On<Go>(t => t.Target("b")))
            .State("b", s => s.Entry(a => seen.Add(a.In("b"))))
            .Build();

        var pinged = machine.Transition(machine.GetInitialState().State, new Ping());
        RunEffects(pinged);

        Assert.Equal([true, false], seen);
        Assert.Equal(1, pinged.State.Context.Count);

        // An entering state is already part of the configuration when its entry actions run.
        seen.Clear();
        RunEffects(machine.Transition(pinged.State, new Go()));
        Assert.Equal([true], seen);
    }
}
