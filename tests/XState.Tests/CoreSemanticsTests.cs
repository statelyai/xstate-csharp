using XState.Builder;
using XState.TestKit;
using Xunit;

namespace XState.Tests;

public sealed record Fetch : MachineEvent;
public sealed record Charged(int Amount) : MachineEvent;
public sealed record Approve : MachineEvent;
public sealed record Route : MachineEvent;
public sealed record SemTick : MachineEvent;

public sealed record Order(int Total = 0, string Note = "");

/// <summary>Actor logic that finishes as soon as it is sent <see cref="Finish"/>, and never on its own.</summary>
internal sealed class WaiterLogic : IMachineLogic
{
    private sealed record Snap(SnapshotStatus Status, object? Output) : ISnapshot
    {
        public Exception? Error => null;
    }

    public string Id => "waiter";
    public string? Version => null;
    public IReadOnlyDictionary<string, IActorLogic> Sources { get; } =
        new Dictionary<string, IActorLogic>(StringComparer.Ordinal);

    public (ISnapshot State, IReadOnlyList<Effect> Effects) GetInitialSnapshot(object? input) =>
        (new Snap(SnapshotStatus.Active, input), []);

    public XState.Persistence.PersistedSnapshot Persist(ISnapshot state, XState.Persistence.PersistOptions? options = null) =>
        throw new NotSupportedException();

    public XState.Persistence.IRestoreResult Restore(
        XState.Persistence.PersistedSnapshot snapshot,
        Func<XState.Persistence.PersistedSnapshot, string?, XState.Persistence.PersistedSnapshot>? migrate = null,
        Func<object?, object?>? contextConverter = null,
        string? rootAddress = null) =>
        throw new NotSupportedException();

    public (ISnapshot State, IReadOnlyList<Effect> Effects) Transition(ISnapshot state, MachineEvent evt) =>
        evt is Finish f
            ? (new Snap(SnapshotStatus.Done, f.Amount), [new TerminateEffect(SnapshotStatus.Done, f.Amount, null)])
            : (state, []);
}

/// <summary>
/// The v6-alpha semantics layered on top of the SCXML core: emit, action/guard params, meta,
/// timeouts, state input, multi-target initials, default-entry content, choice and route states,
/// state-level <c>onError</c>, <c>onSnapshot</c>, internal events and transition-domain overrides.
/// Tests are named after the xstate v6 cases they port.
/// </summary>
public class CoreSemanticsTests
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

    // ------------------------------------------------------------------
    // 1. Emit — "should emit an event from an entry action" / transition action
    // ------------------------------------------------------------------

    [Fact]
    public void Should_emit_events_from_entry_and_transition_actions_without_queueing_them()
    {
        var machine = Machine.Create("emitter")
            .Initial("idle")
            .State("idle", s => s
                // An emitted event must never come back round as an event the machine handles.
                .On<Charged>(t => t.Target("done"))
                .On<Go>(t => t.Emit(_ => new Charged(42)).Target("done")))
            .State("done", s => s.EntryEmit(new Charged(7)))
            .Build();

        var interpreter = DeterministicInterpreter.Start(machine);
        interpreter.Send(new Go());

        Assert.Equal(
            [42, 7],
            interpreter.Emitted.OfType<Charged>().Select(e => e.Amount));
        Assert.True(((State<Unit>)interpreter.RootSnapshot).Matches("emitter.done"));
    }

    // ------------------------------------------------------------------
    // 2. Params — "should call a named action with its params" / named guards
    // ------------------------------------------------------------------

    [Fact]
    public void Should_pass_params_to_named_actions_and_guards()
    {
        var seen = new List<object?>();

        var machine = Machine.Create<Order>("params")
            .Context(_ => new Order())
            .Action("track", args => seen.Add(args.Params))
            .Guard("atLeast", args => args.Context.Total >= (int)args.Params!)
            .Initial("idle")
            .State("idle", s => s
                .Entry("track", "on-entry")
                .On<Charged>(t => t
                    .Assign(a => a.Context with { Total = a.Event.Amount })
                    .Target("checked")))
            .State("checked", s => s
                .On<Go>(t => t.Guard("atLeast", 100).Target("big").Do("track", "big"))
                .On<Go>(t => t.Target("small")))
            .State("big")
            .State("small")
            .Build();

        var machineSnapshot = machine.GetInitialState();
        RunEffects(machineSnapshot);
        Assert.Equal(["on-entry"], seen);

        var charged = machine.Transition(machineSnapshot.State, new Charged(150));
        var big = machine.Transition(charged.State, new Go());
        RunEffects(big);
        Assert.True(big.State.Matches("params.big"));
        Assert.Equal(["on-entry", "big"], seen);

        var small = machine.Transition(
            machine.Transition(machineSnapshot.State, new Charged(1)).State,
            new Go());
        Assert.True(small.State.Matches("params.small"));
    }

    [Fact]
    public void Should_report_every_missing_named_implementation_at_build_time()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Machine.Create("missing")
                .Initial("a")
                .State("a", s => s
                    .Entry("track")
                    .On<Go>(t => t.Guard("isReady").Target("b").Do("notify")))
                .State("b")
                .Build());

        Assert.Contains("action 'track'", error.Message, StringComparison.Ordinal);
        Assert.Contains("guard 'isReady'", error.Message, StringComparison.Ordinal);
        Assert.Contains("action 'notify'", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 3. Meta / description / tags — "should show meta of all active states"
    // ------------------------------------------------------------------

    [Fact]
    public void Should_show_meta_of_all_active_states()
    {
        var machine = Machine.Create("light")
            .Initial("red")
            .State("red", s => s
                .Meta(new { Message = "stop" })
                .Description("The red light")
                .Tag("stop")
                .Initial("walk")
                .State("walk", w => w.Meta(new { Message = "walk" }).Tag("crosswalkLight")))
            .Build();

        var snapshot = machine.GetInitialState().State;

        // Every active node that declares meta, keyed by state id — ancestors included.
        Assert.Equal(["light.red", "light.red.walk"], snapshot.Meta.Keys.Order());
        Assert.Equal(["crosswalkLight", "stop"], snapshot.Tags.Order());
        Assert.Equal("The red light", machine.GetNodeById("light.red").Description);
        Assert.Null(machine.Root.Meta);
    }

    // ------------------------------------------------------------------
    // 4. Timeouts — "transitions via onTimeout when duration elapses" and
    //    "cancels the timeout when the state is exited by another event"
    // ------------------------------------------------------------------

    [Fact]
    public void Should_transition_via_onTimeout_when_the_duration_elapses_and_cancel_it_on_exit()
    {
        TimeoutEvent? received = null;

        var machine = Machine.Create("sla")
            .Initial("waiting")
            .State("waiting", s => s
                .Timeout(TimeSpan.FromSeconds(1))
                .OnTimeout(t => t.Target("escalated").Do(a => received = a.Event))
                .On<Approve>(t => t.Target("approved")))
            .State("escalated")
            .State("approved")
            .Build();

        var timed = new DeterministicInterpreter(machine).Start();
        timed.AdvanceTime(TimeSpan.FromSeconds(1));
        Assert.True(((State<Unit>)timed.RootSnapshot).Matches("sla.escalated"));
        Assert.Equal(new TimeoutEvent("sla.waiting"), received);

        var approved = new DeterministicInterpreter(machine).Start();
        approved.Send(new Approve());
        approved.AdvanceTime(TimeSpan.FromSeconds(5));
        Assert.True(((State<Unit>)approved.RootSnapshot).Matches("sla.approved"));
    }

    [Fact]
    public void Should_transition_via_invoke_onTimeout_and_not_when_the_invoke_completes_first()
    {
        var machine = Machine.Create("work")
            .Initial("working")
            .State("working", s => s
                .Invoke(
                    new WaiterLogic(),
                    id: "child",
                    timeout: TimeSpan.FromSeconds(1),
                    onDone: t => t.Target("finished"),
                    onTimeout: t => t.Target("timedOut")))
            .State("timedOut")
            .State("finished")
            .Build();

        var late = new DeterministicInterpreter(machine).Start();
        late.AdvanceTime(TimeSpan.FromSeconds(1));
        Assert.True(((State<Unit>)late.RootSnapshot).Matches("work.timedOut"));

        // The invoke finishing cancels the timer, so the state that handled `onDone` stays put.
        var early = new DeterministicInterpreter(machine).Start();
        early.SendTo("child", new Finish(1));
        Assert.True(((State<Unit>)early.RootSnapshot).Matches("work.finished"));
        early.AdvanceTime(TimeSpan.FromSeconds(10));
        Assert.True(((State<Unit>)early.RootSnapshot).Matches("work.finished"));
        Assert.Empty(early.PendingSends);
    }

    [Fact]
    public void Should_throw_at_construction_when_timeout_is_set_without_onTimeout()
    {
        var state = Assert.Throws<InvalidOperationException>(() =>
            Machine.Create("t").Initial("a").State("a", s => s.Timeout(TimeSpan.FromSeconds(1))).Build());
        Assert.Equal("State \"t.a\" has `timeout` but no `onTimeout` transition.", state.Message);

        var invoke = Assert.Throws<InvalidOperationException>(() =>
            Machine.Create("t")
                .Initial("a")
                .State("a", s => s.Invoke(new WaiterLogic(), id: "c", timeout: TimeSpan.FromSeconds(1)))
                .Build());
        Assert.Equal("Invoke on state \"t.a\" has `timeout` but no `onTimeout` transition.", invoke.Message);
    }

    // ------------------------------------------------------------------
    // 5. State input — "transition should pass input to target state",
    //    "initial transition should accept input", "entry/exit/output receive input"
    // ------------------------------------------------------------------

    [Fact]
    public void Should_pass_state_input_to_entry_exit_invoke_and_output()
    {
        object? entryInput = null;
        object? exitInput = null;
        object? invokeInput = null;

        var machine = Machine.Create<Order>("input")
            .Context(_ => new Order())
            .Initial(t => t.Target("idle").Input(new Order(Note: "initial")))
            .State("idle", s => s
                .Entry(a => entryInput = a.Input)
                .On<Fetch>(t => t
                    .Target("loading")
                    .Input(a => new Order(Total: a.Context.Total, Note: "fetched"))))
            .State("loading", s => s
                .Entry(a => entryInput = a.Input)
                .Exit(a => exitInput = a.Input)
                .Invoke(new WaiterLogic(), id: "child", input: a => invokeInput = a.Input, onDone: t => t.Target("done")))
            .Final("done", s => s.Output(a => a.Input))
            .Build();

        var initial = machine.GetInitialState();
        RunEffects(initial);
        Assert.Equal(new Order(Note: "initial"), entryInput);
        Assert.Equal(new Order(Note: "initial"), initial.State.StateInputs["input.idle"]);

        var loading = machine.Transition(initial.State, new Fetch());
        RunEffects(loading);
        Assert.Equal(new Order(Note: "fetched"), entryInput);
        Assert.Equal(new Order(Note: "fetched"), invokeInput);

        // Both entries live on: v6 never prunes `_stateInputs`.
        Assert.Equal(
            ["input.idle", "input.loading"],
            loading.State.StateInputs.Keys.Order());

        // The exiting state still sees the input it was entered with.
        var done = machine.Transition(loading.State, new DoneActorEvent("child", null, "0"));
        RunEffects(done);
        Assert.Equal(new Order(Note: "fetched"), exitInput);
    }

    // ------------------------------------------------------------------
    // 6. Multiple initial targets and #id targets — "deep initial across regions"
    // ------------------------------------------------------------------

    [Fact]
    public void Should_enter_every_target_of_a_multi_target_initial()
    {
        var machine = Machine.Create("deep")
            .Initial("active")
            .State("active", s => s
                .Initial("regions.left.ready", "#rightDone")
                .Parallel("regions", r => r
                    .State("left", l => l.Initial("idle").State("idle").State("ready"))
                    .State("right", n => n
                        .Initial("idle")
                        .State("idle")
                        .State("done", d => d.Id("rightDone")))))
            .Build();

        var snapshot = machine.GetInitialState().State;

        Assert.Contains("deep.active.regions.left.ready", snapshot.Configuration);
        Assert.Contains("rightDone", snapshot.Configuration);
        Assert.DoesNotContain("deep.active.regions.left.idle", snapshot.Configuration);
        Assert.DoesNotContain("deep.active.regions.right.idle", snapshot.Configuration);
    }

    // ------------------------------------------------------------------
    // 7. Initial / history default transition content — "should execute actions of the
    //    initial transition" and "…only on default entry"
    // ------------------------------------------------------------------

    [Fact]
    public void Should_run_initial_and_history_default_content_only_on_default_entry()
    {
        var log = new List<string>();

        // `away` is the initial state, so `outer` can be reached three different ways without its
        // history ever having been recorded.
        var machine = Machine.Create("defaults")
            .Initial("away")
            .State("away", a => a
                .On<Ping>(t => t.Target("outer"))
                .On<Go>(t => t.Target("outer.second"))
                .On<Next>(t => t.Target("outer.hist")))
            .State("outer", s => s
                .Initial(t => t.Target("first").Do(_ => log.Add("initial")))
                .History("hist", HistoryType.Shallow, "second", h => h.Do(_ => log.Add("historyDefault")))
                .State("first")
                .State("second"))
            .Build();

        var initial = machine.GetInitialState();
        RunEffects(initial);
        Assert.Empty(log);

        // Entering the compound itself is a default entry: its initial transition's content runs.
        log.Clear();
        var byDefault = machine.Transition(initial.State, new Ping());
        RunEffects(byDefault);
        Assert.True(byDefault.State.Matches("defaults.outer.first"));
        Assert.Equal(["initial"], log);

        // Targeting a descendant directly is not a default entry, so the content does not run.
        log.Clear();
        var direct = machine.Transition(initial.State, new Go());
        RunEffects(direct);
        Assert.True(direct.State.Matches("defaults.outer.second"));
        Assert.Empty(log);

        // A history state with nothing recorded takes its default transition. xstate v6 puts the
        // history node's *parent* into `statesForDefaultEntry` (stateUtils.ts:1744) and keys the
        // default's content by that parent, so the parent's own initial content runs first and the
        // history default's second — even though the initial target itself is not entered.
        log.Clear();
        var restored = machine.Transition(initial.State, new Next());
        RunEffects(restored);
        Assert.True(restored.State.Matches("defaults.outer.second"));
        Assert.Equal(["initial", "historyDefault"], log);
    }

    // ------------------------------------------------------------------
    // 8. Choice states — "should transition to the resolved target on entry"
    // ------------------------------------------------------------------

    [Fact]
    public void Should_resolve_a_choice_state_on_entry_and_never_rest_in_it()
    {
        var machine = Machine.Create<Order>("routing")
            .Context(_ => new Order(Total: 150))
            .Initial("decide")
            .State("decide", s => s.Choice(a => new ChoiceResult(a.Context.Total >= 100 ? "big" : "small")))
            .State("big")
            .State("small")
            .Build();

        var snapshot = machine.GetInitialState().State;
        Assert.True(snapshot.Matches("routing.big"));
        Assert.DoesNotContain("routing.decide", snapshot.Configuration);
    }

    [Fact]
    public void Should_reject_a_choice_state_that_declares_state_content()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Machine.Create("c")
                .Initial("pick")
                .State("pick", s => s.Choice(_ => new ChoiceResult("a")).Entry(_ => { }).On<Go>())
                .State("a")
                .Build());

        Assert.Contains("may not declare transitions, entry actions", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 9. Route states — "routes to a state by id" / "leaves the snapshot unchanged when
    //    no route matches" / "narrows to one parallel region"
    // ------------------------------------------------------------------

    [Fact]
    public void Should_route_to_a_state_by_id_and_ignore_an_unmatched_route()
    {
        var machine = Machine.Create("app")
            .Initial("home")
            .State("home", s => s.Id("home").Route())
            .State("dashboard", s => s
                .Id("dashboard")
                .Route(t => t.Input(new Order(Note: "routed")))
                .Initial("overview")
                .State("overview"))
            .State("secret", s => s.Id("secret").Route(t => t.Guard(_ => false)))
            .State("hidden")
            .Build();

        var initial = machine.GetInitialState().State;

        var routed = machine.Transition(initial, new RouteEvent("#dashboard")).State;
        Assert.True(routed.Matches("#dashboard"));
        Assert.True(routed.Matches("app.dashboard.overview"));
        // An explicit `.Id(...)` *is* the node's canonical id, so that is the `_stateInputs` key.
        Assert.Equal(new Order(Note: "routed"), routed.StateInputs["dashboard"]);

        // A state without `route`, an unknown id, and a route whose guard refuses are all simply
        // unhandled: the macrostep settles on an equal snapshot (a fresh instance, since only a
        // boundary rejection hands back the very same object).
        Assert.Equal(routed, machine.Transition(routed, new RouteEvent("#hidden")).State);
        Assert.Equal(routed, machine.Transition(routed, new RouteEvent("#nope")).State);
        Assert.Equal(routed, machine.Transition(routed, new RouteEvent("#secret")).State);

        Assert.True(machine.Transition(routed, new RouteEvent("#home")).State.Matches("app.home"));
    }

    // ------------------------------------------------------------------
    // 10. State-level onError — "state onError catches invoked actor errors"
    // ------------------------------------------------------------------

    [Fact]
    public void Should_catch_actor_errors_with_a_state_level_onError()
    {
        var machine = Machine.Create("errors")
            .Initial("working")
            .State("working", s => s
                .Invoke(new WaiterLogic(), id: "plain")
                .Invoke(new WaiterLogic(), id: "handled", onError: t => t.Target("handledError"))
                .OnError(t => t.Target("caught")))
            .State("caught")
            .State("handledError")
            .Build();

        var initial = machine.GetInitialState().State;

        // An invoke's own onError matches exactly and outranks the state-level wildcard.
        Assert.True(machine
            .Transition(initial, new ErrorActorEvent("handled", new InvalidOperationException("x"), "1"))
            .State.Matches("errors.handledError"));

        // Everything else in the `xstate.error.*` family lands on the state-level handler.
        Assert.True(machine
            .Transition(initial, new ErrorActorEvent("plain", new InvalidOperationException("x"), "0"))
            .State.Matches("errors.caught"));
        Assert.True(machine
            .Transition(initial, new ErrorPlatformEvent("communication"))
            .State.Matches("errors.caught"));
    }

    // ------------------------------------------------------------------
    // 11. Invoke onSnapshot — "should receive the child's snapshots"
    // ------------------------------------------------------------------

    [Fact]
    public void Should_deliver_child_snapshots_to_an_invoke_with_onSnapshot()
    {
        var child = Machine.Create<Counter>("counter")
            .Context(_ => new Counter(0))
            .Initial("idle")
            .State("idle", s => s.On<Increment>(t => t.Assign(a => a.Context with { Count = a.Context.Count + a.Event.By })))
            .Build();

        var parent = Machine.Create<Counter>("parent")
            .Context(_ => new Counter(0))
            .Initial("watching")
            .State("watching", s => s
                .Invoke(
                    child,
                    id: "child",
                    onSnapshot: t => t.Assign(a =>
                        a.Context with { Count = ((State<Counter>)a.Event.Snapshot!).Context.Count })))
            .Build();

        var interpreter = DeterministicInterpreter.Start(parent);

        // The child's initial snapshot is relayed too.
        Assert.Equal(0, ((State<Counter>)interpreter.RootSnapshot).Context.Count);

        interpreter.SendTo("child", new Increment(5));
        Assert.Equal(5, ((State<Counter>)interpreter.RootSnapshot).Context.Count);

        interpreter.SendTo("child", new Increment(2));
        Assert.Equal(7, ((State<Counter>)interpreter.RootSnapshot).Context.Count);
    }

    [Fact]
    public void Should_drop_a_snapshot_from_a_previous_incarnation_of_a_child()
    {
        var parent = Machine.Create<Counter>("gate")
            .Context(_ => new Counter(0))
            .Initial("watching")
            .State("watching", s => s
                .Invoke(new WaiterLogic(), id: "child",
                    onSnapshot: t => t.Assign(a => a.Context with { Count = a.Context.Count + 1 })))
            .Build();

        var initial = parent.GetInitialState().State;
        Assert.Equal("0", initial.Children.Single(c => c.Id == "child").Incarnation);

        Assert.Equal(1, parent.Transition(initial, new SnapshotEvent("child", null, "0")).State.Context.Count);
        Assert.Equal(0, parent.Transition(initial, new SnapshotEvent("child", null, "9")).State.Context.Count);
    }

    // ------------------------------------------------------------------
    // 12. Internal events — "rejects an internal event sent from outside"
    // ------------------------------------------------------------------

    [Fact]
    public void Should_reject_an_internal_event_from_outside_but_still_deliver_it_when_raised()
    {
        var machine = Machine.Create("internal")
            .InternalEvents("SemTick")
            .Initial("idle")
            .State("idle", s => s
                .On<Go>(t => t.Raise(new SemTick()))
                .On<SemTick>(t => t.Target("ticked")))
            .State("ticked")
            .Build();

        var initial = machine.GetInitialState().State;

        var rejected = machine.Transition(initial, new SemTick());
        Assert.Same(initial, rejected.State);
        var dead = Assert.IsType<DeadLetterEffect>(Assert.Single(rejected.Effects));
        Assert.Equal("internalEvent", dead.Reason);

        // Raised from inside, the same type is delivered as usual.
        Assert.True(machine.Transition(initial, new Go()).State.Matches("internal.ticked"));
    }

    // ------------------------------------------------------------------
    // 13. Transition domain override — "internal keeps the source, external re-enters it"
    // ------------------------------------------------------------------

    [Fact]
    public void Should_honor_an_explicit_transition_domain()
    {
        var log = new List<string>();

        StateMachine<Unit> Build(Action<TransitionBuilder<Unit, Go>> domain) =>
            Machine.Create("domain")
                .Initial("outer")
                .State("outer", s => s
                    .Entry(_ => log.Add("enter outer"))
                    .Exit(_ => log.Add("exit outer"))
                    .Initial("a")
                    .State("a", a => a.On<Go>(t =>
                    {
                        t.Target("#outerB");
                        domain(t);
                    }))
                    .State("b", b => b.Id("outerB")))
                .Build();

        // `internal`: the source's own compound ancestor is the domain, so it is not re-entered.
        var internalMachine = Build(t => t.Internal());
        log.Clear();
        RunEffects(internalMachine.Transition(internalMachine.GetInitialState().State, new Go()));
        Assert.DoesNotContain("exit outer", log);

        // `external`: the LCCA is computed without the "targets are inside the source" shortcut,
        // and here that is still `outer` — the transition's own source is `a`, so `outer` survives.
        // Making the source the compound itself is what shows the difference.
        var externalMachine = Machine.Create("external")
            .Initial("outer")
            .State("outer", s => s
                .Entry(_ => log.Add("enter outer"))
                .Exit(_ => log.Add("exit outer"))
                .Initial("a")
                .State("a")
                .State("b", b => b.Id("externalB"))
                .On<Go>(t => t.Target("#externalB").External()))
            .Build();

        log.Clear();
        RunEffects(externalMachine.Transition(externalMachine.GetInitialState().State, new Go()));
        Assert.Equal(["exit outer", "enter outer"], log.Where(l => l.EndsWith("outer", StringComparison.Ordinal)));
    }
}
