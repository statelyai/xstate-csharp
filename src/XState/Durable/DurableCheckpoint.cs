using System.Text.Json.Serialization;
using XState.Persistence;

namespace XState.Durable;

/// <summary>
/// Everything a worker needs to resume a durable execution, from
/// <see cref="DurableExecution{TContext}.Checkpoint"/>: the persisted snapshot, the next transition
/// index, and the root events received but not yet processed. A port of xstate v6
/// <c>DurableCheckpoint</c> (<c>durable/index.ts</c>). Persist the whole record atomically, and pass
/// it to <see cref="DurableExecution{TContext}.Restore(DurableCheckpoint, RestoreOptions{TContext}?)"/>.
/// <para>
/// Under <see cref="SnapshotJson.DefaultOptions"/> it is written as
/// <c>{ "snapshot": …, "nextTransitionIndex": n, "inbox": [{ "event": …, "source"?: { "@actor": … } }] }</c>.
/// </para>
/// </summary>
/// <param name="Snapshot">The persisted snapshot.</param>
/// <param name="NextTransitionIndex">The index the next transition gets.</param>
/// <param name="Inbox">
/// Root-addressed events that settled <see cref="DurableExecution{TContext}.ExecuteEffects"/>
/// batches captured and <see cref="DurableExecution{TContext}.WaitForEvent"/> has not handed out
/// yet, in capture order.
/// </param>
public sealed record DurableCheckpoint(
    [property: JsonPropertyName("snapshot")] PersistedSnapshot Snapshot,
    [property: JsonPropertyName("nextTransitionIndex")] int NextTransitionIndex,
    [property: JsonPropertyName("inbox")] IReadOnlyList<DurableInboxEntry> Inbox);

/// <summary>
/// A root-addressed event the execution received but has not processed, stored in a
/// <see cref="DurableCheckpoint"/> (v6 <c>DurableInboxEntry</c>).
/// </summary>
/// <param name="Event">The event. Its payload must be serializable data.</param>
/// <param name="Source">The sender's durable identity, when the host reported one.</param>
public sealed record DurableInboxEntry(
    [property: JsonPropertyName("event")] MachineEvent Event,
    [property: JsonPropertyName("source")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SerializedActorRef? Source = null);

/// <summary>
/// What <see cref="DurableExecution{TContext}.Restore(DurableCheckpoint, RestoreOptions{TContext}?)"/>
/// hands back: the rebuilt snapshot, the tagged effects that re-arm its pending timers, and the
/// children the host must re-create or re-attach (<see cref="RestoreResult{TContext}.Children"/>).
/// </summary>
/// <param name="Effects">
/// Timer re-arms tagged <c>"restore:{nextTransitionIndex}:{i}"</c>. Execute them with
/// <see cref="DurableExecution{TContext}.ExecuteEffects"/> before waiting or processing the next
/// event. Empty unless the restored snapshot is active.
/// </param>
public sealed record DurableRestoreResult<TContext>(
    State<TContext> State,
    IReadOnlyList<DurableEffect> Effects,
    IReadOnlyList<ChildToRestore> Children);
