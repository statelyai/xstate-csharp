using Xunit;
using XState.Durable;

namespace XState.Tests;

/// <summary>
/// The durable-execution helper: the identity a host journals effects under, and the drive loop it
/// wraps around a pure machine.
/// </summary>
public class DurableExecutionTests
{
    /// <summary>A worker that is finished the moment it starts.</summary>
    private static StateMachine<Unit> Worker() =>
        Machine.Create("worker").Initial("done").Final("done", s => s.Output(_ => "worked")).Build();

    /// <summary>Invokes a worker, then waits a second, then completes.</summary>
    private static StateMachine<Unit> Job(IActorLogic worker) =>
        Machine.Create("job")
            .Version("1.0.0")
            .Actor("worker", worker)
            .Initial("running")
            .State("running", s => s.Invoke("worker", id: "w", onDone: t => t.Target("waiting")))
            .State("waiting", s => s.After(TimeSpan.FromSeconds(1), t => t.Target("finished")))
            .Final("finished", s => s.Output(_ => "job done"))
            .Build();

    // --- Effect identity ---

    [Fact]
    public void Effects_are_numbered_by_transition_then_position()
    {
        var machine = Machine.Create("counter")
            .Initial("idle")
            .State("idle", s => s
                .Entry(new LogAction<Unit>(_ => "entered"))
                .On<Ping>(t => t.Target("busy")))
            .State("busy", s => s
                .Entry(new LogAction<Unit>(_ => "a"))
                .Entry(new LogAction<Unit>(_ => "b")))
            .Build();

        var execution = new DurableExecution<Unit>(machine, new RecordingHost());

        Assert.Equal("counter", execution.RootAddress);
        Assert.Equal("counter", execution.MachineId);
        Assert.Equal(0, execution.NextTransitionIndex);

        var (state, first) = execution.InitialTransition();
        Assert.Equal(["0:0"], first.Select(e => e.Id));
        Assert.Equal(1, execution.NextTransitionIndex);

        var (_, second) = execution.Transition(state, new Ping());
        Assert.Equal(["1:0", "1:1"], second.Select(e => e.Id));

        // Every effect is journalable: the descriptor names its source address, never a live handle.
        Assert.All(second, e => Assert.Equal("counter", e.Source));
        Assert.Equal("action", second[0].Descriptor.Kind);
    }

    [Fact]
    public void A_resumed_execution_continues_the_journals_numbering()
    {
        var machine = Machine.Create("resumed").Initial("idle").State("idle").Build();
        var execution = new DurableExecution<Unit>(
            machine,
            new RecordingHost(),
            new DurableExecutionOptions { TransitionIndex = 7, ExecutionId = "run-1" });

        Assert.Equal(7, execution.NextTransitionIndex);
        Assert.Equal("run-1", execution.ExecutionId);

        var (_, effects) = execution.InitialTransition();
        Assert.Empty(effects);
        Assert.Equal(8, execution.NextTransitionIndex);
    }

    [Fact]
    public void A_negative_transition_index_is_refused()
    {
        var machine = Machine.Create("resumed").Initial("idle").State("idle").Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableExecution<Unit>(
            machine,
            new RecordingHost(),
            new DurableExecutionOptions { TransitionIndex = -1 }));
    }

    // --- Driving ---

    [Fact]
    public async Task Run_drives_a_machine_that_invokes_a_child_and_waits_on_a_timer()
    {
        var host = new TestHost();
        var execution = new DurableExecution<Unit>(Job(Worker()), host);
        host.Execution = execution;

        Assert.Equal("job", execution.RootAddress);
        Assert.Equal("1.0.0", execution.MachineVersion);

        var output = await execution.Run();

        Assert.Equal("job done", output);
        Assert.Equal(
            [
                // The child is created and started, and reports completion straight back — a root
                // event the execution hands out itself, so the loop never parks for it.
                "0:0 spawn w",
                "0:1 start w",
                // Leaving `running` retires the child; entering `waiting` arms its `after` timer.
                "1:0 stop w",
                "schedule xstate.after.1000.job.waiting",
                // Nothing else is outstanding, so the loop parks and the clock advances.
                "wait event:1",
                // Firing the timer leaves `waiting`, which cancels the timer the machine still
                // holds on its ledger, and enters the final state.
                "cancel xstate.after.1000.job.waiting",
                "2:1 terminate Done"
            ],
            host.Journal);
    }

    [Fact]
    public async Task Run_refuses_to_start_an_execution_that_is_not_fresh()
    {
        var host = new TestHost();
        var resumed = new DurableExecution<Unit>(
            Job(Worker()), host, new DurableExecutionOptions { TransitionIndex = 3 });

        await Assert.ThrowsAsync<DurableExecutionResumeException>(() => resumed.Run());

        // …and equally so once a fresh execution has already transitioned.
        var started = new DurableExecution<Unit>(Job(Worker()), host);
        started.InitialTransition();
        await Assert.ThrowsAsync<DurableExecutionResumeException>(() => started.Run());
    }

    [Fact]
    public async Task Run_reports_a_stopped_machine_as_a_cancellation_rather_than_an_output()
    {
        var machine = Machine.Create("stoppable").Initial("idle").State("idle").Build();
        var host = new ScriptedHost([new StopEvent()]);
        var execution = new DurableExecution<Unit>(machine, host);

        await Assert.ThrowsAsync<DurableExecutionCancelledException>(() => execution.Run());
    }

    [Fact]
    public async Task Overlapping_effect_batches_are_refused()
    {
        var machine = Machine.Create("slow")
            .Initial("idle")
            .State("idle", s => s.Entry(new LogAction<Unit>(_ => "x")))
            .Build();

        var host = new BlockingHost();
        var execution = new DurableExecution<Unit>(machine, host);
        var (_, effects) = execution.InitialTransition();

        var first = execution.ExecuteEffects(effects);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => execution.ExecuteEffects(effects));
        Assert.StartsWith("ExecuteEffects calls must not overlap", error.Message, StringComparison.Ordinal);

        host.Release();
        await first;

        // The batch released the lock, so the next one is accepted.
        await execution.ExecuteEffects(effects);
    }

    [Fact]
    public void A_stale_timer_firing_produces_no_effects()
    {
        var host = new TestHost();
        var execution = new DurableExecution<Unit>(Job(Worker()), host);
        var (state, _) = execution.InitialTransition();

        var (next, effects) = execution.Transition(state, new TimerEvent("xstate.timer.auto.99"));

        Assert.Same(state, next);
        Assert.Empty(effects);

        // The transition index still advanced: it counts transitions, not effects, so a replay
        // that sees the same events recomputes the same ids.
        Assert.Equal(2, execution.NextTransitionIndex);
    }

    [Fact]
    public async Task A_host_that_implements_nothing_fails_loudly_on_the_operation_it_is_missing()
    {
        var execution = new DurableExecution<Unit>(Job(Worker()), new BareHost());
        var (_, effects) = execution.InitialTransition();

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => execution.ExecuteEffects(effects));
        Assert.Contains(nameof(IDurableHost.SpawnActor), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_host_base_runs_actions_locally()
    {
        var ran = false;
        var machine = Machine.Create("acting")
            .Initial("idle")
            .State("idle", s => s.Entry(new CustomAction<Unit>(_ => ran = true)))
            .Build();

        var execution = new DurableExecution<Unit>(machine, new BareHost());
        var (_, effects) = execution.InitialTransition();
        await execution.ExecuteEffects(effects);

        Assert.True(ran);
    }

    // --- Hosts ---

    /// <summary>A host that only runs actions locally; every other operation keeps the default.</summary>
    private sealed class BareHost : DurableHostBase
    {
        public override ValueTask<MachineEvent> WaitForEvent(DurableWait wait, CancellationToken cancellationToken) =>
            throw new NotSupportedException("no events");
    }

    /// <summary>A host that accepts everything and remembers nothing interesting.</summary>
    private sealed class RecordingHost : IDurableHost
    {
        public ValueTask ExecuteAction(DurableEffect effect, ActionEffect action) => default;

        public ValueTask<MachineEvent> WaitForEvent(DurableWait wait, CancellationToken cancellationToken) =>
            throw new NotSupportedException("no events");
    }

    /// <summary>A host that hands out a fixed script of events.</summary>
    private sealed class ScriptedHost(IReadOnlyList<MachineEvent> events) : IDurableHost
    {
        private int _next;

        public ValueTask ExecuteAction(DurableEffect effect, ActionEffect action) => default;

        public ValueTask Terminate(DurableEffect effect, TerminateEffect terminate) => default;

        public ValueTask<MachineEvent> WaitForEvent(DurableWait wait, CancellationToken cancellationToken) =>
            new(events[_next++]);
    }

    /// <summary>A host whose action never completes until it is released.</summary>
    private sealed class BlockingHost : IDurableHost
    {
        private readonly TaskCompletionSource _gate = new();

        public void Release() => _gate.TrySetResult();

        public ValueTask ExecuteAction(DurableEffect effect, ActionEffect action) => new(_gate.Task);

        public ValueTask<MachineEvent> WaitForEvent(DurableWait wait, CancellationToken cancellationToken) =>
            throw new NotSupportedException("no events");
    }

    /// <summary>
    /// An in-memory host on a virtual clock: children run to their initial snapshot, timers fire in
    /// due order, and everything it is asked to do lands in <see cref="Journal"/>.
    /// </summary>
    private sealed class TestHost : IDurableHost
    {
        private readonly Dictionary<string, (IActorLogic Logic, object? Input, string Incarnation)> _children =
            new(StringComparer.Ordinal);

        private readonly List<(TimeSpan Due, string Id)> _timers = [];
        private TimeSpan _now;

        public DurableExecution<Unit>? Execution { get; set; }

        public List<string> Journal { get; } = [];

        public ValueTask SpawnActor(DurableEffect effect, SpawnEffect spawn)
        {
            Journal.Add($"{effect.Id} spawn {spawn.Id}");
            _children[spawn.Id] = (spawn.Logic, spawn.Input, spawn.Incarnation);
            return default;
        }

        public ValueTask StartActor(DurableEffect effect, StartEffect start)
        {
            Journal.Add($"{effect.Id} start {start.Id}");
            var child = _children[start.Id];
            var (snapshot, _) = child.Logic.GetInitialSnapshot(child.Input);

            // The parent is the root of this execution, so its child's completion is a root event.
            // The incarnation goes back verbatim: that is what lets the parent tell this child's
            // report apart from an earlier child of the same id.
            if (snapshot.Status is SnapshotStatus.Done)
            {
                Execution!.ReportRootEvent(new DoneActorEvent(start.Id, snapshot.Output, child.Incarnation));
            }

            return default;
        }

        public ValueTask StopActor(DurableEffect effect, StopChildEffect stop)
        {
            Journal.Add($"{effect.Id} stop {stop.ChildId}");
            _children.Remove(stop.ChildId);
            return default;
        }

        public ValueTask Terminate(DurableEffect effect, TerminateEffect terminate)
        {
            Journal.Add($"{effect.Id} terminate {terminate.Status}");
            return default;
        }

        public ValueTask ScheduleTimer(DurableEffect effect, string id, TimeSpan delay)
        {
            Journal.Add($"schedule {id}");
            _timers.Add((_now + delay, id));
            return default;
        }

        public ValueTask CancelTimer(DurableEffect effect, CancelEffect cancel)
        {
            var id = cancel.SendId;
            Journal.Add($"cancel {id}");
            _timers.RemoveAll(t => t.Id == id);
            return default;
        }

        public ValueTask ExecuteAction(DurableEffect effect, ActionEffect action)
        {
            Journal.Add($"{effect.Id} action");
            action.Exec();
            return default;
        }

        public ValueTask<MachineEvent> WaitForEvent(DurableWait wait, CancellationToken cancellationToken)
        {
            Journal.Add($"wait {wait.Id}");

            if (_timers.Count == 0)
            {
                throw new InvalidOperationException("The durable loop parked with nothing outstanding.");
            }

            var next = _timers.OrderBy(t => t.Due).First();
            _timers.Remove(next);
            _now = next.Due;

            return new ValueTask<MachineEvent>(new TimerEvent(next.Id));
        }
    }
}
