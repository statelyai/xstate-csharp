using System.Collections.Immutable;

namespace XState;

/// <summary>What a <see cref="LogicalTimer"/> does when it fires.</summary>
public enum TimerKind
{
    /// <summary>The event is pushed onto the owning actor's internal queue.</summary>
    Raise,

    /// <summary>The event is sent to <see cref="LogicalTimer.Target"/> (an external delivery).</summary>
    SendTo
}

/// <summary>
/// A timer the machine believes is outstanding — the durable half of a delayed raise/send.
/// The host owns only the clock: when the delay elapses it sends <see cref="TimerEvent"/> with
/// this id back to the owning actor, and the machine decides what firing means.
/// </summary>
/// <param name="Target">The send target; null means the owning actor itself.</param>
public sealed record LogicalTimer(
    string Id,
    TimeSpan Delay,
    TimerKind Kind,
    MachineEvent Event,
    string? Target);

/// <summary>
/// A child actor the machine believes is running.
/// </summary>
/// <param name="Src">
/// The registered actor-source key the child's logic came from, or null for inline logic
/// (runnable, but not persistable).
/// </param>
/// <param name="Incarnation">
/// Token minted when this child was spawned. The host echoes it as the <c>SessionId</c> of the
/// done/error event it relays, so a completion from a previous incarnation of the same id can be
/// told apart from this one's.
/// </param>
public sealed record ChildRecord(string Id, string? Src, string Incarnation);

/// <summary>
/// An immutable machine snapshot: configuration + context + status, plus the durable ledgers
/// (children, timers, id counters) that let a replay reproduce the same effects. Produced by
/// <see cref="StateMachine{TContext}.GetInitialState"/> / <see cref="StateMachine{TContext}.Transition"/>.
/// </summary>
public sealed record State<TContext> : ISnapshot
{
    /// <summary>
    /// Ids of all active state nodes, including ancestors, in document order.
    /// </summary>
    public required IReadOnlyList<string> Configuration { get; init; }

    public required TContext Context { get; init; }

    public SnapshotStatus Status { get; init; } = SnapshotStatus.Active;

    /// <summary>Output of the machine when Status == Done (root final state's donedata).</summary>
    public object? Output { get; init; }

    public Exception? Error { get; init; }

    /// <summary>History node id → recorded configuration slice (document order).</summary>
    public ImmutableDictionary<string, ImmutableArray<string>> HistoryValue { get; init; } =
        ImmutableDictionary<string, ImmutableArray<string>>.Empty;

    internal StateMachine<TContext>? Machine { get; init; }

    // --- Durable ledgers carried across macrosteps (never part of snapshot equality) ---

    /// <summary>
    /// Next index <em>per generated-id prefix</em> for generated actor ids (<c>{prefix}:{n}</c>);
    /// monotonic across macrosteps. xstate v6 counterpart: <c>_nextActorIds</c> on the machine
    /// snapshot (<c>State.ts</c>), which replaced the single <c>_nextActorId</c> counter so that
    /// two differently-named child logics number independently and a replay reproduces the same
    /// ids regardless of interleaving.
    /// </summary>
    public ImmutableDictionary<string, int> NextActorIds { get; internal init; } =
        ImmutableDictionary.Create<string, int>(StringComparer.Ordinal);

    /// <summary>
    /// Next index for generated timer ids (<c>xstate.timer.auto.{n}</c>); monotonic across
    /// macrosteps. xstate v6 counterpart: <c>_nextTimerId</c> (<c>transitionActions.ts</c>).
    /// </summary>
    public int NextTimerId { get; internal init; }

    /// <summary>
    /// Next child incarnation token. Every spawn stamps
    /// <see cref="ChildRecord.Incarnation"/> from this counter and advances it, so a done/error
    /// event can be attributed to the exact child instance that produced it even when an id is
    /// reused.
    /// </summary>
    public int NextIncarnation { get; internal init; }

    /// <summary>Children believed to be running (invoked + spawned), in spawn order.</summary>
    public ImmutableArray<ChildRecord> Children { get; internal init; } = [];

    /// <summary>Logical timers believed to be outstanding, keyed by timer id.</summary>
    public ImmutableDictionary<string, LogicalTimer> Timers { get; internal init; } =
        ImmutableDictionary.Create<string, LogicalTimer>(StringComparer.Ordinal);

    /// <summary>
    /// Per-state input (xstate v6 <c>_stateInputs</c>): the value the transition that entered a
    /// state carried for it, keyed by state id. Entries are never pruned on exit (v6 keeps
    /// <c>_stateInputs</c> the same way): an inactive state's last input stays readable until the
    /// next transition into it overwrites it. Surfaced to that state's entry/exit actions, invoke
    /// input resolvers and final-state output resolvers as <see cref="ActionArgs{TContext}.Input"/>.
    /// </summary>
    public ImmutableDictionary<string, object?> StateInputs { get; internal init; } =
        ImmutableDictionary.Create<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// Whether the given state path/id is active. Accepts full ids ("machine.a.b"),
    /// relative paths ("a.b"), or explicit ids ("#a").
    /// </summary>
    public bool Matches(string statePath)
    {
        if (Configuration.Contains(statePath.StartsWith('#') ? statePath[1..] : statePath))
            return true;

        // Explicit ids and key paths are aliases of the same node, so resolve through the machine.
        return Machine?.ResolveStateId(statePath) is { } resolved && Configuration.Contains(resolved);
    }

    /// <summary>Union of the tags of every active state node (v6 <c>snapshot.tags</c>).</summary>
    public IReadOnlySet<string> Tags =>
        Machine is null
            ? new HashSet<string>()
            : Machine.NodesOf(Configuration).SelectMany(n => n.Tags).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether any active state node carries <paramref name="tag"/>.</summary>
    public bool HasTag(string tag) => Tags.Contains(tag);

    /// <summary>
    /// The <c>meta</c> of every active state node that declares one, keyed by state id
    /// (xstate v6 <c>snapshot.getMeta()</c>). Active nodes without meta are omitted.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Meta =>
        Machine is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : Machine.NodesOf(Configuration)
                .Where(n => n.Meta is not null)
                .ToDictionary(n => n.Id, n => n.Meta, StringComparer.Ordinal);

    /// <summary>
    /// The nested state value (v6 <c>snapshot.value</c> / <c>getStateValue</c>): the key of the
    /// active atomic child for a compound state, and a
    /// <see cref="Dictionary{TKey,TValue}"/> of region key → value otherwise.
    /// </summary>
    public object Value => Machine?.StateValueOf(Configuration) ?? new Dictionary<string, object>();

    /// <summary>Leaf (atomic/final) active state ids — the "state value" flattened, in document order.</summary>
    public IReadOnlyList<string> ActiveLeafStates =>
        Machine is null
            ? [.. Configuration]
            : [.. Machine.NodesOf(Configuration)
                .Where(n => n.Children.Count == 0 || n.Type is StateNodeType.Final)
                .Select(n => n.Id)];

    // --- Structural equality ---
    //
    // The compiler-generated record equality would compare the immutable collections by
    // reference (and produce an unstable hash), so two structurally identical snapshots
    // would compare unequal. The durable ledgers above are deliberately excluded: they are
    // bookkeeping the host reconciles against, not part of "which state is the machine in"
    // (xstate v6 likewise keeps children/timers out of its snapshot-equality tests, while
    // still persisting them).

    public bool Equals(State<TContext>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Status == other.Status &&
               ReferenceEquals(Machine, other.Machine) &&
               Configuration.SequenceEqual(other.Configuration) &&
               EqualityComparer<TContext>.Default.Equals(Context, other.Context) &&
               Equals(Output, other.Output) &&
               Equals(Error, other.Error) &&
               HistoryEquals(HistoryValue, other.HistoryValue);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Status);
        foreach (var id in Configuration)
        {
            hash.Add(id);
        }
        hash.Add(Context);
        return hash.ToHashCode();
    }

    private static bool HistoryEquals(
        ImmutableDictionary<string, ImmutableArray<string>> left,
        ImmutableDictionary<string, ImmutableArray<string>> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;

        foreach (var (key, value) in left)
        {
            // Recorded slices are in document order, so they compare as sequences — and a
            // rehydrated snapshot may carry an uninitialised (default) array, which reads as empty.
            if (!right.TryGetValue(key, out var otherValue) ||
                !Slice(value).SequenceEqual(Slice(otherValue)))
            {
                return false;
            }
        }

        return true;

        static ReadOnlySpan<string> Slice(ImmutableArray<string> value) =>
            value.IsDefault ? default : value.AsSpan();
    }
}

/// <summary>Result of a pure transition: next state + ordered side-effect intents.</summary>
public sealed record TransitionResult<TContext>(
    State<TContext> State,
    ImmutableArray<Effect> Effects);
