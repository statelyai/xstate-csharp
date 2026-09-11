using XState.TestKit;
using Xunit;

namespace XState.Tests;

/// <summary>Behavior of the deterministic, virtual-time test interpreter itself.</summary>
public class InterpreterTests
{
    private static MachineEvent Ev(string type) => new NamedEvent(type);

    [Fact]
    public void Delayed_sends_are_delivered_in_time_then_sequence_order()
    {
        var order = new List<string>();

        var machine = Machine.Create("timers")
            .Initial("a")
            .State("a", s => s
                .On("GO", t => t
                    .Raise(Ev("LATE"), TimeSpan.FromMilliseconds(200), "late")
                    .Raise(Ev("EARLY_1"), TimeSpan.FromMilliseconds(100), "early1")
                    .Raise(Ev("EARLY_2"), TimeSpan.FromMilliseconds(100), "early2"))
                .On("LATE", t => t.Do(_ => order.Add("late")))
                .On("EARLY_1", t => t.Do(_ => order.Add("early1")))
                .On("EARLY_2", t => t.Do(_ => order.Add("early2"))))
            .Build();

        var actor = DeterministicInterpreter.Start(machine);
        actor.Send(Ev("GO"));

        Assert.Equal(3, actor.PendingSends.Count);
        Assert.Equal(
            ["early1", "early2", "late"],
            actor.PendingSends.Select(s => s.Id));

        actor.AdvanceTime(TimeSpan.FromMilliseconds(150));
        Assert.Equal(["early1", "early2"], order);
        Assert.Equal(TimeSpan.FromMilliseconds(150), actor.VirtualTime);

        actor.AdvanceTime(TimeSpan.FromMilliseconds(100));
        Assert.Equal(["early1", "early2", "late"], order);
        Assert.Empty(actor.PendingSends);
    }

    [Fact]
    public void Cancelling_a_delayed_send_removes_it_from_the_schedule()
    {
        var fired = false;

        var machine = Machine.Create("cancel")
            .Initial("a")
            .State("a", s => s
                .On("GO", t => t.Raise(Ev("BOOM"), TimeSpan.FromMilliseconds(100), "boom"))
                .On("STOP", t => t.Do(new CancelAction<Unit>(_ => "boom")))
                .On("BOOM", t => t.Do(_ => fired = true)))
            .Build();

        var actor = DeterministicInterpreter.Start(machine);
        actor.Send(Ev("GO"));
        Assert.Single(actor.PendingSends);

        actor.Send(Ev("STOP"));
        Assert.Empty(actor.PendingSends);

        actor.AdvanceTime(TimeSpan.FromMilliseconds(1000));
        Assert.False(fired);
    }

    [Fact]
    public void Sibling_actors_can_address_each_other_by_id()
    {
        var childA = Machine.Create("a")
            .Initial("idle")
            .State("idle", s => s.On("PING", t => t.SendTo("b", _ => Ev("PONG"))))
            .Build();

        var childB = Machine.Create("b")
            .Initial("idle")
            .State("idle", s => s.On("PONG", t => t.Target("done")))
            .Final("done", s => s.Output(_ => "pinged"))
            .Build();

        var parent = Machine.Create("parent")
            .Initial("running")
            .State("running", s => s
                .Invoke(childA, id: "a")
                .Invoke(childB, id: "b", onDone: t => t.Target("finished"))
                .On("START", t => t.SendTo("a", _ => Ev("PING"))))
            .State("finished")
            .Build();

        var actor = DeterministicInterpreter.Start(parent);
        actor.Send(Ev("START"));

        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("finished"));
        Assert.False(actor.IsRunning("b"));
        Assert.Empty(actor.Diagnostics);
    }

    [Fact]
    public void Stopping_a_child_drops_the_timers_it_scheduled()
    {
        var child = Machine.Create("child")
            .Initial("waiting")
            .State("waiting", s => s.After(TimeSpan.FromMilliseconds(1000), t => t.Target("late")))
            .State("late")
            .Build();

        var parent = Machine.Create("parent")
            .Initial("active")
            .State("active", s => s
                .Invoke(child, id: "child")
                .On("CANCEL", t => t.Target("idle")))
            .State("idle")
            .Build();

        var actor = DeterministicInterpreter.Start(parent);
        var scheduled = Assert.Single(actor.PendingSends);
        Assert.Equal("child", scheduled.SenderId);

        actor.Send(Ev("CANCEL"));

        Assert.Empty(actor.PendingSends);
        Assert.False(actor.IsRunning("child"));

        actor.AdvanceTime(TimeSpan.FromMilliseconds(5000));
        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("idle"));
    }

    [Fact]
    public void PostTransitionEvents_are_delivered_before_queued_mailbox_events()
    {
        var seen = new List<string>();

        var machine = Machine.Create("hooked")
            .Initial("a")
            .State("a", s => s.On("*", t => t.Do(a => seen.Add(a.Event.Type))))
            .Build();

        var steps = 0;
        var actor = new DeterministicInterpreter(machine)
        {
            PostTransitionEvents = _ => ++steps == 2 ? [Ev("HOOK")] : []
        };
        actor.Start();

        actor.Enqueue(Ev("GO"));
        actor.Enqueue(Ev("MAIL"));
        actor.RunUntilQuiescent();

        // `HOOK` was produced while `MAIL` was already queued, and still went first.
        Assert.Equal(["GO", "HOOK", "MAIL"], seen);
    }

    [Fact]
    public void RunToCompletion_drives_a_self_ticking_machine_to_its_final_state()
    {
        var machine = Machine.Create<Counter>("ticker")
            .Context(_ => new Counter(0))
            .Initial("tick")
            .State("tick", s => s
                .After(TimeSpan.FromMilliseconds(100), t => t
                    .Assign(a => a.Context with { Count = a.Context.Count + 1 })
                    .Target("tick")
                    .Reenter())
                .Always(t => t.Guard(a => a.Context.Count >= 5).Target("done")))
            .Final("done", s => s.Output(a => a.Context.Count))
            .Build();

        var actor = DeterministicInterpreter.Start(machine);
        actor.RunToCompletion();

        Assert.Equal(SnapshotStatus.Done, actor.RootSnapshot.Status);
        Assert.Equal(5, actor.RootSnapshot.Output);
        Assert.Equal(TimeSpan.FromMilliseconds(500), actor.VirtualTime);
        Assert.Empty(actor.PendingSends);
    }

    [Fact]
    public void RunToCompletion_stops_at_the_virtual_time_budget()
    {
        var machine = Machine.Create<Counter>("endless")
            .Context(_ => new Counter(0))
            .Initial("tick")
            .State("tick", s => s.After(TimeSpan.FromMilliseconds(100), t => t
                .Assign(a => a.Context with { Count = a.Context.Count + 1 })
                .Target("tick")
                .Reenter()))
            .Build();

        var actor = DeterministicInterpreter.Start(machine);
        actor.RunToCompletion(TimeSpan.FromMilliseconds(250));

        Assert.Equal(SnapshotStatus.Active, actor.RootSnapshot.Status);
        Assert.Equal(2, ((State<Counter>)actor.RootSnapshot).Context.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(250), actor.VirtualTime);
        Assert.Single(actor.PendingSends);
    }

    [Fact]
    public void Log_effects_are_collected_and_unresolvable_targets_become_diagnostics()
    {
        var machine = Machine.Create("noisy")
            .Initial("a")
            .State("a", s => s.On("GO", t => t
                .Log(_ => "hello", "greeting")
                .SendTo("nobody", _ => Ev("VOID"))))
            .Build();

        var actor = DeterministicInterpreter.Start(machine);
        actor.Send(Ev("GO"));

        var entry = Assert.Single(actor.Log);
        Assert.Equal("greeting", entry.Label);
        Assert.Equal("hello", entry.Message);

        var diagnostic = Assert.Single(actor.Diagnostics);
        Assert.Contains("nobody", diagnostic);
    }

    [Fact]
    public void A_spawn_adapter_can_substitute_logic_for_a_non_machine_source()
    {
        var machine = Machine.Create("host")
            .Initial("a")
            .State("a", s => s.Invoke(new StubLogic(), id: "worker", onDone: t => t.Target("b")))
            .State("b")
            .Build();

        // Any IActorLogic runs: the contract is the pure (snapshot, effects) pair, so the stub
        // is started as-is. It never completes, so the parent stays put.
        var unadapted = DeterministicInterpreter.Start(machine);
        Assert.True(unadapted.IsRunning("worker"));
        Assert.True(((State<Unit>)unadapted.RootSnapshot).Matches("a"));

        var workerLogic = Machine.Create("worker")
            .Initial("only")
            .Final("only", s => s.Output(_ => "done"))
            .Build();

        var adapted = new DeterministicInterpreter(machine)
        {
            SpawnAdapter = spawn => spawn.Logic is StubLogic ? workerLogic : null
        };
        adapted.Start();

        Assert.Empty(adapted.Diagnostics);
        Assert.True(((State<Unit>)adapted.RootSnapshot).Matches("b"));
    }

    [Fact]
    public void A_runaway_send_loop_is_stopped_by_the_step_guard()
    {
        var machine = Machine.Create("loop")
            .Initial("a")
            .State("a", s => s.On("PING", t => t.SendTo("loop", _ => Ev("PING"))))
            .Build();

        var actor = DeterministicInterpreter.Start(machine);

        var ex = Assert.Throws<InvalidOperationException>(() => actor.Send(Ev("PING")));
        Assert.Contains("Runaway", ex.Message);
        Assert.Equal(DeterministicInterpreter.MaxProcessedEvents + 1, actor.ProcessedEvents);
    }
}
