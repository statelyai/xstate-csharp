using System.Collections.Immutable;

namespace XState.Persistence;

/// <summary>
/// How <see cref="StateMachine{TContext}.Persist"/> treats the parts of a snapshot the pure core
/// does not own.
/// </summary>
public sealed record PersistOptions
{
    /// <summary>
    /// Whether each child's own persisted snapshot is embedded (a whole-tree checkpoint) or the
    /// child is recorded by address only. The pure core holds no live children, so embedding needs
    /// <see cref="ChildSnapshot"/> to supply them.
    /// </summary>
    public bool EmbedChildren { get; init; } = true;

    /// <summary>
    /// Supplies the persisted snapshot of one live child. The core knows a child exists and what
    /// source it came from, but never holds the child's state — that lives with whatever host is
    /// running it, which is the only thing that can answer this.
    /// </summary>
    public Func<ChildRecord, PersistedSnapshot?>? ChildSnapshot { get; init; }

    /// <summary>
    /// Root address to build child addresses from. Defaults to
    /// <see cref="ActorAddress.Root(string?)"/> of the machine's id; a nested embed passes the
    /// embedding parent's address so the whole tree shares one address space.
    /// </summary>
    public string? RootAddress { get; init; }

    /// <summary>
    /// Persist a child whose logic was supplied inline (no registered source key) instead of
    /// throwing. Mirrors v6's <c>__unsafeAllowInlineActors</c> (<c>State.ts</c>:606-613): the
    /// resulting snapshot records the child but can never be restored, because nothing names the
    /// logic to re-create.
    /// </summary>
    public bool AllowInlineActors { get; init; }
}

/// <summary>
/// A child a restored snapshot expects the host to bring back: what it is, where it lives, and —
/// unless it is <see cref="Remote"/> — the logic to re-create it from.
/// </summary>
/// <param name="Logic">
/// The logic resolved from the machine's registered <see cref="StateMachine{TContext}.Sources"/>,
/// or null for a <see cref="Remote"/> child, whose state lives with another runtime and which the
/// host re-attaches to by <paramref name="Address"/> rather than re-creating.
/// </param>
public sealed record ChildToRestore(
    string Id,
    string? Src,
    string Address,
    string Incarnation,
    PersistedSnapshot? Snapshot,
    IActorLogic? Logic,
    bool Remote);

/// <summary>
/// What <see cref="StateMachine{TContext}.Restore"/> hands back: the rebuilt snapshot, plus the
/// two things the machine believes exist but cannot itself re-create — its children and its
/// outstanding timers.
/// </summary>
public sealed record RestoreResult<TContext>(
    State<TContext> State,
    IReadOnlyList<ChildToRestore> Children,
    IReadOnlyList<LogicalTimer> Timers) : IRestoreResult
{
    ISnapshot IRestoreResult.State => State;
}

/// <summary>Context-type-agnostic view of a <see cref="RestoreResult{TContext}"/> (see <see cref="IMachineLogic.Restore"/>).</summary>
public interface IRestoreResult
{
    ISnapshot State { get; }
    IReadOnlyList<ChildToRestore> Children { get; }
    IReadOnlyList<LogicalTimer> Timers { get; }
}

/// <summary>How <see cref="StateMachine{TContext}.Restore"/> bridges the gaps a serializer leaves.</summary>
public sealed record RestoreOptions<TContext>
{
    /// <summary>
    /// Migrates a snapshot written by a different version of this machine. Called with the raw
    /// snapshot and the version it declared (null when it declared none); the returned snapshot is
    /// restored in its place. Without it, a version mismatch throws — xstate v6
    /// <c>StateMachine.ts</c>:1391-1402.
    /// </summary>
    public Func<PersistedSnapshot, string?, PersistedSnapshot>? Migrate { get; init; }

    /// <summary>
    /// Turns <see cref="PersistedSnapshot.Context"/> into <c>TContext</c>. Required whenever the
    /// snapshot came through a deserializer that cannot know the context type — with
    /// <c>System.Text.Json</c>, <see cref="PersistedSnapshot.Context"/> is a
    /// <c>JsonElement</c> (or, under <see cref="SnapshotJson"/>'s options, a plain
    /// dictionary/list tree), never a <c>TContext</c>. Omitted, the value is cast directly, which
    /// is correct only for an in-process persist/restore.
    /// </summary>
    public Func<object?, TContext>? ContextConverter { get; init; }

    /// <summary>Root address override; see <see cref="PersistOptions.RootAddress"/>.</summary>
    public string? RootAddress { get; init; }
}

/// <summary>Internal shape shared by persist and restore while walking a nested state value.</summary>
internal static class StateValueShape
{
    /// <summary>
    /// Reads a nested state value node as either a leaf key or a map of child key → value,
    /// accepting the shapes a round-trip through a serializer can produce
    /// (<see cref="System.Text.Json.JsonElement"/>, or any dictionary) as well as the
    /// <see cref="State{TContext}.Value"/> shape itself.
    /// </summary>
    public static bool TryAsKey(object? value, out string key)
    {
        switch (value)
        {
            case string s:
                key = s;
                return true;
            case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } e:
                key = e.GetString()!;
                return true;
            default:
                key = string.Empty;
                return false;
        }
    }

    /// <summary>The child key → value entries of a non-leaf value node; empty for anything else.</summary>
    public static IEnumerable<KeyValuePair<string, object?>> AsMap(object? value)
    {
        switch (value)
        {
            case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } e:
                foreach (var property in e.EnumerateObject())
                {
                    yield return new KeyValuePair<string, object?>(property.Name, property.Value);
                }

                break;

            case System.Collections.IDictionary dictionary:
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is string name)
                    {
                        yield return new KeyValuePair<string, object?>(name, entry.Value);
                    }
                }

                break;
        }
    }

    /// <summary>Normalizes a history ledger for the snapshot record.</summary>
    public static ImmutableDictionary<string, ImmutableArray<string>> ToHistory(
        IEnumerable<KeyValuePair<string, ImmutableArray<string>>> entries) =>
        entries.ToImmutableDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
}
