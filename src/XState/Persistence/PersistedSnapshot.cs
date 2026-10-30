using System.Text.Json;
using System.Text.Json.Serialization;

namespace XState.Persistence;

/// <summary>
/// The persisted form of an <see cref="Exception"/>: a durable snapshot carries what an error
/// <em>was</em>, not a live CLR object (stack traces and custom exception types do not survive a
/// process boundary, and xstate v6 persists errors the same way — as plain JSON).
/// </summary>
/// <param name="Type">The CLR type name of the original exception.</param>
/// <param name="Message">The exception message.</param>
public sealed record PersistedError(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>
/// The identity a persisted snapshot was produced by — xstate v6 <c>machine: { id, version }</c>
/// (<c>State.ts</c> <c>getPersistedSnapshot</c>), written only when the machine declares a
/// version. Restoring compares it against the machine it is being restored into; a persisted
/// <c>machine</c> lacking either key is an <see cref="InvalidSnapshotException"/>.
/// </summary>
public sealed record PersistedMachineIdentity(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string? Version = null);

/// <summary>
/// One entry of a persisted snapshot's child ledger. On the wire (<see cref="SnapshotJson"/>)
/// <c>children</c> is an object keyed by <see cref="Id"/>, each entry
/// <c>{ address, src?, registryKey?, syncSnapshot?, snapshot? | remote: true, incarnation? }</c>
/// — v6 <c>persisted-snapshot/v1</c>.
/// </summary>
[JsonConverter(typeof(PersistedChildConverter))]
public sealed record PersistedChild
{
    /// <summary>The child's id within its parent (the invoke/spawn id) — the entry's key on the wire.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The child's durable address (<see cref="ActorAddress"/>) — the identity that survives
    /// persist/restore, unlike any per-process session id.
    /// </summary>
    public required string Address { get; init; }

    /// <summary>
    /// The registered actor-source key the child's logic came from. Null (omitted on the wire)
    /// for an inline invoke, which restores through the invoke definition with this child's id
    /// among the active states (v6 ceeebb056c), and for an inline spawned child persisted under
    /// <see cref="PersistOptions.AllowInlineActors"/>, which cannot be restored.
    /// </summary>
    public string? Src { get; init; }

    /// <summary>
    /// v6 <c>registryKey</c> (the child's <c>systemId</c>). This port has no actor-system registry,
    /// so it never writes one; a foreign snapshot's value round-trips.
    /// </summary>
    public string? RegistryKey { get; init; }

    /// <summary>Whether the parent observes this child's snapshots (v6 <c>syncSnapshot</c>).</summary>
    public bool SyncSnapshot { get; init; }

    /// <summary>
    /// The incarnation token the parent minted for this child (<see cref="ChildRecord.Incarnation"/>).
    /// <para>
    /// v6 persists a public <c>incarnation</c> only for remote handles (the schema requires
    /// <c>incarnation</c> ⇒ <c>remote</c>), because its token is a per-process <c>sessionId</c>.
    /// This port's incarnation is a deterministic per-snapshot counter, so it is durable data:
    /// a remote child writes it as <c>incarnation</c>, an embedded child under the private key
    /// <c>_incarnation</c> (port extension; dropping it would break done/error attribution after a
    /// restore). Both keys are accepted on read.
    /// </para>
    /// </summary>
    public string? Incarnation { get; init; }

    /// <summary>The child's own persisted snapshot, when the parent embedded it.</summary>
    public PersistedSnapshot? Snapshot { get; init; }

    /// <summary>
    /// True when the child is referenced by address only: its state lives with another runtime (or
    /// the parent chose not to embed it), and restoring yields a handle to re-attach rather than
    /// logic to re-create. Written as <c>remote: true</c>; omitted for an embedded child.
    /// </summary>
    public bool Remote { get; init; }
}

/// <summary>
/// One entry of a persisted snapshot's timer ledger — a delayed raise or a delayed send the
/// machine believes is still outstanding. Wire shape (<see cref="SnapshotJson"/>):
/// <c>{ id?, seq?, delay, type, event, target, scheduledAt? }</c>.
/// </summary>
[JsonConverter(typeof(PersistedTimerConverter))]
public sealed record PersistedTimer
{
    /// <summary>The explicit id the raise/send was scheduled with, if any.</summary>
    public string? Id { get; init; }

    /// <summary>
    /// The timer sequence number of an id-less delayed raise/send. Built-in <c>after</c>/
    /// <c>timeout</c> timers have neither id nor seq; their event identifies them.
    /// </summary>
    public int? Seq { get; init; }

    /// <summary>The declared delay in milliseconds (wire <c>delay</c>).</summary>
    public required double DelayMs { get; init; }

    /// <summary>
    /// <c>@xstate.raise</c> or <c>@xstate.sendTo</c> — the same discriminants v6 persists
    /// (<c>State.ts</c>:690).
    /// </summary>
    public required string Type { get; init; }

    public required MachineEvent Event { get; init; }

    /// <summary>
    /// Null for the owning actor itself (wire <c>"self"</c>), <see cref="EffectTarget.Parent"/>
    /// for its parent (wire <c>{ "type": "parent" }</c>), or a child id. Any other string is
    /// written verbatim; e.g. a <c>#_registry:key</c> target is a port extension with no v6
    /// counterpart.
    /// </summary>
    public string? Target { get; init; }

    /// <summary>
    /// Set instead of <see cref="Target"/> when the wire target was a serialized actor reference
    /// (<c>{ "$actor": address }</c>). Restore resolves it to the restored child with that address.
    /// </summary>
    public SerializedActorRef? TargetActor { get; init; }

    /// <summary>
    /// The timer's absolute start in Unix milliseconds (v6 <c>scheduledAt</c>). Host-supplied:
    /// the pure core never sets it, only carries it through
    /// <see cref="LogicalTimer.ScheduledAt"/> so re-persisting keeps the original deadline.
    /// </summary>
    public double? ScheduledAt { get; init; }
}

/// <summary>
/// A machine snapshot reduced to plain data: everything a different process — or the same process
/// after a restart — needs to reconstruct the actor, and nothing that only means something inside
/// the process that produced it (no delegates, no live actor references, no CLR exceptions).
/// <para>
/// Through <see cref="SnapshotJson"/> it serializes as v6's <c>persisted-snapshot/v1</c> envelope:
/// <c>status</c>, <c>value</c>, <c>context</c>, <c>output</c>?, <c>error</c>?,
/// <c>historyValue</c>, <c>stateInputs</c>?, <c>children</c>, <c>timers</c>, <c>machine</c>?,
/// and the private counters <c>_nextActorIds</c>, <c>_nextTimerId</c>, <c>_nextIncarnation</c>.
/// Unknown fields are ignored on read.
/// </para>
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
    [JsonPropertyName("status"), JsonPropertyOrder(0)]
    public required string Status { get; init; }

    /// <summary>The machine's output when <see cref="Status"/> is <c>"done"</c>.</summary>
    [JsonPropertyName("output"), JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Output { get; init; }

    /// <summary>The failure when <see cref="Status"/> is <c>"error"</c>.</summary>
    [JsonPropertyName("error"), JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PersistedError? Error { get; init; }

    /// <summary>
    /// The nested state value (<see cref="State{TContext}.Value"/>): a string for a compound state
    /// whose active child is atomic, a map of region key → value otherwise.
    /// </summary>
    [JsonPropertyName("value"), JsonPropertyOrder(1)]
    public required object Value { get; init; }

    /// <summary>
    /// The machine context, as-is. The host serializes it with its own options, and supplies
    /// <see cref="RestoreOptions{TContext}.ContextConverter"/> to turn whatever its deserializer
    /// produced back into <c>TContext</c>. A <see cref="SerializedActorRef"/> inside it is written
    /// as <c>{ "$actor": address }</c> and read back as a <see cref="SerializedActorRef"/>.
    /// </summary>
    [JsonPropertyName("context"), JsonPropertyOrder(2)]
    public object? Context { get; init; }

    /// <summary>
    /// Children the snapshot believes are running, in spawn order. Written as an object keyed by
    /// child id.
    /// </summary>
    [JsonPropertyName("children"), JsonPropertyOrder(7)]
    [JsonConverter(typeof(PersistedChildMapConverter))]
    public IReadOnlyList<PersistedChild> Children { get; init; } = [];

    /// <summary>
    /// Outstanding logical timers, in scheduling order. The pre-alpha.68 shape — an object keyed
    /// by timer id — is rejected on deserialization.
    /// </summary>
    [JsonPropertyName("timers"), JsonPropertyOrder(8)]
    [JsonConverter(typeof(PersistedTimerListConverter))]
    public IReadOnlyList<PersistedTimer> Timers { get; init; } = [];

    /// <summary>
    /// History node id → recorded state ids. Written as v6 <c>{ [historyId]: [{ "id": … }] }</c>
    /// (<c>State.ts</c> <c>serializeHistoryValue</c>).
    /// </summary>
    [JsonPropertyName("historyValue"), JsonPropertyOrder(5)]
    [JsonConverter(typeof(PersistedHistoryConverter))]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> HistoryValue { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>Generated-actor-id counters per prefix (<see cref="State{TContext}.NextActorIds"/>).</summary>
    [JsonPropertyName("_nextActorIds"), JsonPropertyOrder(10)]
    [JsonConverter(typeof(PersistedCounterMapConverter))]
    public IReadOnlyDictionary<string, int> NextActorIds { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Next timer sequence number (<see cref="State{TContext}.NextTimerId"/>).</summary>
    [JsonPropertyName("_nextTimerId"), JsonPropertyOrder(11)]
    public int NextTimerId { get; init; }

    /// <summary>Next child incarnation token (<see cref="State{TContext}.NextIncarnation"/>).</summary>
    [JsonPropertyName("_nextIncarnation"), JsonPropertyOrder(12)]
    public int NextIncarnation { get; init; }

    /// <summary>
    /// Per-state input, written only when non-empty (v6 <c>State.ts</c>:704 writes
    /// <c>stateInputs</c> only for a non-empty map).
    /// </summary>
    [JsonPropertyName("stateInputs"), JsonPropertyOrder(6)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, object?>? StateInputs { get; init; }

    /// <summary>
    /// The machine that produced this snapshot — written only when the machine declares a
    /// version (v6 <c>State.ts</c>), so an unversioned machine's snapshot carries none.
    /// </summary>
    [JsonPropertyName("machine"), JsonPropertyOrder(9)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PersistedMachineIdentity? Machine { get; init; }
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
        _ => throw new InvalidSnapshotException(
            $"Persisted snapshot has an unknown status '{status}'.")
    };
}

/// <summary>
/// Reads <see cref="PersistedSnapshot.Timers"/> as a list, rejecting the old record shape
/// (an object keyed by timer id) with an <see cref="InvalidSnapshotException"/>.
/// </summary>
internal sealed class PersistedTimerListConverter : JsonConverter<IReadOnlyList<PersistedTimer>>
{
    // A JSON null must reach Read so it is rejected like any other wrong shape.
    public override bool HandleNull => true;

    public override IReadOnlyList<PersistedTimer> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.StartObject)
        {
            throw new InvalidSnapshotException(
                "Persisted 'timers' is an object keyed by timer id — the pre-alpha.68 record shape. " +
                "Timers are now an ordered list; re-persist the snapshot with this version.");
        }

        if (reader.TokenType is not JsonTokenType.StartArray)
        {
            throw new InvalidSnapshotException("Persisted 'timers' must be an array.");
        }

        var timers = new List<PersistedTimer>();
        while (reader.Read() && reader.TokenType is not JsonTokenType.EndArray)
        {
            timers.Add(JsonSerializer.Deserialize<PersistedTimer>(ref reader, options)
                       ?? throw new InvalidSnapshotException("A persisted timer cannot be null."));
        }

        return timers;
    }

    public override void Write(
        Utf8JsonWriter writer, IReadOnlyList<PersistedTimer> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var timer in value)
        {
            JsonSerializer.Serialize(writer, timer, options);
        }

        writer.WriteEndArray();
    }
}

/// <summary>Reads <c>_nextActorIds</c>, rejecting <c>null</c> and non-object shapes as malformed.</summary>
internal sealed class PersistedCounterMapConverter : JsonConverter<IReadOnlyDictionary<string, int>>
{
    public override bool HandleNull => true;

    public override IReadOnlyDictionary<string, int> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartObject)
        {
            throw new InvalidSnapshotException("Persisted '_nextActorIds' must be an object of prefix → counter.");
        }

        return JsonSerializer.Deserialize<Dictionary<string, int>>(ref reader, options)
               ?? throw new InvalidSnapshotException("Persisted '_nextActorIds' must be an object of prefix → counter.");
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, int> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (prefix, next) in value)
        {
            writer.WriteNumber(prefix, next);
        }
        writer.WriteEndObject();
    }
}
