using System.Text.Json;
using Xunit;
using XState.Persistence;

namespace XState.Tests;

/// <summary>
/// Machine versions: a long-lived actor outlives the code that created it, so storage fills up
/// with snapshots written by machines that no longer exist. These tests pin the three answers that
/// makes possible — which version wrote this, how does it come forward, and what happens to the
/// event history recorded alongside it.
/// </summary>
public class MachineVersionsTests
{
    private static StateMachine<Unit> Cart(string? version, string id = "cart")
    {
        var builder = Machine.Create(id).Initial("idle").State("idle").State("checkout");
        return (version is null ? builder : builder.Version(version)).Build();
    }

    private static readonly StateMachine<Unit> V1 = Cart("1.0.0");
    private static readonly StateMachine<Unit> V2 = Cart("2.0.0");

    private static PersistedSnapshot SnapshotOf(StateMachine<Unit> machine) =>
        machine.Persist(machine.GetInitialState().State);

    // --- Registration invariants (v6 machineVersions.ts:322-359) ---

    [Fact]
    public void Every_registered_machine_must_declare_a_version()
    {
        var error = Assert.Throws<InvalidOperationException>(() => MachineVersions.Create(V1, Cart(null)));
        Assert.Equal("Machine 'cart' must define a version.", error.Message);
    }

    [Fact]
    public void The_wildcard_is_not_a_version()
    {
        var error = Assert.Throws<InvalidOperationException>(() => MachineVersions.Create(Cart("*")));
        Assert.Equal("Machine version '*' is reserved for wildcard migrations.", error.Message);
    }

    [Fact]
    public void Every_registered_machine_must_be_the_same_machine()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => MachineVersions.Create(V1, Cart("2.0.0", "basket")));
        Assert.Equal("Machine 'basket' does not match machine ID 'cart'.", error.Message);
    }

    [Fact]
    public void A_version_cannot_be_registered_twice()
    {
        var error = Assert.Throws<InvalidOperationException>(() => MachineVersions.Create(V1, Cart("1.0.0")));
        Assert.Equal("Duplicate machine identity 'cart' version '1.0.0'.", error.Message);
    }

    [Fact]
    public void The_unversioned_fallback_must_itself_be_retained()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => MachineVersions.Create([V1, V2], unversioned: "0.9.0"));
        Assert.Equal("Unversioned snapshot version '0.9.0' is not retained for machine 'cart'.", error.Message);

        var versions = MachineVersions.Create([V1, V2], unversioned: "1.0.0");
        Assert.Equal("cart", versions.MachineId);
        Assert.Equal(["1.0.0", "2.0.0"], versions.Versions.Order());
        Assert.Same(V2, versions.Get("2.0.0"));
        Assert.Null(versions.Get("3.0.0"));
    }

    // --- Identity resolution (v6 machineVersions.ts:361-419) ---

    [Fact]
    public void Identity_comes_from_the_nested_machine_first()
    {
        var versions = MachineVersions.Create(V1, V2);
        var parsed = versions.ParseSnapshot(SnapshotOf(V2));

        Assert.Equal(new PersistedMachineIdentity("cart", "2.0.0"), parsed.Identity);
        Assert.Same(V2, parsed.Machine);
        Assert.Equal("2.0.0", parsed.Snapshot.Machine!.Version);
    }

    [Fact]
    public void Identity_falls_back_to_the_legacy_top_level_version()
    {
        var versions = MachineVersions.Create(V1, V2);
        var legacy = SnapshotOf(V1) with { Machine = null, Version = "1.0.0" };

        var parsed = versions.ParseSnapshot(legacy);

        Assert.Equal("1.0.0", parsed.Identity.Version);
        Assert.Same(V1, parsed.Machine);
        // Parsing stamps the identity it resolved, so the caller never has to resolve it twice.
        Assert.Equal(new PersistedMachineIdentity("cart", "1.0.0"), parsed.Snapshot.Machine);
    }

    [Fact]
    public void Identity_falls_back_to_the_configured_unversioned_version_last()
    {
        var bare = SnapshotOf(V1) with { Machine = null, Version = null };

        Assert.Same(V1, MachineVersions.Create([V1, V2], unversioned: "1.0.0").ParseSnapshot(bare).Machine);

        var error = Assert.Throws<InvalidOperationException>(
            () => MachineVersions.Create(V1, V2).ParseSnapshot(bare));
        Assert.Equal("Persisted snapshot is missing machine identity.", error.Message);
    }

    [Fact]
    public void A_snapshot_whose_two_recorded_versions_disagree_is_refused()
    {
        var versions = MachineVersions.Create(V1, V2);
        var conflicted = SnapshotOf(V2) with { Version = "1.0.0" };

        var error = Assert.Throws<InvalidOperationException>(() => versions.ParseSnapshot(conflicted));
        Assert.Equal("Persisted snapshot version '1.0.0' conflicts with machine version '2.0.0'.", error.Message);
    }

    [Fact]
    public void A_version_that_is_no_longer_retained_is_refused()
    {
        var versions = MachineVersions.Create(V2);

        var error = Assert.Throws<InvalidOperationException>(() => versions.ParseSnapshot(SnapshotOf(V1)));
        Assert.Equal("Unknown machine identity 'cart' version '1.0.0'.", error.Message);
    }

    [Fact]
    public void A_snapshot_can_be_parsed_straight_from_json()
    {
        var versions = MachineVersions.Create(V1, V2);
        var options = SnapshotJson.DefaultOptions();
        var json = JsonSerializer.SerializeToElement(SnapshotOf(V1), options);

        Assert.Equal("1.0.0", versions.ParseSnapshot(json, options).Identity.Version);

        var error = Assert.Throws<InvalidOperationException>(
            () => versions.ParseSnapshot(JsonSerializer.SerializeToElement(42), options));
        Assert.Equal("Persisted snapshot is missing machine identity.", error.Message);
    }

    // --- Migration (v6 machineVersions.ts:497-573) ---

    [Fact]
    public void A_snapshot_already_at_the_target_version_passes_through()
    {
        var versions = MachineVersions.Create(V1, V2);

        var migrated = versions.MigrateSnapshot(SnapshotOf(V2), "2.0.0", new Dictionary<string, Func<PersistedSnapshot, PersistedSnapshot>>());

        Assert.Equal(new PersistedMachineIdentity("cart", "2.0.0"), migrated.Machine);
        Assert.Equal("2.0.0", migrated.Version);
    }

    [Fact]
    public void An_exact_migration_handles_its_own_source_version_and_the_result_is_stamped()
    {
        var versions = MachineVersions.Create(V1, V2);
        string? seen = null;

        var migrated = versions.MigrateSnapshot(SnapshotOf(V1), "2.0.0", new Dictionary<string, Func<PersistedSnapshot, PersistedSnapshot>>
        {
            ["1.0.0"] = snapshot =>
            {
                seen = snapshot.Machine!.Version;
                return snapshot with { Value = "checkout" };
            }
        });

        Assert.Equal("1.0.0", seen);
        Assert.Equal("checkout", migrated.Value);
        // Finalize stamps the target identity in both places, so the result restores into V2.
        Assert.Equal(new PersistedMachineIdentity("cart", "2.0.0"), migrated.Machine);
        Assert.Equal("2.0.0", migrated.Version);
        Assert.True(V2.Restore(migrated).State.Matches("checkout"));
    }

    [Fact]
    public void A_wildcard_migration_handles_everything_else_including_an_unresolvable_identity()
    {
        var versions = MachineVersions.Create(V1, V2);
        var foreign = SnapshotOf(V1) with { Machine = new PersistedMachineIdentity("cart", "0.1.0"), Version = "0.1.0" };

        var migrated = versions.MigrateSnapshot(foreign, "2.0.0", new Dictionary<string, Func<PersistedSnapshot, PersistedSnapshot>>
        {
            // The wildcard receives the *raw* snapshot: it exists precisely for the case where
            // nothing could be resolved about it.
            [MachineVersions.Wildcard] = snapshot => snapshot with { Value = "checkout" }
        });

        Assert.Equal("2.0.0", migrated.Machine!.Version);
        Assert.Equal("checkout", migrated.Value);
    }

    [Fact]
    public void A_missing_migration_reports_which_hop_is_unhandled()
    {
        var versions = MachineVersions.Create(V1, V2);
        var empty = new Dictionary<string, Func<PersistedSnapshot, PersistedSnapshot>>();

        var error = Assert.Throws<InvalidOperationException>(
            () => versions.MigrateSnapshot(SnapshotOf(V1), "2.0.0", empty));
        Assert.Equal("No snapshot migration from version '1.0.0' to '2.0.0' for machine 'cart'.", error.Message);

        // With no wildcard and nothing to resolve, the parse failure is the real story and is what
        // surfaces (v6 rethrows `parseError`).
        var unresolvable = Assert.Throws<InvalidOperationException>(
            () => versions.MigrateSnapshot(SnapshotOf(V1) with { Machine = null, Version = null }, "2.0.0", empty));
        Assert.Equal("Persisted snapshot is missing machine identity.", unresolvable.Message);
    }

    [Fact]
    public void A_migration_target_must_be_a_registered_machine()
    {
        var versions = MachineVersions.Create(V1, V2);

        var error = Assert.Throws<InvalidOperationException>(
            () => versions.MigrateSnapshot(SnapshotOf(V1), "3.0.0", new Dictionary<string, Func<PersistedSnapshot, PersistedSnapshot>>()));
        Assert.Equal("Target version '3.0.0' is not backed by a machine for 'cart'.", error.Message);
    }

    // --- Event adaptation (v6 machineVersions.ts:420-496) ---

    [Fact]
    public void An_event_history_recorded_against_the_target_version_passes_through()
    {
        var versions = MachineVersions.Create(V1, V2);
        IReadOnlyList<MachineEvent> events = [new Ping()];

        Assert.Same(
            events,
            versions.AdaptEvents(events, "2.0.0", "2.0.0", new Dictionary<string, Func<IReadOnlyList<MachineEvent>, string, IReadOnlyList<MachineEvent>>>()));
    }

    [Fact]
    public void An_exact_adapter_wins_and_a_wildcard_catches_the_rest()
    {
        var versions = MachineVersions.Create(V1, V2);
        IReadOnlyList<MachineEvent> events = [new Ping()];

        var adapters = new Dictionary<string, Func<IReadOnlyList<MachineEvent>, string, IReadOnlyList<MachineEvent>>>
        {
            ["1.0.0"] = (history, from) => [new NamedEvent($"exact:{from}:{history.Count}")],
            [MachineVersions.Wildcard] = (history, from) => [new NamedEvent($"wildcard:{from}")]
        };

        Assert.Equal("exact:1.0.0:1", versions.AdaptEvents(events, "1.0.0", "2.0.0", adapters)[0].Type);
        // An unregistered source version is exactly what a wildcard adapter is for.
        Assert.Equal("wildcard:0.1.0", versions.AdaptEvents(events, "0.1.0", "2.0.0", adapters)[0].Type);
    }

    [Fact]
    public void A_missing_adapter_distinguishes_an_unhandled_hop_from_an_unknown_source()
    {
        var versions = MachineVersions.Create(V1, V2);
        IReadOnlyList<MachineEvent> events = [new Ping()];
        var empty = new Dictionary<string, Func<IReadOnlyList<MachineEvent>, string, IReadOnlyList<MachineEvent>>>();

        var unhandled = Assert.Throws<InvalidOperationException>(
            () => versions.AdaptEvents(events, "1.0.0", "2.0.0", empty));
        Assert.Equal("No event adapter from version '1.0.0' to '2.0.0' for machine 'cart'.", unhandled.Message);

        var unknown = Assert.Throws<InvalidOperationException>(
            () => versions.AdaptEvents(events, "0.1.0", "2.0.0", empty));
        Assert.Equal("Unknown event history source 'cart' version '0.1.0'.", unknown.Message);
    }
}
