using System.Collections.Immutable;
using XState.TestKit;
using Xunit;

namespace XState.Tests;

public sealed record Tick : MachineEvent;

/// <summary>
/// Regressions for verified core-algorithm bugs. Each test names the defect it pins down.
/// </summary>
public class RegressionTests
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

    private static MachineEvent Ev(string type) => new NamedEvent(type);

    // --- 1. Only xstate.error.actor is fatal; IgnoreUnhandledErrors disables even that ---

    [Fact]
    public void Only_unhandled_actor_errors_are_fatal()
    {
        var machine = Machine.Create("errors")
            .Initial("a")
            .State("a")
            .Build();

        var state = machine.GetInitialState().State;

        // An actor error nobody handles still fails the machine (xstate `isErrorActorEvent`).
        var fatal = machine.Transition(state, new ErrorActorEvent("kid", new InvalidOperationException("boom"), "x:1"));
        Assert.Equal(SnapshotStatus.Error, fatal.State.Status);

        // v6 `isErrorEvent` keys off the type prefix, so *every* unhandled `xstate.error.*`
        // event is fatal — not just the actor-error one.
        var platform = machine.Transition(state, new ErrorPlatformEvent("communication", "undeliverable"));
        Assert.Equal(SnapshotStatus.Error, platform.State.Status);
        Assert.NotNull(platform.State.Error);

        var named = machine.Transition(state, new NamedEvent("xstate.error.custom"));
        Assert.Equal(SnapshotStatus.Error, named.State.Status);

        // An unhandled event outside that namespace is still simply discarded.
        var ordinary = machine.Transition(state, new NamedEvent("xstate.snapshot.actor"));
        Assert.Equal(SnapshotStatus.Active, ordinary.State.Status);
        Assert.Null(ordinary.State.Error);

        // SCXML sessions opt out of the fatal rule entirely.
        var lenient = Machine.Create("lenient")
            .IgnoreUnhandledErrors()
            .Initial("a")
            .State("a")
            .Build();

        var ignored = lenient.Transition(
            lenient.GetInitialState().State,
            new ErrorActorEvent("kid", new InvalidOperationException("boom"), "x:1"));

        Assert.Equal(SnapshotStatus.Active, ignored.State.Status);
        Assert.Null(ignored.State.Error);
    }

    // --- 2. The fatal-error path releases nothing and emits only the termination effect ---

    [Fact]
    public void Unhandled_actor_error_terminates_without_releasing_resources()
    {
        var child = Machine.Create("kidm").Initial("i").State("i").Build();

        var machine = Machine.Create("fatal")
            .Initial("a")
            .State("a", s => s
                .Invoke(child, "kid")
                .After(TimeSpan.FromSeconds(1), t => t.Target("b")))
            .State("b")
            .Build();

        var initial = machine.GetInitialState();
        var timerId = Assert.Single(initial.Effects.OfType<RaiseEffect>()).Id;

        // stateUtils.ts `macrostep`: the error path clones the *incoming* snapshot with status
        // `error` and emits nothing but the termination effect — it exits no states and releases
        // no resources. Cleanup is the host's, driven by that one effect.
        var fatal = machine.Transition(
            initial.State,
            new ErrorActorEvent("other", new InvalidOperationException("boom"), null));

        Assert.Equal(SnapshotStatus.Error, fatal.State.Status);
        Assert.True(fatal.State.Matches("a"));

        var terminate = Assert.IsType<TerminateEffect>(Assert.Single(fatal.Effects));
        Assert.Equal(SnapshotStatus.Error, terminate.Status);
        Assert.Equal("boom", terminate.Error?.Message);

        // The ledgers the host reconciles against survive on the failed snapshot.
        Assert.Equal("kid", Assert.Single(fatal.State.Children).Id);
        Assert.True(fatal.State.Timers.ContainsKey(timerId!));
    }

    // --- 3. A nested parallel region's done data survives completion in an earlier macrostep ---

    [Fact]
    public void Nested_parallel_done_data_does_not_depend_on_completion_order()
    {
        var captured = new List<object?>();

        var machine = Machine.Create("nested")
            .Initial("p")
            .Parallel("p", p => p
                .Parallel("A", a => a
                    .State("A1", r => r
                        .Initial("x")
                        .State("x", c => c
                            .On("E1", t => t.Target("f1"))
                            .On("E", t => t.Target("f1")))
                        .Final("f1", f => f.Output(_ => 1)))
                    .State("A2", r => r
                        .Initial("y")
                        .State("y", c => c
                            .On("E1", t => t.Target("f2"))
                            .On("E", t => t.Target("f2")))
                        .Final("f2", f => f.Output(_ => 2))))
                .State("B", b => b
                    .Initial("z")
                    .State("z", c => c
                        .On("E2", t => t.Target("f3"))
                        .On("E", t => t.Target("f3")))
                    .Final("f3", f => f.Output(_ => "b")))
                .OnDone(t => t
                    .Do(a => captured.Add(a.Event.Output))
                    .Target("finished")))
            .State("finished")
            .Build();

        var initial = machine.GetInitialState().State;

        // (a) The nested parallel "A" completes on its own event, one macrostep before "p" does.
        var first = machine.Transition(initial, Ev("E1"));
        RunEffects(first);
        var split = machine.Transition(first.State, Ev("E2"));
        RunEffects(split);
        Assert.True(split.State.Matches("finished"));

        // (b) Everything completes in a single microstep.
        var single = machine.Transition(initial, Ev("E"));
        RunEffects(single);
        Assert.True(single.State.Matches("finished"));

        Assert.Equal(2, captured.Count);
        Assert.Equal(Flatten(captured[1]), Flatten(captured[0]));
        Assert.Equal(((object?)1, (object?)2, (object?)"b"), Flatten(captured[0]));

        static (object? A1, object? A2, object? B) Flatten(object? output)
        {
            var top = Assert.IsType<Dictionary<string, object?>>(output);
            var a = Assert.IsType<Dictionary<string, object?>>(top["A"]);
            return (a["A1"], a["A2"], top["B"]);
        }
    }

    // --- 4. Id-less delayed sends get a generated cancellation handle ---

    [Fact]
    public void Id_less_delayed_sends_are_tracked_and_cancelled_on_teardown()
    {
        var machine = Machine.Create("anon-send")
            .Initial("a")
            .State("a", s => s
                .Entry(new RaiseAction<Unit>(_ => new Ping(), TimeSpan.FromSeconds(5)))
                .On<Go>(t => t.Target("fin")))
            .Final("fin")
            .Build();

        var initial = machine.GetInitialState();
        var scheduled = Assert.Single(initial.Effects.OfType<RaiseEffect>());

        Assert.NotNull(scheduled.Id);
        // xstate v6 `scheduleTimer` (transitionActions.ts): `xstate.timer.auto.{n}`.
        Assert.Equal("xstate.timer.auto.0", scheduled.Id);

        var done = machine.Transition(initial.State, new Go());

        Assert.Equal(SnapshotStatus.Done, done.State.Status);
        Assert.Contains(done.Effects, e => e is CancelEffect cancel && cancel.SendId == scheduled.Id);
    }

    // --- 5. A fired timer is struck off the ledger by the machine itself ---

    [Fact]
    public void Delivered_delayed_sends_are_not_cancelled_again_on_stop()
    {
        // Mirrors SCXML's `idlocation` loop: every pass schedules a send under a fresh id.
        var machine = Machine.Create<Counter>("idloc")
            .Context(_ => new Counter(0))
            .Initial("a")
            .State("a", s => s
                .Entry(new ScriptAction<Counter>(a => new ScriptResult<Counter>(
                    a.Context with { Count = a.Context.Count + 1 },
                    Effects: [new RaiseEffect(
                        new Tick(),
                        $"send-{a.Context.Count}",
                        TimeSpan.FromMilliseconds(10))])))
                .On<Tick>(t => t.Reenter().Target("a")))
            .Build();

        var interpreter = DeterministicInterpreter.Start(machine);

        for (var i = 0; i < 5; i++)
        {
            interpreter.AdvanceTime(TimeSpan.FromMilliseconds(10));
        }

        var snapshot = Assert.IsType<State<Counter>>(interpreter.RootSnapshot);
        Assert.Equal(6, snapshot.Context.Count);

        // Five sends fired and only the newest is still outstanding — the ledger must not have
        // accumulated the five delivered ids.
        var cancels = machine.Stop(snapshot).Effects.OfType<CancelEffect>().ToList();
        Assert.Equal(["send-5"], cancels.Select(c => c.SendId));
    }

    // --- 6. Duplicate spawn ids throw; duplicate delayed-send ids replace ---

    [Fact]
    public void Duplicate_actor_ids_throw_and_duplicate_send_ids_replace()
    {
        var duplicate = Machine.Create("dup")
            .Initial("idle")
            .State("idle", s => s.On<Go>(t => t.Target("p")))
            .Parallel("p", p => p
                .State("r1", r => r.Invoke(new StubLogic(), "shared"))
                .State("r2", r => r.Invoke(new StubLogic(), "shared")))
            .Build();

        var error = Assert.Throws<InvalidOperationException>(
            () => duplicate.Transition(duplicate.GetInitialState().State, new Go()));
        Assert.Contains("shared", error.Message);

        var rescheduling = Machine.Create("resend")
            .Initial("a")
            .State("a", s => s
                .Entry(new RaiseAction<Unit>(_ => new Ping(), TimeSpan.FromSeconds(5), "tick"))
                .On<Go>(t => t.Reenter().Target("a")))
            .Build();

        var initial = rescheduling.GetInitialState();
        Assert.Single(initial.Effects.OfType<RaiseEffect>());

        var again = rescheduling.Transition(initial.State, new Go());
        var cancelIndex = again.Effects.IndexOf(again.Effects.OfType<CancelEffect>().Single());
        var sendIndex = again.Effects.IndexOf(again.Effects.OfType<RaiseEffect>().Single());

        Assert.True(cancelIndex < sendIndex, "the outstanding timer must be cancelled before it is rescheduled");

        // One ledger entry, not two: stopping cancels "tick" exactly once.
        var cancels = rescheduling.Stop(again.State).Effects.OfType<CancelEffect>().ToList();
        Assert.Equal(["tick"], cancels.Select(c => c.SendId));
    }

    // --- 7. Targeting a history state whose recorded configuration is still active is a no-op ---

    [Fact]
    public void History_target_does_not_re_enter_states_that_were_never_exited()
    {
        var entered = new List<string>();
        var child = Machine.Create("kidm").Initial("i").State("i").Build();

        var machine = Machine.Create("hist")
            .Initial("p")
            .State("p", s => s
                .Initial("a1")
                .State("a1", c => c
                    .Entry(_ => entered.Add("a1"))
                    .Invoke(child, "kid")
                    .After(TimeSpan.FromSeconds(1), t => t.Target("a2"))
                    .On("TOH", t => t.Target("h"))
                    .On("OUT", t => t.Target("#hist.q")))
                .State("a2")
                .History("h"))
            .State("q", s => s.On("BACK", t => t.Target("#hist.p.h")))
            .Build();

        var state = machine.GetInitialState();
        RunEffects(state);

        // Leave and come back through history, so "a1" is both recorded and active.
        var out_ = machine.Transition(state.State, Ev("OUT"));
        RunEffects(out_);
        var back = machine.Transition(out_.State, Ev("BACK"));
        RunEffects(back);

        Assert.True(back.State.Matches("hist.p.a1"));
        Assert.Equal(["a1", "a1"], entered);
        Assert.Single(back.Effects.OfType<SpawnEffect>());

        // Targeting the sibling history node now exits nothing, so it must enter nothing either.
        var current = back.State;
        for (var i = 0; i < 2; i++)
        {
            var noop = machine.Transition(current, Ev("TOH"));
            RunEffects(noop);

            Assert.Empty(noop.Effects.OfType<SpawnEffect>());
            Assert.Empty(noop.Effects.OfType<RaiseEffect>());
            Assert.Equal(["hist", "hist.p", "hist.p.a1"], noop.State.Configuration);
            current = noop.State;
        }

        Assert.Equal(["a1", "a1"], entered);
    }

    // --- 8. A default(ImmutableArray) history slice reads as empty ---

    [Fact]
    public void Rehydrated_default_history_values_are_treated_as_empty()
    {
        var machine = Machine.Create("rehydrate")
            .Initial("p")
            .State("p", s => s
                .Initial("a1")
                .State("a1", c => c
                    .On<Go>(t => t.Target("a2"))
                    .On<Ping>(t => t.Target("h")))
                .State("a2")
                .History("h"))
            .Build();

        var initial = machine.GetInitialState().State;

        var uninitialised = initial with
        {
            HistoryValue = ImmutableDictionary<string, ImmutableArray<string>>.Empty
                .Add("rehydrate.p.h", default)
        };

        // Falls back to the history state's default target instead of throwing.
        var next = machine.Transition(uninitialised, new Ping());
        Assert.True(next.State.Matches("rehydrate.p.a1"));

        var empty = initial with
        {
            HistoryValue = ImmutableDictionary<string, ImmutableArray<string>>.Empty
                .Add("rehydrate.p.h", ImmutableArray<string>.Empty)
        };

        Assert.Equal(empty, uninitialised);

        // Recorded slices are document-ordered, so order is part of the value.
        var ab = initial with
        {
            HistoryValue = ImmutableDictionary<string, ImmutableArray<string>>.Empty
                .Add("rehydrate.p.h", ["a", "b"])
        };
        var ba = initial with
        {
            HistoryValue = ImmutableDictionary<string, ImmutableArray<string>>.Empty
                .Add("rehydrate.p.h", ["b", "a"])
        };

        Assert.NotEqual(ab, ba);
    }

    // --- 9. Reaching a top-level final state stops the entry loop ---

    [Fact]
    public void Entering_a_top_level_final_state_stops_entering_further_states()
    {
        var entered = new List<string>();

        var machine = Machine.Create("multi")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Target("fin", "b")))
            .Final("fin")
            .State("b", s => s.Entry(_ => entered.Add("b")))
            .Build();

        var done = machine.Transition(machine.GetInitialState().State, new Go());
        RunEffects(done);

        Assert.Equal(SnapshotStatus.Done, done.State.Status);
        Assert.Equal(["multi", "multi.fin"], done.State.Configuration);
        Assert.Empty(entered);
    }

    // --- 10. `OnDone` matches real DoneStateEvents, not events that merely spell the type ---

    [Fact]
    public void A_spoofed_done_state_event_does_not_select_a_typed_OnDone_transition()
    {
        var seen = new List<string>();

        var machine = Machine.Create("m")
            .Initial("p")
            .State("p", p => p
                .Initial("x")
                .State("x", x => x.On("E", t => t.Target("f")))
                .Final("f")
                // Typed on DoneStateEvent: the guard and action below cast the event.
                .OnDone(t => t
                    .Guard(a => a.Event.StateId == "m.p")
                    .Do(a => seen.Add(a.Event.StateId))
                    .Target("finished")))
            .State("finished")
            .Build();

        var initial = machine.GetInitialState().State;

        // A user-sent event whose *type* is the flattened done descriptor is just an unhandled
        // event: it must not be cast to DoneStateEvent, and must not take the transition.
        var spoofed = machine.Transition(initial, Ev("xstate.done.state.m.p"));
        RunEffects(spoofed);

        Assert.True(spoofed.State.Matches("p.x"));
        Assert.Equal(SnapshotStatus.Active, spoofed.State.Status);
        Assert.Null(spoofed.State.Error);
        Assert.Empty(seen);

        // Real region completion still fires `onDone`.
        var completed = machine.Transition(initial, Ev("E"));
        RunEffects(completed);

        Assert.True(completed.State.Matches("finished"));
        Assert.Equal(["m.p"], seen);
    }
}
