using XState.Builder;
using Xunit;

namespace XState.Tests;

public sealed record Expire : MachineEvent;

public sealed record Internal : MachineEvent;

/// <summary>Pure transition semantics synced with xstate v6 beta.</summary>
public class BetaSyncTransitionTests
{
    private static TimerEvent Fire<T>(State<T> state) => new(Assert.Single(state.Timers).Key);

    [Fact]
    public void An_after_transition_is_taken_before_an_always_guard_is_reevaluated()
    {
        var ready = false;
        var machine = Machine.Create("after-first")
            .Initial("waiting")
            .State("waiting", s => s
                .Always(t => t.Guard(_ => ready).Target("early"))
                .After(TimeSpan.FromMilliseconds(10), t => t.Target("elapsed")))
            .State("early")
            .State("elapsed")
            .Build();

        var initial = machine.GetInitialState().State;
        ready = true;

        Assert.True(machine.Transition(initial, Fire(initial)).State.Matches("elapsed"));
    }

    [Fact]
    public void A_state_timeout_is_handled_before_always_is_reevaluated()
    {
        var ready = false;
        var machine = Machine.Create("timeout-first")
            .Initial("waiting")
            .State("waiting", s => s
                .Always(t => t.Guard(_ => ready).Target("early"))
                .Timeout(TimeSpan.FromMilliseconds(10))
                .OnTimeout(t => t.Target("elapsed")))
            .State("early")
            .State("elapsed")
            .Build();

        var initial = machine.GetInitialState().State;
        ready = true;

        Assert.True(machine.Transition(initial, Fire(initial)).State.Matches("elapsed"));
    }

    [Fact]
    public void A_delayed_raise_is_handled_before_always_and_settles_before_raised_events()
    {
        var ready = false;
        var machine = Machine.Create("raise-first")
            .Initial("waiting")
            .State("waiting", s => s
                .Entry(new RaiseAction<Unit>(_ => new Expire(), TimeSpan.FromMilliseconds(10)))
                .Always(t => t.Guard(_ => ready).Target("early"))
                .On<Expire>(t => t.Target("elapsed")))
            .State("early")
            .State("elapsed", s => s
                .Entry(new RaiseAction<Unit>(_ => new Internal()))
                .Always(t => t.Guard(a => a.Event is Expire).Target("settled"))
                .On<Internal>(t => t.Target("early")))
            .State("settled")
            .Build();

        var initial = machine.GetInitialState().State;
        ready = true;

        Assert.True(machine.Transition(initial, Fire(initial)).State.Matches("settled"));
    }

    [Fact]
    public void Always_is_reevaluated_after_an_unhandled_delayed_raise()
    {
        var machine = Machine.Create("unhandled")
            .Initial("waiting")
            .State("waiting", s => s
                .Entry(new RaiseAction<Unit>(_ => new Ping(), TimeSpan.FromMilliseconds(10)))
                .Always(t => t.Guard(a => a.Event is Ping).Target("settled")))
            .State("settled")
            .Build();

        var initial = machine.GetInitialState().State;

        Assert.True(machine.Transition(initial, Fire(initial)).State.Matches("settled"));
    }

    [Fact]
    public void A_delayed_send_does_not_reevaluate_the_sender_always_transitions()
    {
        var ready = false;
        var machine = Machine.Create("send-first")
            .Initial("waiting")
            .State("waiting", s => s
                .Entry(new SendToAction<Unit>(_ => "child", _ => new Ping(), TimeSpan.FromMilliseconds(10)))
                .Always(t => t.Guard(_ => ready).Target("early")))
            .State("early")
            .Build();

        var initial = machine.GetInitialState().State;
        ready = true;

        var fired = machine.Transition(initial, Fire(initial));

        Assert.True(fired.State.Matches("waiting"));
        Assert.IsType<Ping>(Assert.Single(fired.Effects.OfType<SendToEffect>()).Event);
    }

    [Fact]
    public void A_targetless_reenter_transition_runs_no_exit_or_entry()
    {
        var machine = Machine.Create<Counter>("targetless")
            .Context(_ => new Counter(0))
            .Initial("a")
            .State("a", s => s
                .Entry(new LogAction<Counter>(_ => "entry a"))
                .Exit(new LogAction<Counter>(_ => "exit a"))
                .On<Go>(t => t.Reenter().Assign(a => a.Context with { Count = 1 })))
            .Build();

        var initial = machine.GetInitialState().State;
        Assert.Contains(machine.GetInitialState().Effects, e => e is ActionEffect { Params: LogParams });
        var result = machine.Transition(initial, new Go());

        Assert.True(result.State.Matches("a"));
        Assert.Equal(1, result.State.Context.Count);
        Assert.DoesNotContain(result.Effects, e => e is ActionEffect { Params: LogParams });
    }
}
