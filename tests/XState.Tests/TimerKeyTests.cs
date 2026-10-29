using System.Text.Json;
using Xunit;
using XState.Durable;
using XState.Persistence;

namespace XState.Tests;

/// <summary>Timer identity (xstate v6 <c>timerKey.ts</c>, #5800): id, else seq, else event.</summary>
public class TimerKeyTests
{
    private static StateMachine<Unit> TwoTimers() =>
        Machine.Create("two")
            .Initial("a")
            .State("a", s => s
                .Entry(new RaiseAction<Unit>(_ => new Ping(), TimeSpan.FromSeconds(1), "first"))
                .Entry(new RaiseAction<Unit>(_ => new Ping(), TimeSpan.FromSeconds(2), "second"))
                .On<Go>(t => t.Do(new RaiseAction<Unit>(_ => new Go(), TimeSpan.FromSeconds(9), "first"))))
            .Build();

    [Fact]
    public void Rescheduling_a_pending_key_replaces_it_in_place_without_a_cancel()
    {
        var machine = TwoTimers();
        var initial = machine.GetInitialState();

        var again = machine.Transition(initial.State, new Go());

        Assert.Empty(again.Effects.OfType<CancelEffect>());
        Assert.Equal(["first", "second"], again.State.Timers.Select(t => t.Id));
        Assert.Equal(TimeSpan.FromSeconds(9), again.State.Timers[0].Delay);
        Assert.IsType<Go>(again.State.Timers[0].Event);
    }

    [Fact]
    public void An_after_timer_fires_by_its_event_key()
    {
        var machine = Machine.Create("t")
            .Initial("a")
            .State("a", s => s.After(TimeSpan.FromSeconds(1), t => t.Target("b")))
            .State("b")
            .Build();

        var initial = machine.GetInitialState();
        var timer = Assert.Single(initial.State.Timers);
        Assert.Null(timer.Id);
        Assert.Null(timer.Seq);

        var fired = machine.Transition(initial.State, new TimerEvent(new TimerKey.ByEvent(new AfterEvent("1000", "t.a"))));

        Assert.True(fired.State.Matches("b"));
    }

    [Fact]
    public void An_id_less_delayed_raise_is_numbered_and_cancellable_by_seq()
    {
        var machine = Machine.Create("seq")
            .Initial("a")
            .State("a", s => s
                .Entry(new RaiseAction<Unit>(_ => new Ping(), TimeSpan.FromSeconds(1)))
                .Entry(new RaiseAction<Unit>(_ => new Ping(), TimeSpan.FromSeconds(1)))
                .On<Go>(t => t.Do(new CancelAction<Unit>(_ => new TimerKey.BySeq(0)))))
            .Build();

        var initial = machine.GetInitialState();
        Assert.Equal([0, 1], initial.State.Timers.Select(t => t.Seq));
        Assert.Equal(2, initial.State.NextTimerId);

        var cancelled = machine.Transition(initial.State, new Go());

        Assert.Equal(new TimerKey.BySeq(0), Assert.Single(cancelled.Effects.OfType<CancelEffect>()).Key);
        Assert.Equal(1, Assert.Single(cancelled.State.Timers).Seq);
    }

    [Fact]
    public void A_built_in_timer_is_cancellable_by_its_event()
    {
        var machine = Machine.Create("t")
            .Initial("a")
            .State("a", s => s
                .After(TimeSpan.FromSeconds(1), t => t.Target("b"))
                .On<Go>(t => t.Do(new CancelAction<Unit>(_ => new TimerKey.ByEvent(new AfterEvent("1000", "t.a"))))))
            .State("b")
            .Build();

        var cancelled = machine.Transition(machine.GetInitialState().State, new Go());

        Assert.Empty(cancelled.State.Timers);
        Assert.True(cancelled.State.Matches("a"));
    }

    [Fact]
    public void An_actor_timeout_key_ignores_the_incarnation_token()
    {
        var key = new TimerKey.ByEvent(new ActorTimeoutEvent("kid"));
        var timer = new LogicalTimer(null, null, TimeSpan.Zero, TimerKind.Raise, new ActorTimeoutEvent("kid", "3"), null);

        Assert.True(key.Matches(timer));
        Assert.Equal(key.Serialize(), timer.Key.Serialize());
        Assert.False(new TimerKey.ByEvent(new ActorTimeoutEvent("other")).Matches(timer));
        Assert.False(new TimerKey.BySeq(0).Matches(timer with { Id = "x" }));
    }

    [Fact]
    public void Persisted_timers_round_trip_in_order_with_scheduled_at()
    {
        var machine = TwoTimers();
        var options = SnapshotJson.DefaultOptions();
        var persisted = machine.Persist(machine.GetInitialState().State);
        persisted = persisted with
        {
            Timers = [persisted.Timers[0] with { ScheduledAt = 1234 }, persisted.Timers[1]]
        };

        var json = JsonSerializer.Serialize(persisted, options);
        Assert.Contains("\"timers\":[", json);

        var restored = machine.Restore(
            JsonSerializer.Deserialize<PersistedSnapshot>(json, options)!,
            new RestoreOptions<Unit> { ContextConverter = _ => Unit.Value });

        Assert.Equal(["first", "second"], restored.Timers.Select(t => t.Id));
        Assert.Equal(1234, restored.Timers[0].ScheduledAt);
        Assert.Null(restored.Timers[1].ScheduledAt);
        Assert.Equal(1234, machine.Persist(restored.State).Timers[0].ScheduledAt);
    }

    [Fact]
    public void An_old_record_shaped_timers_ledger_is_rejected()
    {
        var options = SnapshotJson.DefaultOptions();
        var json = JsonSerializer.Serialize(TwoTimers().Persist(TwoTimers().GetInitialState().State), options);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;

        // The pre-alpha.68 shape: an object keyed by timer id.
        node["timers"] = new System.Text.Json.Nodes.JsonObject { ["first"] = node["timers"]![0]!.DeepClone() };
        var old = node.ToJsonString();

        var error = Assert.Throws<InvalidSnapshotException>(
            () => JsonSerializer.Deserialize<PersistedSnapshot>(old, options));
        Assert.Contains("keyed by timer id", error.Message);
    }

    [Fact]
    public async Task The_durable_host_receives_keys_and_rearms_by_key()
    {
        var machine = Machine.Create("job")
            .Initial("a")
            .State("a", s => s
                .Entry(new RaiseAction<Unit>(_ => new Ping(), TimeSpan.FromSeconds(1)))
                .After(TimeSpan.FromSeconds(2), t => t.Target("b")))
            .State("b")
            .Build();

        var host = new KeyHost();
        var execution = new DurableExecution<Unit>(machine, host);
        var (state, effects) = execution.InitialTransition();
        await execution.ExecuteEffects(effects);

        var after = new TimerKey.ByEvent(new AfterEvent("2000", "job.a"));
        Assert.Equal([new TimerKey.BySeq(0), after], host.Scheduled.Select(s => s.Key));

        var restored = machine.Restore(machine.Persist(state));
        var rearm = new KeyHost();
        await new DurableExecution<Unit>(machine, rearm).RearmTimers(restored.Timers);
        Assert.Equal(host.Scheduled, rearm.Scheduled);
    }

    private sealed class KeyHost : IDurableHost
    {
        public List<(TimerKey Key, TimeSpan Delay)> Scheduled { get; } = [];

        public ValueTask ScheduleTimer(DurableEffect effect, TimerKey key, TimeSpan delay)
        {
            Scheduled.Add((key, delay));
            return default;
        }

        public ValueTask StartActor(DurableEffect effect, StartEffect start) => default;

        public ValueTask ExecuteAction(DurableEffect effect, ActionEffect action) => default;

        public ValueTask<MachineEvent> WaitForEvent(DurableWait wait, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
