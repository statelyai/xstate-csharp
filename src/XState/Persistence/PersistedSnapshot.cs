namespace XState.Persistence;

/// <summary>
/// The persisted form of an <see cref="Exception"/>: a durable snapshot carries what an error
/// <em>was</em>, not a live CLR object (stack traces and custom exception types do not survive a
/// process boundary, and xstate v6 persists errors the same way — as plain JSON).
/// </summary>
/// <param name="Type">The CLR type name of the original exception.</param>
/// <param name="Message">The exception message.</param>
public sealed record PersistedError(string Type, string? Message);

/// <summary>
/// The identity a persisted snapshot was produced by — xstate v6 <c>machine: { id, version }</c>
/// (<c>State.ts</c> <c>getPersistedSnapshot</c>). Restoring compares it against the machine it is
/// being restored into.
/// </summary>
public sealed record PersistedMachineIdentity(string Id, string? Version = null);

/// <summary>
/// One entry of a persisted snapshot's child ledger.
/// </summary>
public sealed record PersistedChild
{
    /// <summary>The child's id within its parent (the invoke/spawn id).</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The child's durable address (<see cref="ActorAddress"/>) — the identity that survives
    /// persist/restore, unlike any per-process session id.
    /// </summary>
    public required string Address { get; init; }

    /// <summary>
    /// The registered actor-source key the child's logic came from. Non-null for every
    /// persistable child; null only for an inline child persisted under
    /// <see cref="PersistOptions.AllowInlineActors"/>, which cannot be restored.
    /// </summary>
    public string? Src { get; init; }

    /// <summary>
    /// The incarnation token the parent minted for this child (<see cref="ChildRecord.Incarnation"/>).
    /// <para>
    /// Deviation from xstate v6 (<c>State.ts</c>:630-640), which persists an <c>incarnation</c>
    /// only for remote handles because its token is a per-process <c>sessionId</c> and embedding a
    /// local one would make snapshots nondeterministic. This port's incarnation is a deterministic
    /// per-snapshot counter, so it is durable data and is always persisted; dropping it would
    /// break done/error attribution after a restore.
    /// </para>
    /// </summary>
    public string? Incarnation { get; init; }

    /// <summary>The child's own persisted snapshot, when the parent embedded it.</summary>
    public PersistedSnapshot? Snapshot { get; init; }

    /// <summary>
    /// True when the child is referenced by address only: its state lives with another runtime (or
    /// the parent chose not to embed it), and restoring yields a handle to re-attach rather than
    /// logic to re-create.
    /// </summary>
    public bool Remote { get; init; }
}

/// <summary>
/// One entry of a persisted snapshot's timer ledger — a delayed raise or a delayed send the
/// machine believes is still outstanding.
/// </summary>
public sealed record PersistedTimer
{
    public required string Id { get; init; }

    /// <summary>The declared delay in milliseconds.</summary>
    public required double DelayMs { get; init; }

    /// <summary>
    /// <c>@xstate.raise</c> or <c>@xstate.sendTo</c> — the same discriminants v6 persists
    /// (<c>State.ts</c>:690).
    /// </summary>
    public required string Type { get; init; }

    public required MachineEvent Event { get; init; }

    /// <summary>
    /// Null for the owning actor itself, <see cref="EffectTarget.Parent"/> for its parent, or a
    /// child id.
    /// <para>
    /// Deviation from v6, which writes the parent target as the object <c>{ type: 'parent' }</c>
    /// to keep it apart from a child literally named <c>parent</c>. This port keeps the string
    /// alias the effect layer already uses; <c>#_parent</c> is not a legal actor id, so the two
    /// still cannot collide.
    /// </para>
    /// </summary>
    public string? Target { get; init; }
}

/// <summary>
/// A machine snapshot reduced to plain data: everything a different process — or the same process
/// after a restart — needs to reconstruct the actor, and nothing that only means something inside
/// the process that produced it (no delegates, no live actor references, no CLR exceptions).
/// <para>
/// Produced by <see cref="StateMachine{TContext}.Persist"/> and consumed by
/// <see cref="StateMachine{TContext}.Restore"/>. It is deliberately
/// <c>System.Text.Json</c>-friendly: see <see cref="SnapshotJson"/> for options that round-trip
/// the <see cref="MachineEvent"/>s it carries.
/// </para>
/// </summary>
public sealed record PersistedSnapshot
{
    /// <summary>
    /// <c>"active"</c>, <c>"done"</c>, <c>"error"</c> or <c>"stopped"</c> — the v6 spelling of
    /// <see cref="SnapshotStatus"/>.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>The machine's output when <see cref="Status"/> is <c>"done"</c>.</summary>
    public object? Output { get; init; }

    /// <summary>The failure when <see cref="Status"/> is <c>"error"</c>.</summary>
    public PersistedError? Error { get; init; }

    /// <summary>
    /// The nested state value (<see cref="State{TContext}.Value"/>): a string for a compound state
    /// whose active child is atomic, a map of region key → value otherwise.
    /// </summary>
    public required object Value { get; init; }

    /// <summary>
    /// The machine context, as-is. The host serializes it with its own options, and supplies
    /// <see cref="RestoreOptions{TContext}.ContextConverter"/> to turn whatever its deserializer
    /// produced back into <c>TContext</c>.
    /// </summary>
    public object? Context { get; init; }

    /// <summary>Children the snapshot believes are running, in spawn order.</summary>
    public IReadOnlyList<PersistedChild> Children { get; init; } = [];

    /// <summary>Outstanding logical timers, keyed by timer id.</summary>
    public IReadOnlyDictionary<string, PersistedTimer> Timers { get; init; } =
        new Dictionary<string, PersistedTimer>(StringComparer.Ordinal);

    /// <summary>
    /// History node id → recorded state ids.
    /// <para>
    /// Deviation from v6 (<c>State.ts</c> <c>serializeHistoryValue</c>), which writes
    /// <c>[{ id }]</c> objects. Plain ids carry the same information in a smaller shape; there is
    /// no state-node data to preserve beyond the id.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> HistoryValue { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>Generated-actor-id counters per prefix (<see cref="State{TContext}.NextActorIds"/>).</summary>
    public IReadOnlyDictionary<string, int> NextActorIds { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Next generated timer index (<see cref="State{TContext}.NextTimerId"/>).</summary>
    public int NextTimerId { get; init; }

    /// <summary>Next child incarnation token (<see cref="State{TContext}.NextIncarnation"/>).</summary>
    public int NextIncarnation { get; init; }

    /// <summary>
    /// Per-state input, written only when non-empty (v6 <c>State.ts</c>:704 writes
    /// <c>stateInputs</c> only for a non-empty map).
    /// </summary>
    public IReadOnlyDictionary<string, object?>? StateInputs { get; init; }

    /// <summary>
    /// The machine that produced this snapshot. Always written by
    /// <see cref="StateMachine{TContext}.Persist"/>; nullable because a snapshot written before
    /// the nested identity existed (or by a foreign producer) carries none, and v6 restores those
    /// by falling back to <see cref="Version"/> (<c>StateMachine.ts</c>:1357-1372).
    /// </summary>
    public PersistedMachineIdentity? Machine { get; init; }

    /// <summary>
    /// Legacy top-level version mirror. v6 writes <c>version</c> alongside
    /// <c>machine.version</c> (<c>State.ts</c>:709-713) for snapshots written before the nested
    /// identity existed; restoring accepts it and rejects a snapshot whose two versions disagree.
    /// </summary>
    public string? Version { get; init; }
}

/// <summary>The v6 spellings of <see cref="SnapshotStatus"/>, as persisted.</summary>
public static class PersistedStatus
{
    public const string Active = "active";
    public const string Done = "done";
    public const string Error = "error";
    public const string Stopped = "stopped";

    /// <summary>The persisted spelling of <paramref name="status"/>.</summary>
    public static string Of(SnapshotStatus status) => status switch
    {
        SnapshotStatus.Active => Active,
        SnapshotStatus.Done => Done,
        SnapshotStatus.Error => Error,
        SnapshotStatus.Stopped => Stopped,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    /// <summary>Parses a persisted status spelling.</summary>
    public static SnapshotStatus Parse(string status) => status switch
    {
        Active => SnapshotStatus.Active,
        Done => SnapshotStatus.Done,
        Error => SnapshotStatus.Error,
        Stopped => SnapshotStatus.Stopped,
        _ => throw new InvalidOperationException(
            $"Persisted snapshot has an unknown status '{status}'.")
    };
}
