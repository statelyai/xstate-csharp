using System.Text.Json;
using Xunit;
using XState.Persistence;

namespace XState.Tests;

/// <summary>
/// Persist/restore: what has to survive the gap between two processes for a long-running actor to
/// pick up where it left off — where it was, what it was waiting for, and who it was waiting on.
/// </summary>
public class PersistenceTests
{
    private static StateMachine<Unit> Worker() =>
        Machine.Create("worker").Initial("busy").State("busy").Build();

    /// <summary>A job that invokes a registered worker and gives up after five seconds.</summary>
    private static StateMachine<Unit> Job(IActorLogic worker, string? version = "1.0.0")
    {
        var builder = Machine.Create("job")
            .Actor("worker", worker)
            .Initial("running")
            .State("running", s => s
                .Invoke("worker", id: "w")
                .After(TimeSpan.FromSeconds(5), t => t.Target("timedOut")))
            .State("timedOut");

        return (version is null ? builder : builder.Version(version)).Build();
    }

    // --- Round trip ---

    [Fact]
    public void A_restored_snapshot_names_the_child_to_recreate_and_the_timer_to_rearm()
    {
        var worker = Worker();
        var machine = Job(worker);

        var initial = machine.GetInitialState();
        var timerId = Assert.Single(initial.State.Timers.Keys);

        var persisted = machine.Persist(
            initial.State,
            new PersistOptions { ChildSnapshot = _ => Persist(worker) });

        // The child is identified by its durable address, not by any per-process handle.
        var child = Assert.Single(persisted.Children);
        Assert.Equal("w", child.Id);
        Assert.Equal("job/w", child.Address);
        Assert.Equal("worker", child.Src);
        Assert.False(child.Remote);
        Assert.NotNull(child.Snapshot);

        var timer = Assert.Single(persisted.Timers).Value;
        Assert.Equal("@xstate.raise", timer.Type);
        Assert.Equal(5000, timer.DelayMs);
        Assert.Null(timer.Target);

        var restored = machine.Restore(persisted);

        var toRestore = Assert.Single(restored.Children);
        Assert.Same(worker, toRestore.Logic);
        Assert.Equal("job/w", toRestore.Address);
        Assert.False(toRestore.Remote);

        var toRearm = Assert.Single(restored.Timers);
        Assert.Equal(timerId, toRearm.Id);
        Assert.Equal(TimeSpan.FromSeconds(5), toRearm.Delay);

        // The restored snapshot is the same state, and continues identically.
        Assert.Equal(initial.State.Configuration, restored.State.Configuration);
        Assert.True(restored.State.Matches("running"));

        var fired = machine.Transition(restored.State, new TimerEvent(timerId));
        Assert.True(fired.State.Matches("timedOut"));
        Assert.Equal(
            machine.Transition(initial.State, new TimerEvent(timerId)).Effects.Select(e => e.Kind),
            fired.Effects.Select(e => e.Kind));
    }

    [Fact]
    public void A_snapshot_survives_a_round_trip_through_json_and_keeps_transitioning()
    {
        var worker = Worker();
        var machine = Job(worker);
        var options = SnapshotJson.DefaultOptions();

        var initial = machine.GetInitialState();
        var timerId = Assert.Single(initial.State.Timers.Keys);

        var json = JsonSerializer.Serialize(
            machine.Persist(initial.State, new PersistOptions { ChildSnapshot = _ => Persist(worker) }),
            options);

        var wire = JsonSerializer.Deserialize<PersistedSnapshot>(json, options)!;

        // A deserializer cannot know TContext, so the host converts it back.
        var restored = machine.Restore(
            wire,
            new RestoreOptions<Unit> { ContextConverter = _ => Unit.Value });

        Assert.Equal("worker", Assert.Single(restored.Children).Src);
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(restored.Timers).Delay);

        var expected = machine.Transition(initial.State, new TimerEvent(timerId));
        var actual = machine.Transition(restored.State, new TimerEvent(timerId));

        Assert.True(actual.State.Matches("timedOut"));
        Assert.Equal(expected.State.Configuration, actual.State.Configuration);
        Assert.Equal(expected.Effects.ToArray(), actual.Effects.ToArray());
    }

    [Fact]
    public void An_unknown_event_type_survives_json_as_a_named_event_with_its_payload_intact()
    {
        var options = SnapshotJson.DefaultOptions();
        var json = JsonSerializer.Serialize<MachineEvent>(new Ping(), options);

        Assert.Contains("\"$event\":\"Ping\"", json, StringComparison.Ordinal);

        // Nothing registered `Ping`, so it reads back as the type the machine matches on, with the
        // payload kept verbatim rather than dropped.
        var unknown = Assert.IsType<NamedEvent>(JsonSerializer.Deserialize<MachineEvent>(json, options));
        Assert.Equal("Ping", unknown.Type);
        Assert.Equal(json, unknown.Data);

        // Registered, it reads back as itself.
        var known = JsonSerializer.Deserialize<MachineEvent>(json, SnapshotJson.DefaultOptions(typeof(Ping)));
        Assert.Equal(new Ping(), known);
    }

    // --- Inline actors ---

    [Fact]
    public void An_inline_child_cannot_be_persisted_unless_the_caller_insists()
    {
        var machine = Machine.Create("job")
            .Initial("running")
            .State("running", s => s.Invoke(Worker(), "w"))
            .Build();

        var state = machine.GetInitialState().State;
        Assert.Null(Assert.Single(state.Children).Src);

        var error = Assert.Throws<InvalidOperationException>(() => machine.Persist(state));
        Assert.Equal("An inline child actor cannot be persisted.", error.Message);

        var persisted = machine.Persist(
            state,
            new PersistOptions { AllowInlineActors = true, ChildSnapshot = _ => Persist(Worker()) });
        Assert.Null(Assert.Single(persisted.Children).Src);

        // …and the snapshot it produces is exactly the one that cannot be restored.
        var restoreError = Assert.Throws<InvalidOperationException>(() => machine.Restore(persisted));
        Assert.Equal(
            "Unable to restore child actor 'w': child source '<unknown>' is not provided in machine 'job'.",
            restoreError.Message);
    }

    [Fact]
    public void Embedding_is_the_hosts_job_and_a_missing_child_snapshot_is_an_error()
    {
        var worker = Worker();
        var machine = Job(worker);
        var state = machine.GetInitialState().State;

        var error = Assert.Throws<InvalidOperationException>(() => machine.Persist(state));
        Assert.StartsWith("Child 'w' has no persisted snapshot.", error.Message, StringComparison.Ordinal);

        // Persisting by address needs no child state at all: the child lives elsewhere.
        var byAddress = machine.Persist(state, new PersistOptions { EmbedChildren = false });
        var child = Assert.Single(byAddress.Children);
        Assert.True(child.Remote);
        Assert.Null(child.Snapshot);

        // A remote child is re-attached by address, not re-created, so it comes back without logic.
        var restored = Assert.Single(machine.Restore(byAddress).Children);
        Assert.True(restored.Remote);
        Assert.Null(restored.Logic);
        Assert.Equal("job/w", restored.Address);
    }

    // --- Identity gate ---

    [Fact]
    public void A_snapshot_from_another_machine_is_refused()
    {
        var machine = Job(Worker());
        var persisted = machine.Persist(machine.GetInitialState().State, new PersistOptions { EmbedChildren = false });

        var error = Assert.Throws<InvalidOperationException>(
            () => machine.Restore(persisted with { Machine = new PersistedMachineIdentity("other", "1.0.0") }));

        Assert.Equal(
            "Machine ID mismatch: persisted snapshot was created by machine 'other', but machine 'job' was provided.",
            error.Message);
    }

    [Fact]
    public void A_snapshot_from_another_version_is_refused_unless_a_migration_is_supplied()
    {
        var worker = Worker();
        var old = Job(worker, "1.0.0");
        var current = Job(worker, "2.0.0");

        var persisted = old.Persist(old.GetInitialState().State, new PersistOptions { EmbedChildren = false });

        var error = Assert.Throws<InvalidOperationException>(() => current.Restore(persisted));
        Assert.StartsWith(
            "Persisted snapshot version '1.0.0' does not match machine version '2.0.0' for machine 'job'.",
            error.Message,
            StringComparison.Ordinal);

        string? seen = "unset";
        var restored = current.Restore(persisted, new RestoreOptions<Unit>
        {
            Migrate = (snapshot, from) =>
            {
                seen = from;
                return snapshot with
                {
                    Machine = new PersistedMachineIdentity("job", "2.0.0"),
                    Version = "2.0.0"
                };
            }
        });

        Assert.Equal("1.0.0", seen);
        Assert.True(restored.State.Matches("running"));
    }

    [Fact]
    public void A_snapshot_whose_two_recorded_versions_disagree_is_refused()
    {
        var machine = Job(Worker());
        var persisted = machine.Persist(machine.GetInitialState().State, new PersistOptions { EmbedChildren = false });

        var error = Assert.Throws<InvalidOperationException>(
            () => machine.Restore(persisted with { Version = "0.9.0" }));

        Assert.Equal(
            "Persisted snapshot version '0.9.0' conflicts with machine version '1.0.0'.",
            error.Message);
    }

    [Fact]
    public void A_value_naming_a_state_this_machine_does_not_have_is_refused()
    {
        var machine = Job(Worker());
        var persisted = machine.Persist(machine.GetInitialState().State, new PersistOptions { EmbedChildren = false });

        var error = Assert.Throws<InvalidOperationException>(
            () => machine.Restore(persisted with { Value = "gone" }));

        Assert.Equal(
            "Persisted snapshot references state 'gone' which does not exist on machine 'job'.",
            error.Message);
    }

    [Fact]
    public void A_timer_aimed_at_a_child_the_snapshot_no_longer_knows_is_refused()
    {
        var machine = Job(Worker());
        var persisted = machine.Persist(machine.GetInitialState().State, new PersistOptions { EmbedChildren = false });

        var stray = Assert.Single(persisted.Timers).Value with { Target = "ghost" };
        var error = Assert.Throws<InvalidOperationException>(() => machine.Restore(persisted with
        {
            Timers = new Dictionary<string, PersistedTimer> { [stray.Id] = stray }
        }));

        Assert.Equal($"Unable to restore timer '{stray.Id}': target actor 'ghost' is unavailable.", error.Message);
    }

    // --- Counter folding ---

    [Fact]
    public void A_restored_generated_child_id_is_never_handed_out_again()
    {
        var worker = Worker();
        var machine = Machine.Create("spawner")
            .Actor("worker", worker)
            .Initial("idle")
            .State("idle", s => s.On<Ping>(t => t.Do(new SpawnAction<Unit>(worker, Src: "worker"))))
            .Build();

        var persisted = machine.Persist(machine.GetInitialState().State) with
        {
            // A snapshot written before per-prefix counters existed: the child is there, the
            // counter is not.
            Children =
            [
                new PersistedChild { Id = "worker:3", Address = "spawner/worker:3", Src = "worker", Incarnation = "0" }
            ]
        };

        var restored = machine.Restore(persisted);
        Assert.Equal(4, restored.State.NextActorIds["worker"]);

        var spawned = Assert.Single(machine.Transition(restored.State, new Ping()).Effects.OfType<SpawnEffect>());
        Assert.Equal("worker:4", spawned.Id);
    }

    private static PersistedSnapshot Persist(StateMachine<Unit> machine) =>
        machine.Persist(machine.GetInitialState().State);
}
