using System.Text.Json.Nodes;
using XState.Durable;
using XState.Persistence;
using Xunit;

namespace XState.Tests;

public sealed record Expired : MachineEvent;

public sealed record Replace : MachineEvent;

/// <summary>
/// Timer scheduling occurrences (xstate v6 cfc1520310): a firing from a cancelled or replaced
/// scheduling must not fire a same-key replacement early.
/// </summary>
public class TimerOccurrenceTests
{
    // --- Helpers ---

    /// <summary>The firing a host sends for a delayed raise/send: its key and its occurrence.</summary>
    private static TimerEvent FiringOf(Effect effect) => effect switch
    {
        RaiseEffect r => new TimerEvent(TimerKey.For(r.Id, r.Seq, r.Event), r.Occurrence),
        SendToEffect { Delay: not null } s => new TimerEvent(TimerKey.For(s.Id, s.Seq, s.Event), s.Occurrence),
        _ => throw new ArgumentException($"'{effect.Kind}' is not a timer effect.", nameof(effect))
    };

    private static TimerEvent SingleFiring(IReadOnlyList<Effect> effects) =>
        FiringOf(Assert.Single(effects, e => e is RaiseEffect or SendToEffect { Delay: not null }));

    private static readonly System.Text.Json.JsonSerializerOptions Json =
        SnapshotJson.DefaultOptions(typeof(Expired));

    private static State<Unit> RoundTrip(StateMachine<Unit> machine, State<Unit> state) =>
        machine.Restore(
            SnapshotJson.Deserialize(SnapshotJson.Serialize(machine.Persist(state), Json), Json),
            new RestoreOptions<Unit> { ContextConverter = _ => Unit.Value }).State;

    /// <summary>
    /// <c>Go</c> schedules <see cref="Expired"/> under id <c>shared</c>; <see cref="Replace"/>
    /// cancels <c>shared</c> and schedules it again. The delivery is a raise or a send to self.
    /// </summary>
    private static StateMachine<Unit> Replaceable(bool sendTo)
    {
        var delay = TimeSpan.FromMilliseconds(100);
        return Machine.Create("replaceable")
            .Initial("idle")
            .State("idle", s => s
                .On<Go>(t => Schedule(t, sendTo, delay))
                .On<Replace>(t => Schedule(t.Do(new CancelAction<Unit>(_ => "shared")), sendTo, delay))
                .On<Expired>(t => t.Target("expired")))
            .State("expired")
            .Build();
    }

    private static Builder.TransitionBuilder<Unit, TEvent> Schedule<TEvent>(
        Builder.TransitionBuilder<Unit, TEvent> t, bool sendTo, TimeSpan delay)
        where TEvent : MachineEvent =>
        sendTo
            ? t.SendTo(EffectTarget.Self, _ => new Expired(), delay, "shared")
            : t.Raise(new Expired(), delay, "shared");

    private static StateMachine<Unit> Restartable() =>
        Machine.Create("restartable")
            .Initial("waiting")
            .State("waiting", s => s
                .After(TimeSpan.FromMilliseconds(100), t => t.Target("done"))
                .On<Go>(t => t.Target("waiting").Reenter()))
            .State("done")
            .Build();

    // --- Stale firings ---

    [Fact]
    public void A_stale_firing_does_not_fire_a_delayed_raise_replaced_under_the_same_id()
    {
        var machine = Replaceable(sendTo: false);
        var started = machine.Transition(machine.GetInitialState().State, new Go());
        var stale = SingleFiring(started.Effects);

        var replaced = machine.Transition(started.State, new Replace());
        var fresh = SingleFiring(replaced.Effects);
        Assert.Equal(stale.Key, fresh.Key);
        Assert.NotEqual(stale.Occurrence, fresh.Occurrence);

        var ignored = machine.Transition(replaced.State, stale);
        Assert.Empty(ignored.Effects);
        Assert.True(ignored.State.Matches("idle"));
        Assert.Single(ignored.State.Timers);

        var fired = machine.Transition(ignored.State, fresh);
        Assert.True(fired.State.Matches("expired"));
        Assert.Empty(fired.State.Timers);
    }

    [Fact]
    public void A_stale_firing_does_not_fire_a_delayed_send_replaced_under_the_same_id()
    {
        var machine = Replaceable(sendTo: true);
        var started = machine.Transition(machine.GetInitialState().State, new Go());
        var stale = SingleFiring(started.Effects);
        var replaced = machine.Transition(started.State, new Replace());
        var fresh = SingleFiring(replaced.Effects);

        var ignored = machine.Transition(replaced.State, stale);
        Assert.Empty(ignored.Effects);
        Assert.Single(ignored.State.Timers);

        var fired = machine.Transition(ignored.State, fresh);
        var send = Assert.IsType<SendToEffect>(Assert.Single(fired.Effects));
        Assert.IsType<Expired>(send.Event);
        Assert.Null(send.Delay);
        Assert.Empty(fired.State.Timers);
    }

    [Fact]
    public void A_stale_after_firing_does_not_fire_the_timer_scheduled_by_reentry()
    {
        var machine = Restartable();
        var initial = machine.GetInitialState();
        var stale = SingleFiring(initial.Effects);

        var restarted = machine.Transition(initial.State, new Go());
        var fresh = SingleFiring(restarted.Effects);
        Assert.Equal(stale.Key.Serialize(), fresh.Key.Serialize());
        Assert.NotEqual(stale.Occurrence, fresh.Occurrence);

        var ignored = machine.Transition(restarted.State, stale);
        Assert.True(ignored.State.Matches("waiting"));
        Assert.Single(ignored.State.Timers);

        Assert.True(machine.Transition(ignored.State, fresh).State.Matches("done"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_stale_firing_is_ignored_after_a_persist_and_restore(bool sendTo)
    {
        var machine = Replaceable(sendTo);
        var started = machine.Transition(machine.GetInitialState().State, new Go());
        var stale = SingleFiring(started.Effects);

        var restored = RoundTrip(machine, started.State);
        Assert.Equal(stale.Occurrence, Assert.Single(restored.Timers).Occurrence);

        var replaced = machine.Transition(restored, new Replace());
        var fresh = SingleFiring(replaced.Effects);
        Assert.NotEqual(stale.Occurrence, fresh.Occurrence);

        var ignored = machine.Transition(replaced.State, stale);
        Assert.Empty(ignored.Effects);
        Assert.Single(ignored.State.Timers);

        var fired = machine.Transition(ignored.State, fresh);
        Assert.Empty(fired.State.Timers);
        if (sendTo)
        {
            Assert.IsType<Expired>(Assert.IsType<SendToEffect>(Assert.Single(fired.Effects)).Event);
        }
        else
        {
            Assert.True(fired.State.Matches("expired"));
        }
    }

    [Fact]
    public void A_stale_after_firing_is_ignored_after_a_persist_and_restore()
    {
        var machine = Restartable();
        var initial = machine.GetInitialState();
        var stale = SingleFiring(initial.Effects);

        var restarted = machine.Transition(RoundTrip(machine, initial.State), new Go());
        var fresh = SingleFiring(restarted.Effects);
        Assert.NotEqual(stale.Occurrence, fresh.Occurrence);

        var ignored = machine.Transition(restarted.State, stale);
        Assert.True(ignored.State.Matches("waiting"));
        Assert.True(machine.Transition(ignored.State, fresh).State.Matches("done"));
    }

    [Fact]
    public void A_firing_without_an_occurrence_matches_by_key_alone()
    {
        var machine = Replaceable(sendTo: false);
        var started = machine.Transition(machine.GetInitialState().State, new Go());
        var replaced = machine.Transition(started.State, new Replace());

        var fired = machine.Transition(replaced.State, new TimerEvent(new TimerKey.ById("shared")));

        Assert.True(fired.State.Matches("expired"));
        Assert.Empty(fired.State.Timers);
    }

    // --- Stamping ---

    [Fact]
    public void Named_and_anonymous_timers_share_the_occurrence_counter_but_not_the_seq_counter()
    {
        var machine = Machine.Create("mixed")
            .Initial("a")
            .State("a", s => s
                .Entry(new RaiseAction<Unit>(_ => new Ping(), TimeSpan.FromMilliseconds(100), "named"))
                .Entry(new RaiseAction<Unit>(_ => new Next(), TimeSpan.FromMilliseconds(200))))
            .Build();

        var initial = machine.GetInitialState();

        Assert.Equal(0, initial.State.Timers[1].Seq);
        Assert.Equal([0, 1], initial.State.Timers.Select(t => t.Occurrence));
        Assert.Equal([0, 1], initial.Effects.OfType<RaiseEffect>().Select(r => r.Occurrence));
        Assert.Equal(2, initial.State.NextTimerOccurrence);
        Assert.Equal(1, initial.State.NextTimerId);
    }

    [Fact]
    public void Descriptors_carry_the_seq_and_occurrence_of_a_delayed_raise()
    {
        var machine = Machine.Create("described")
            .Initial("a")
            .State("a", s => s.Entry(new RaiseAction<Unit>(_ => new Ping(), TimeSpan.FromMilliseconds(100))))
            .Build();

        var raise = Assert.IsType<RaiseEffect>(Assert.Single(machine.GetInitialState().Effects));
        var descriptor = EffectDescriptor.Of(raise, "described");

        Assert.Equal(0, descriptor.Seq);
        Assert.Equal(0, descriptor.Occurrence);
    }

    // --- Persistence ---

    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 1)]
    [InlineData(20, 20)]
    public void Restore_keeps_the_occurrence_counter_ahead_of_every_pending_timer(int? persistedNext, int expected)
    {
        var machine = Machine.Create("floor")
            .Initial("a")
            .State("a", s => s
                .Entry(new RaiseAction<Unit>(_ => new Ping(), TimeSpan.FromMilliseconds(100), "shared"))
                .On<Replace>(t => t.Raise(new Ping(), TimeSpan.FromMilliseconds(100), "shared")))
            .Build();

        var initial = machine.GetInitialState().State;
        var node = JsonNode.Parse(SnapshotJson.Serialize(machine.Persist(initial)))!;
        Assert.Equal(0, (int)node["timers"]![0]!["occurrence"]!);
        node.AsObject().Remove("nextTimerOccurrence");
        if (persistedNext is { } next)
        {
            node["nextTimerOccurrence"] = next;
        }

        var restored = machine.Restore(SnapshotJson.Deserialize(node.ToJsonString()),
            new RestoreOptions<Unit> { ContextConverter = _ => Unit.Value }).State;
        var replaced = machine.Transition(restored, new Replace());

        Assert.Equal(expected, Assert.Single(replaced.State.Timers).Occurrence);
        Assert.Empty(machine.Transition(replaced.State, new TimerEvent(new TimerKey.ById("shared"), 0)).Effects);
    }

    [Fact]
    public void Persisted_timers_write_occurrence_first_and_the_counter_after_next_timer_id()
    {
        var machine = Machine.Create("wire")
            .Initial("a")
            .State("a", s => s.Entry(new RaiseAction<Unit>(_ => new Ping(), TimeSpan.FromMilliseconds(100), "t")))
            .Build();

        var json = SnapshotJson.Serialize(machine.Persist(machine.GetInitialState().State));

        Assert.Contains("\"timers\":[{\"occurrence\":0,\"id\":\"t\",", json);
        Assert.Contains("\"nextTimerId\":0,\"nextTimerOccurrence\":1,", json);
    }

    [Fact]
    public void The_occurrence_counter_is_omitted_when_zero()
    {
        var machine = Machine.Create("none").Initial("a").State("a").Build();

        var json = SnapshotJson.Serialize(machine.Persist(machine.GetInitialState().State));

        Assert.DoesNotContain("nextTimerOccurrence", json);
    }

    [Fact]
    public void A_legacy_timer_without_an_occurrence_restores_and_fires_by_key()
    {
        var machine = Restartable();
        var node = JsonNode.Parse(SnapshotJson.Serialize(machine.Persist(machine.GetInitialState().State)))!;
        node["timers"]![0]!.AsObject().Remove("occurrence");
        node.AsObject().Remove("nextTimerOccurrence");

        var restored = machine.Restore(SnapshotJson.Deserialize(node.ToJsonString()),
            new RestoreOptions<Unit> { ContextConverter = _ => Unit.Value });
        var timer = Assert.Single(restored.Timers);
        Assert.Null(timer.Occurrence);
        Assert.Equal(0, restored.State.NextTimerOccurrence);

        Assert.True(machine.Transition(restored.State, new TimerEvent(timer.Key)).State.Matches("done"));
    }

    [Fact]
    public void A_negative_occurrence_counter_is_rejected_on_restore()
    {
        var machine = Restartable();
        var persisted = machine.Persist(machine.GetInitialState().State);

        Assert.Throws<InvalidSnapshotException>(() => machine.Restore(persisted with { NextTimerOccurrence = -1 }));
        Assert.Throws<InvalidSnapshotException>(() => machine.Restore(persisted with
        {
            Timers = [Assert.Single(persisted.Timers) with { Occurrence = -1 }]
        }));
    }

    // --- Hosts ---

    [Fact]
    public async Task Rearmed_timers_carry_their_occurrence()
    {
        var machine = Restartable();
        var restored = machine.Restore(machine.Persist(machine.Transition(machine.GetInitialState().State, new Go()).State));
        var host = new RecordingHost();
        var execution = new DurableExecution<Unit>(machine, host);

        await execution.RearmTimers(restored.Timers);

        var (effect, _) = Assert.Single(host.Scheduled);
        Assert.Equal(1, effect.Descriptor.Occurrence);
        Assert.Equal(1, Assert.IsType<RaiseEffect>(effect.Effect).Occurrence);
    }

    private sealed class RecordingHost : IDurableHost
    {
        public List<(DurableEffect Effect, TimerKey Key)> Scheduled { get; } = [];

        public ValueTask ScheduleTimer(DurableEffect effect, TimerKey key, TimeSpan delay)
        {
            Scheduled.Add((effect, key));
            return default;
        }

        public ValueTask ExecuteAction(DurableEffect effect, ActionEffect action) => default;

        public ValueTask<MachineEvent> WaitForEvent(DurableWait wait, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
