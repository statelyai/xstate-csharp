using System.Text.Json;

namespace XState.Persistence;

/// <summary>
/// A snapshot resolved to the machine version that wrote it.
/// </summary>
public sealed record ParsedSnapshot(
    PersistedMachineIdentity Identity,
    IMachineLogic Machine,
    PersistedSnapshot Snapshot);

/// <summary>
/// A registry of the versions of one machine that are still allowed to appear in storage — a port
/// of xstate v6 <c>machineVersions()</c> (<c>machineVersions.ts</c>:320-573).
/// <para>
/// A long-lived actor outlives the code that created it: snapshots written months ago by an older
/// machine keep arriving. This turns "which machine wrote this?" into an answerable question, and
/// gives the two operations that follow from it a home — migrating an old snapshot forward, and
/// adapting an old event history.
/// </para>
/// <para>
/// Deviation from v6: v6 entries may be schema-only descriptors and every step validates through a
/// Standard Schema. This port has no schema abstraction, so entries are machines and there is no
/// validation step — the invariants, the identity resolution order and the migration/adaptation
/// dispatch are ported; schema conformance is the caller's business.
/// </para>
/// </summary>
public sealed class MachineVersions
{
    private readonly Dictionary<string, IMachineLogic> _byVersion = new(StringComparer.Ordinal);
    private readonly string? _unversioned;

    private MachineVersions(IReadOnlyList<IMachineLogic> machines, string? unversioned)
    {
        ArgumentNullException.ThrowIfNull(machines);
        if (machines.Count == 0)
        {
            throw new ArgumentException("At least one machine version is required.", nameof(machines));
        }

        MachineId = machines[0].Id;

        foreach (var machine in machines)
        {
            if (machine.Version is not { } version)
            {
                throw new InvalidOperationException($"Machine '{machine.Id}' must define a version.");
            }

            if (version == Wildcard)
            {
                throw new InvalidOperationException(
                    "Machine version '*' is reserved for wildcard migrations.");
            }

            if (machine.Id != MachineId)
            {
                throw new InvalidOperationException(
                    $"Machine '{machine.Id}' does not match machine ID '{MachineId}'.");
            }

            if (!_byVersion.TryAdd(version, machine))
            {
                throw new InvalidOperationException(
                    $"Duplicate machine identity '{machine.Id}' version '{version}'.");
            }
        }

        if (unversioned is not null && !_byVersion.ContainsKey(unversioned))
        {
            throw new InvalidOperationException(
                $"Unversioned snapshot version '{unversioned}' is not retained for machine '{MachineId}'.");
        }

        _unversioned = unversioned;
    }

    /// <summary>The version key standing for "any source version" in a migration/adapter map.</summary>
    public const string Wildcard = "*";

    /// <summary>The id shared by every registered version.</summary>
    public string MachineId { get; }

    /// <summary>The registered versions.</summary>
    public IReadOnlyCollection<string> Versions => _byVersion.Keys;

    /// <summary>The machine registered under <paramref name="version"/>, or null.</summary>
    public IMachineLogic? Get(string version) =>
        _byVersion.TryGetValue(version, out var machine) ? machine : null;

    /// <summary>Registers the versions of one machine.</summary>
    public static MachineVersions Create(params IMachineLogic[] machines) => new(machines, null);

    /// <summary>
    /// Registers the versions of one machine.
    /// </summary>
    /// <param name="unversioned">
    /// The version a snapshot that declares none is assumed to be — the version that was live
    /// before versioning was introduced. It must itself be registered.
    /// </param>
    public static MachineVersions Create(IReadOnlyList<IMachineLogic> machines, string? unversioned = null) =>
        new(machines, unversioned);

    /// <summary>
    /// Resolves which registered version wrote <paramref name="snapshot"/> — v6
    /// <c>machineVersions.ts</c>:361-419. Identity comes from the nested
    /// <see cref="PersistedSnapshot.Machine"/>, else the legacy top-level
    /// <see cref="PersistedSnapshot.Version"/>, else the configured <c>unversioned</c> fallback.
    /// <para>
    /// Deviation from v6, which treats a nested <c>machine</c> without a string <c>version</c> as
    /// an invalid identity: this port's <see cref="StateMachine{TContext}.Persist"/> always writes
    /// the nested identity (v6 omits it entirely for an unversioned machine), so a null nested
    /// version falls through to the same resolution chain rather than failing.
    /// </para>
    /// </summary>
    public ParsedSnapshot ParseSnapshot(PersistedSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var id = snapshot.Machine?.Id ?? MachineId;
        var version = snapshot.Machine?.Version ?? snapshot.Version ?? _unversioned ??
            throw new InvalidOperationException("Persisted snapshot is missing machine identity.");

        if (snapshot.Version is { } legacy && legacy != version)
        {
            throw new InvalidOperationException(
                $"Persisted snapshot version '{legacy}' conflicts with machine version '{version}'.");
        }

        if (id != MachineId || !_byVersion.TryGetValue(version, out var machine))
        {
            throw new InvalidOperationException(
                $"Unknown machine identity '{id}' version '{version}'.");
        }

        var identity = new PersistedMachineIdentity(id, version);
        return new ParsedSnapshot(identity, machine, snapshot with { Machine = identity });
    }

    /// <summary>
    /// Deserializes <paramref name="raw"/> and resolves its identity. Uses
    /// <see cref="SnapshotJson.DefaultOptions"/> when no options are supplied.
    /// </summary>
    public ParsedSnapshot ParseSnapshot(JsonElement raw, JsonSerializerOptions? options = null)
    {
        if (raw.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidOperationException("Persisted snapshot is missing machine identity.");
        }

        return ParseSnapshot(raw.Deserialize<PersistedSnapshot>(options ?? SnapshotJson.DefaultOptions())!);
    }

    /// <summary>
    /// Brings <paramref name="raw"/> forward to <paramref name="to"/>: a snapshot already at the
    /// target version passes through, an exact migration handles its own source version, and a
    /// <see cref="Wildcard"/> migration handles everything else — including a snapshot whose
    /// identity could not be resolved at all, which is the case a wildcard exists for (v6
    /// <c>machineVersions.ts</c>:497-573).
    /// </summary>
    public PersistedSnapshot MigrateSnapshot(
        PersistedSnapshot raw,
        string to,
        IReadOnlyDictionary<string, Func<PersistedSnapshot, PersistedSnapshot>> migrations)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(migrations);

        var target = _byVersion.TryGetValue(to, out var machine)
            ? machine
            : throw new InvalidOperationException(
                $"Target version '{to}' is not backed by a machine for '{MachineId}'.");

        ParsedSnapshot? parsed = null;
        Exception? parseError = null;
        try
        {
            parsed = ParseSnapshot(raw);
        }
        catch (InvalidOperationException error)
        {
            parseError = error;
        }

        if (parsed is not null)
        {
            if (parsed.Identity.Version == to)
            {
                return Finalize(parsed.Snapshot, target);
            }

            if (migrations.TryGetValue(parsed.Identity.Version!, out var exact))
            {
                return Finalize(exact(parsed.Snapshot), target);
            }
        }

        if (migrations.TryGetValue(Wildcard, out var wildcard))
        {
            return Finalize(wildcard(raw), target);
        }

        throw parsed is not null
            ? new InvalidOperationException(
                $"No snapshot migration from version '{parsed.Identity.Version}' to '{to}' for " +
                $"machine '{target.Id}'.")
            : parseError!;
    }

    /// <summary>
    /// Brings an event history recorded against version <paramref name="from"/> forward to
    /// <paramref name="to"/> — the same dispatch as
    /// <see cref="MigrateSnapshot"/> (v6 <c>machineVersions.ts</c>:420-496), minus the schema
    /// validation of the source and adapted events, which this port has no schema layer for.
    /// </summary>
    /// <param name="adapters">
    /// Keyed by source version, or <see cref="Wildcard"/>. Each receives the events and the
    /// version they were recorded against.
    /// </param>
    public IReadOnlyList<MachineEvent> AdaptEvents(
        IReadOnlyList<MachineEvent> events,
        string from,
        string to,
        IReadOnlyDictionary<string, Func<IReadOnlyList<MachineEvent>, string, IReadOnlyList<MachineEvent>>> adapters)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(adapters);

        var target = _byVersion.TryGetValue(to, out var machine)
            ? machine
            : throw new InvalidOperationException(
                $"Target version '{to}' is not backed by a machine for '{MachineId}'.");

        var known = _byVersion.ContainsKey(from);

        if (known)
        {
            if (from == to)
            {
                return events;
            }

            if (adapters.TryGetValue(from, out var exact))
            {
                return exact(events, from);
            }
        }

        if (adapters.TryGetValue(Wildcard, out var wildcard))
        {
            return wildcard(events, from);
        }

        throw known
            ? new InvalidOperationException(
                $"No event adapter from version '{from}' to '{to}' for machine '{target.Id}'.")
            : new InvalidOperationException(
                $"Unknown event history source '{MachineId}' version '{from}'.");
    }

    /// <summary>Stamps the target identity onto a migrated snapshot (v6 <c>finalizeSnapshot</c>).</summary>
    private static PersistedSnapshot Finalize(PersistedSnapshot snapshot, IMachineLogic target) =>
        snapshot with
        {
            Machine = new PersistedMachineIdentity(target.Id, target.Version),
            Version = target.Version
        };
}
