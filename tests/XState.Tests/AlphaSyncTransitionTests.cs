using XState.Builder;
using Xunit;

namespace XState.Tests;

public sealed record SyncPong : MachineEvent;

/// <summary>Pure transition semantics synced with xstate v6 alpha.71.</summary>
public class AlphaSyncTransitionTests
{
    private static TransitionResult<T> Run<T>(TransitionResult<T> result)
    {
        foreach (var action in result.Effects.OfType<ActionEffect>())
        {
            action.Exec();
        }

        return result;
    }

    [Fact]
    public void After_delay_sees_context_assigned_by_entry()
    {
        var machine = Machine.Create<Counter>("delay")
            .Context(_ => new Counter(0))
            .Initial("idle")
            .State("idle", s => s.On<Go>(t => t.Target("waiting")))
            .State("waiting", s => s
                .Entry(new AssignAction<Counter>(a => a.Context with { Count = 50 }))
                .After(new DelayRef<Counter>(a => TimeSpan.FromMilliseconds(a.Context.Count)), t => t.Target("idle")))
            .Build();

        var next = machine.Transition(machine.GetInitialState().State, new Go());

        var raise = Assert.Single(next.Effects.OfType<RaiseEffect>());
        Assert.Equal(TimeSpan.FromMilliseconds(50), raise.Delay);
    }

    private static StateMachine<Unit> InputMachine(List<object?> exitInputs) =>
        Machine.Create("in")
            .Initial("a")
            .State("a", s => s
                .On<Go>(t => t.Target("b").Input("for-b"))
                .On<Ping>(t => t.Target("a").Input("ignored")))
            .State("b", s => s
                .Exit(a => exitInputs.Add(a.Input))
                .On<Go>(t => t.Target("a"))
                .On<Ping>(t => t.Target("b").Input("ignored"))
                .On<SyncPong>(t => t.Target("b").Reenter()))
            .Build();

    [Fact]
    public void Exited_state_input_is_removed_and_exit_sees_entered_input()
    {
        var exits = new List<object?>();
        var machine = InputMachine(exits);
        var b = machine.Transition(machine.GetInitialState().State, new Go());
        Assert.Equal("for-b", b.State.StateInputs["in.b"]);

        var a = Run(machine.Transition(b.State, new Go()));
        Assert.False(a.State.StateInputs.ContainsKey("in.b"));
        Assert.Equal(["for-b"], exits);
    }

    [Fact]
    public void Non_reentering_transition_onto_active_target_keeps_its_input()
    {
        var machine = InputMachine([]);
        var b = machine.Transition(machine.GetInitialState().State, new Go());

        var same = machine.Transition(b.State, new Ping());
        Assert.Equal("for-b", same.State.StateInputs["in.b"]);
    }

    [Fact]
    public void Reentry_without_input_clears_the_stale_input()
    {
        var exits = new List<object?>();
        var machine = InputMachine(exits);
        var b = machine.Transition(machine.GetInitialState().State, new Go());

        var reentered = Run(machine.Transition(b.State, new SyncPong()));
        Assert.False(reentered.State.StateInputs.ContainsKey("in.b"));
        Assert.Equal(["for-b"], exits);
    }

    [Fact]
    public void Previous_snapshot_context_is_not_mutated_by_entry_assign()
    {
        var machine = Machine.Create<Counter>("ctx")
            .Context(_ => new Counter(1))
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Target("b")))
            .State("b", s => s.Entry(new AssignAction<Counter>(a => a.Context with { Count = 2 })))
            .Build();

        var initial = machine.GetInitialState().State;
        var before = initial.Context;
        var next = machine.Transition(initial, new Go());

        Assert.Equal(2, next.State.Context.Count);
        Assert.Same(before, initial.Context);
        Assert.Equal(1, initial.Context.Count);
    }

    [Fact]
    public void Entry_spawned_and_stopped_children_survive_a_later_invoke_in_the_same_microstep()
    {
        var machine = Machine.Create("host")
            .Initial("idle")
            .State("idle", s => s.On<Go>(t => t.Target("A")))
            .State("A", s => s
                .Entry(new SpawnAction<Unit>(new WaiterLogic(), Id: "w"))
                .Entry(new SpawnAction<Unit>(new WaiterLogic(), Id: "gone"))
                .Entry(new StopChildAction<Unit>(_ => "gone"))
                .Initial("B")
                .State("B", b => b.Invoke(new WaiterLogic(), id: "inv")))
            .Build();

        var next = machine.Transition(machine.GetInitialState().State, new Go());

        Assert.Equal(["inv", "w"], next.State.Children.Select(c => c.Id).Order());
    }
}
