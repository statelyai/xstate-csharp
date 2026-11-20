using System.Text.Json;
using XState.Durable;
using XState.Persistence;
using Xunit;

namespace XState.Tests;

/// <summary>
/// Durable checkpoints: the persisted snapshot, the next transition index and the inbox of root
/// events received but not yet processed. Ported from xstate v6
/// <c>packages/core/test/durable-checkpoint.test.ts</c> (c558afa20c).
/// </summary>
public class DurableCheckpointTests
{
    private static readonly RestoreOptions<Unit> UnitContext = new() { ContextConverter = _ => Unit.Value };

    private static readonly PersistOptions ByAddress = new() { EmbedChildren = false };

    private static MachineEvent Ev(string type) => new NamedEvent(type);

    private static StateMachine<Unit> Worker() =>
        Machine.Create("worker")
            .Initial("waiting")
            .State("waiting", s => s.On("WORK", t => t.Target("finished")))
            .Final("finished", s => s.Output(_ => "worked"))
            .Build();

    private static StateMachine<Unit> Parent() =>
        Machine.Create("parent")
            .Actor("worker", Worker())
            .Initial("working")
            .State("working", s => s
                .Invoke("worker", id: "worker", onDone: t => t.Target("success"))
                .On("GO", t => t.SendTo("worker", _ => Ev("WORK")))
                .On("RESTART", t => t.Target("working").Reenter())
                .On("EXIT", t => t.Target("idle")))
            .State("success")
            .State("idle")
            .Build();

    private static DurableCheckpoint RoundTrip(DurableCheckpoint checkpoint) =>
        JsonSerializer.Deserialize<DurableCheckpoint>(
            JsonSerializer.Serialize(checkpoint, SnapshotJson.DefaultOptions()),
            SnapshotJson.DefaultOptions())!;

    /// <summary>The worker has completed, and its completion is pending in the execution.</summary>
    private static async Task<(DurableExecution<Unit> Execution, CheckpointHost Host, State<Unit> State)> CompletedChild()
    {
        var host = new CheckpointHost();
        var execution = new DurableExecution<Unit>(Parent(), host);
        var (state, effects) = execution.InitialTransition();
        await execution.ExecuteEffects(effects);
        (state, effects) = execution.Transition(state, Ev("GO"));
        await execution.ExecuteEffects(effects);
        return (execution, host, state);
    }

    [Fact]
    public async Task A_child_completion_reported_before_the_checkpoint_resumes_after_restore()
    {
        var (execution, _, state) = await CompletedChild();

        var checkpoint = RoundTrip(execution.Checkpoint(state, ByAddress));
        Assert.Equal(2, checkpoint.NextTransitionIndex);
        var entry = Assert.Single(checkpoint.Inbox);
        var done = Assert.IsType<DoneActorEvent>(entry.Event);
        Assert.Equal("worker", done.ActorId);
        Assert.Equal(new SerializedActorRef("parent/worker"), entry.Source);

        var host = new CheckpointHost();
        var resumed = new DurableExecution<Unit>(Parent(), host);
        var (restored, effects, children) = resumed.Restore(checkpoint, UnitContext);
        Assert.Equal(2, resumed.NextTransitionIndex);
        Assert.Equal("worker", Assert.Single(children).Id);

        await resumed.ExecuteEffects(effects);
        var evt = await resumed.WaitForEvent();
        Assert.IsType<DoneActorEvent>(evt);
        Assert.Empty(host.Waits);

        (restored, _) = resumed.Transition(restored, evt);
        Assert.True(restored.Matches("success"));
    }

    [Fact]
    public async Task The_inbox_keeps_report_order()
    {
        var host = new CheckpointHost { OnAction = e => e.ReportRootEvent(new NamedEvent("INC", 1)) };
        host.OnAction += e => e.ReportRootEvent(new NamedEvent("INC", 10));
        var machine = Machine.Create("counter")
            .Initial("idle")
            .State("idle", s => s.On("GO", t => t.Do(_ => { })))
            .Build();
        var execution = new DurableExecution<Unit>(machine, host);
        var (state, effects) = execution.InitialTransition();
        await execution.ExecuteEffects(effects);
        (state, effects) = execution.Transition(state, Ev("GO"));
        await execution.ExecuteEffects(effects);

        var checkpoint = RoundTrip(execution.Checkpoint(state));
        Assert.Equal([1L, 10L], checkpoint.Inbox.Select(e => Convert.ToInt64(((NamedEvent)e.Event).Data)));

        var resumedHost = new CheckpointHost();
        var resumed = new DurableExecution<Unit>(machine, resumedHost);
        var (_, restoreEffects, _) = resumed.Restore(checkpoint, UnitContext);
        await resumed.ExecuteEffects(restoreEffects);

        Assert.Equal(1L, Convert.ToInt64(((NamedEvent)await resumed.WaitForEvent()).Data));
        Assert.Equal(10L, Convert.ToInt64(((NamedEvent)await resumed.WaitForEvent()).Data));
        Assert.Equal("PING", (await resumed.WaitForEvent()).Type);
        Assert.Equal(["event:1"], resumedHost.Waits);
    }

    [Fact]
    public async Task Events_already_taken_by_WaitForEvent_are_left_out()
    {
        var (execution, _, state) = await CompletedChild();
        await execution.WaitForEvent();

        Assert.Empty(execution.Checkpoint(state, ByAddress).Inbox);
    }

    [Fact]
    public async Task The_completion_of_a_child_a_later_transition_replaced_is_dropped()
    {
        var (execution, _, state) = await CompletedChild();

        var (replacement, effects) = execution.Transition(state, Ev("RESTART"));
        await execution.ExecuteEffects(effects);
        Assert.Empty(execution.Checkpoint(replacement, ByAddress).Inbox);

        (replacement, effects) = execution.Transition(replacement, Ev("GO"));
        await execution.ExecuteEffects(effects);
        var checkpoint = RoundTrip(execution.Checkpoint(replacement, ByAddress));
        var entry = Assert.Single(checkpoint.Inbox);
        Assert.Equal(replacement.Children.Single().Incarnation, ((DoneActorEvent)entry.Event).SessionId);

        var resumed = new DurableExecution<Unit>(Parent(), new CheckpointHost());
        var (restored, restoreEffects, _) = resumed.Restore(checkpoint, UnitContext);
        await resumed.ExecuteEffects(restoreEffects);
        (restored, _) = resumed.Transition(restored, await resumed.WaitForEvent());
        Assert.True(restored.Matches("success"));
    }

    [Fact]
    public async Task The_completion_of_a_child_gone_from_the_snapshot_is_dropped()
    {
        var (execution, _, state) = await CompletedChild();
        var (exited, _) = execution.Transition(state, Ev("EXIT"));

        Assert.Empty(execution.Checkpoint(exited, ByAddress).Inbox);
    }

    [Fact]
    public async Task A_stored_completion_is_rebound_to_the_restored_childs_incarnation()
    {
        var (execution, _, state) = await CompletedChild();
        var checkpoint = RoundTrip(execution.Checkpoint(state, ByAddress));
        checkpoint = checkpoint with
        {
            Inbox = [checkpoint.Inbox[0] with
            {
                Event = (DoneActorEvent)checkpoint.Inbox[0].Event with { SessionId = "from-an-earlier-session" }
            }]
        };

        var resumed = new DurableExecution<Unit>(Parent(), new CheckpointHost());
        var (restored, _, _) = resumed.Restore(checkpoint, UnitContext);
        var evt = Assert.IsType<DoneActorEvent>(await resumed.WaitForEvent());

        Assert.Equal(restored.Children.Single().Incarnation, evt.SessionId);
    }

    [Fact]
    public async Task Child_snapshot_updates_are_not_stored()
    {
        var host = new CheckpointHost();
        var execution = new DurableExecution<Unit>(Parent(), host);
        var (state, effects) = execution.InitialTransition();
        await execution.ExecuteEffects(effects);
        execution.ReportRootEvent(new SnapshotEvent("worker", "waiting", state.Children.Single().Incarnation));

        // A restored SyncSnapshot child re-reports its snapshot; the checkpoint never carries one.
        Assert.Empty(execution.Checkpoint(state, ByAddress).Inbox);
    }

    [Fact]
    public async Task A_failed_batch_discards_its_reports()
    {
        var host = new CheckpointHost();
        var machine = Machine.Create("counter")
            .Initial("idle")
            .State("idle", s => s.On("GO", t => t.Do(_ => { })))
            .Build();
        var execution = new DurableExecution<Unit>(machine, host);
        var (state, effects) = execution.InitialTransition();
        await execution.ExecuteEffects(effects);

        host.OnAction = e =>
        {
            e.ReportRootEvent(Ev("CAPTURED"));
            throw new InvalidOperationException("boom");
        };
        (state, effects) = execution.Transition(state, Ev("GO"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => execution.ExecuteEffects(effects));
        Assert.Empty(execution.Checkpoint(state).Inbox);

        // The retry reports again, and only the settled batch's report survives.
        host.OnAction = e => e.ReportRootEvent(Ev("CAPTURED"));
        await execution.ExecuteEffects(effects);
        Assert.Equal("CAPTURED", Assert.Single(execution.Checkpoint(state).Inbox).Event.Type);

        // Outside any batch, a report is pending at once.
        execution.ReportRootEvent(Ev("LATE"));
        Assert.Equal(["CAPTURED", "LATE"], execution.Checkpoint(state).Inbox.Select(e => e.Event.Type));
    }

    [Fact]
    public async Task An_invalid_inbox_is_refused()
    {
        var (execution, _, state) = await CompletedChild();
        var checkpoint = RoundTrip(execution.Checkpoint(state, ByAddress));

        var invalid = checkpoint with { Inbox = [new DurableInboxEntry(null!)] };
        var error = Assert.Throws<InvalidSnapshotException>(
            () => new DurableExecution<Unit>(Parent(), new CheckpointHost()).Restore(invalid, UnitContext));
        Assert.Equal("The checkpoint inbox is invalid.", error.Message);

        Assert.Throws<InvalidSnapshotException>(
            () => new DurableExecution<Unit>(Parent(), new CheckpointHost()).Restore(
                checkpoint with { Inbox = null! }, UnitContext));
    }

    [Fact]
    public async Task A_transition_index_that_disagrees_with_the_checkpoint_is_refused()
    {
        var (execution, _, state) = await CompletedChild();
        var checkpoint = RoundTrip(execution.Checkpoint(state, ByAddress));

        var error = Assert.Throws<InvalidOperationException>(() => new DurableExecution<Unit>(
            Parent(), new CheckpointHost(), new DurableExecutionOptions { TransitionIndex = 5 })
            .Restore(checkpoint, UnitContext));
        Assert.Contains("does not match", error.Message);

        var matching = new DurableExecution<Unit>(
            Parent(), new CheckpointHost(), new DurableExecutionOptions { TransitionIndex = 2 });
        matching.Restore(checkpoint, UnitContext);
        Assert.Equal(2, matching.NextTransitionIndex);

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableExecution<Unit>(Parent(), new CheckpointHost())
            .Restore(checkpoint with { NextTransitionIndex = -1 }, UnitContext));
    }

    [Fact]
    public async Task Restore_requires_a_fresh_execution()
    {
        var (execution, _, state) = await CompletedChild();
        var checkpoint = execution.Checkpoint(state, ByAddress);

        Assert.Throws<InvalidOperationException>(() => execution.Restore(checkpoint, UnitContext));

        var resumed = new DurableExecution<Unit>(Parent(), new CheckpointHost());
        resumed.Restore(checkpoint, UnitContext);
        Assert.Throws<InvalidOperationException>(() => resumed.Restore(checkpoint, UnitContext));
        Assert.Throws<InvalidOperationException>(() => resumed.InitialTransition());
        await Assert.ThrowsAsync<DurableExecutionResumeException>(() => resumed.Run());
    }

    [Fact]
    public async Task A_bare_persisted_snapshot_still_restores()
    {
        var (_, _, state) = await CompletedChild();
        var machine = Parent();
        var persisted = SnapshotJson.Deserialize(SnapshotJson.Serialize(machine.Persist(state, ByAddress)));

        var resumed = new DurableExecution<Unit>(
            machine, new CheckpointHost(), new DurableExecutionOptions { TransitionIndex = 2 });
        var (restored, effects, _) = resumed.Restore(persisted, UnitContext);

        Assert.True(restored.Matches("working"));
        Assert.Empty(effects);
        Assert.Equal(2, resumed.NextTransitionIndex);
    }

    [Fact]
    public async Task Restore_re_arms_pending_timers_as_tagged_effects()
    {
        var machine = Machine.Create("sleeper")
            .Initial("sleeping")
            .State("sleeping", s => s.After(TimeSpan.FromSeconds(1), t => t.Target("awake")))
            .State("awake")
            .Build();
        var execution = new DurableExecution<Unit>(machine, new CheckpointHost());
        var (state, effects) = execution.InitialTransition();
        await execution.ExecuteEffects(effects);
        var checkpoint = RoundTrip(execution.Checkpoint(state));

        var host = new CheckpointHost();
        var resumed = new DurableExecution<Unit>(machine, host);
        var (_, restoreEffects, _) = resumed.Restore(checkpoint, UnitContext);
        Assert.Equal(["restore:1:0"], restoreEffects.Select(e => e.Id));
        Assert.Equal(1, resumed.NextTransitionIndex);

        await resumed.ExecuteEffects(restoreEffects);
        Assert.Equal([state.Timers.Single().Key.Serialize()], host.Scheduled);
    }

    [Fact]
    public async Task A_restore_at_index_zero_waits_under_its_own_id()
    {
        var machine = Machine.Create("idle").Initial("idle").State("idle").Build();
        var persisted = machine.Persist(machine.GetInitialState().State);

        var host = new CheckpointHost();
        var resumed = new DurableExecution<Unit>(machine, host);
        resumed.Restore(new DurableCheckpoint(persisted, 0, []), UnitContext);
        await resumed.WaitForEvent();

        Assert.Equal(["event:restore:0"], host.Waits);
    }

    [Fact]
    public async Task A_checkpoint_round_trips_through_SnapshotJson_with_storage_safe_names()
    {
        var (execution, _, state) = await CompletedChild();
        var checkpoint = execution.Checkpoint(state, ByAddress);

        var json = JsonSerializer.Serialize(checkpoint, SnapshotJson.DefaultOptions());
        using (var document = JsonDocument.Parse(json))
        {
            var root = document.RootElement;
            Assert.Equal(
                ["snapshot", "nextTransitionIndex", "inbox"],
                root.EnumerateObject().Select(p => p.Name));
            var entry = root.GetProperty("inbox")[0];
            Assert.Equal("DoneActorEvent", entry.GetProperty("event").GetProperty("@event").GetString());
            Assert.Equal("parent/worker", entry.GetProperty("source").GetProperty("@actor").GetString());
            Assert.All(PropertyNames(root), name => Assert.False(name.StartsWith('$') || name.StartsWith('_'), name));
        }

        // A source-less entry omits `source`.
        var sourceless = checkpoint with { Inbox = [new DurableInboxEntry(Ev("PING"))] };
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(sourceless, SnapshotJson.DefaultOptions())))
        {
            Assert.False(document.RootElement.GetProperty("inbox")[0].TryGetProperty("source", out _));
        }

        var back = RoundTrip(checkpoint);
        Assert.Equal(checkpoint.NextTransitionIndex, back.NextTransitionIndex);
        Assert.Equal(checkpoint.Inbox, back.Inbox);
        Assert.Equal(json, JsonSerializer.Serialize(back, SnapshotJson.DefaultOptions()));
    }

    private static IEnumerable<string> PropertyNames(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .SelectMany(p => PropertyNames(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(PropertyNames),
        _ => []
    };

    /// <summary>
    /// Runs children in-process: a child reacts to the events sent to it, and its completion goes
    /// back to the execution as a root event carrying the child's address.
    /// </summary>
    private sealed class CheckpointHost : IDurableHost
    {
        private readonly Dictionary<string, (IActorLogic Logic, ISnapshot Snapshot, string Incarnation)> _children =
            new(StringComparer.Ordinal);

        private readonly Dictionary<string, (IActorLogic Logic, object? Input, string Incarnation)> _spawned =
            new(StringComparer.Ordinal);

        private IDurableExecution? _execution;

        public Action<IDurableExecution>? OnAction { get; set; }

        public List<string> Waits { get; } = [];

        public List<string> Scheduled { get; } = [];

        public void Attach(IDurableExecution execution) => _execution = execution;

        public ValueTask SpawnActor(DurableEffect effect, SpawnEffect spawn)
        {
            _spawned[spawn.Id] = (spawn.Logic, spawn.Input, spawn.Incarnation);
            return default;
        }

        public ValueTask StartActor(DurableEffect effect, StartEffect start)
        {
            var (logic, input, incarnation) = _spawned[start.Id];
            _children[start.Id] = (logic, logic.GetInitialSnapshot(input).State, incarnation);
            return default;
        }

        public ValueTask StopActor(DurableEffect effect, StopChildEffect stop)
        {
            _children.Remove(stop.ChildId);
            return default;
        }

        public ValueTask SendEvent(DurableEffect effect, SendToEffect send)
        {
            var (logic, snapshot, incarnation) = _children[send.Target];
            var (next, _) = logic.Transition(snapshot, send.Event);
            _children[send.Target] = (logic, next, incarnation);
            if (next.Status is SnapshotStatus.Done)
            {
                _execution!.ReportRootEvent(
                    new DoneActorEvent(send.Target, next.Output, incarnation),
                    new SerializedActorRef(ActorAddress.Child(_execution.RootAddress, send.Target)));
            }

            return default;
        }

        public ValueTask ScheduleTimer(DurableEffect effect, TimerKey key, TimeSpan delay)
        {
            Scheduled.Add(key.Serialize());
            return default;
        }

        public ValueTask CancelTimer(DurableEffect effect, CancelEffect cancel) => default;

        public ValueTask ExecuteAction(DurableEffect effect, ActionEffect action)
        {
            OnAction?.Invoke(_execution!);
            return default;
        }

        public ValueTask<MachineEvent> WaitForEvent(DurableWait wait, CancellationToken cancellationToken)
        {
            Waits.Add(wait.Id);
            return new(new NamedEvent("PING"));
        }
    }
}
