namespace XState;

/// <summary>
/// An immutable machine definition. The pure interpretation core:
/// <c>(machine, state, event) =&gt; (nextState, effects)</c>.
/// </summary>
public sealed partial class StateMachine<TContext> : IMachineLogic
{
    public required string Id { get; init; }

    /// <summary>
    /// Machine version (xstate v6 <c>machineVersion</c>). A durable host compares it against the
    /// version recorded on a persisted snapshot before restoring.
    /// </summary>
    public string? Version { get; init; }

    public required StateNode<TContext> Root { get; init; }
    public required Func<object?, TContext> ContextFactory { get; init; }

    /// <summary>
    /// Registered actor sources by key. An invoke/spawn resolved from this map records the key as
    /// its <see cref="SpawnEffect.Src"/>, which is what makes a snapshot holding that child
    /// persistable; inline logic has no key and yields <c>Src = null</c>.
    /// </summary>
    public IReadOnlyDictionary<string, IActorLogic> Sources { get; init; } =
        new Dictionary<string, IActorLogic>(StringComparer.Ordinal);

    /// <summary>
    /// Named action implementations (xstate v6 <c>setup({ actions })</c>). A
    /// <see cref="NamedAction{TContext}"/> reference in the definition is replaced by the
    /// registered template at build time, carrying the reference's own <c>params</c>.
    /// </summary>
    public IReadOnlyDictionary<string, ActionDefinition<TContext>> Actions { get; init; } =
        new Dictionary<string, ActionDefinition<TContext>>(StringComparer.Ordinal);

    /// <summary>
    /// Named guard implementations (xstate v6 <c>setup({ guards })</c>). Resolved at build time;
    /// the reference's <c>params</c> reach the predicate as
    /// <see cref="GuardArgs{TContext}.Params"/>.
    /// </summary>
    public IReadOnlyDictionary<string, Guard<TContext>> Guards { get; init; } =
        new Dictionary<string, Guard<TContext>>(StringComparer.Ordinal);

    /// <summary>
    /// Event types this machine only ever raises to itself (xstate v6 <c>internalEvents</c>).
    /// An event of such a type arriving from outside — through
    /// <see cref="Transition(State{TContext}, MachineEvent)"/> — is rejected at the boundary with
    /// a <see cref="DeadLetterEffect"/> (<c>"internalEvent"</c>); the same type raised from inside
    /// the machine is delivered normally.
    /// </summary>
    public IReadOnlySet<string> InternalEvents { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Hard cap on the microsteps one macrostep may take before the machine is declared to be
    /// looping: exceeding it throws <see cref="InfiniteTransitionError"/>. Named
    /// <c>maxIterations</c> upstream (xstate v6 <c>options.maxIterations</c>, default 1000).
    /// </summary>
    public int MaxMicrosteps { get; init; } = 1000;

    /// <summary>
    /// Optional boundary check on incoming events. A non-null result rejects the event: the
    /// snapshot is returned unchanged with a single <see cref="DeadLetterEffect"/>
    /// (<c>"invalidEvent"</c>) rather than routing the failure to the error channel — xstate v6
    /// <c>StateMachine.transition</c>.
    /// </summary>
    public Func<MachineEvent, Exception?>? EventValidator { get; init; }

    /// <summary>
    /// When true, <c>invoke</c>d actors start at the <em>end</em> of the macrostep (for every
    /// state entered and not since exited) instead of on entry, and their input is resolved
    /// there — SCXML §3.13 semantics, where <c>&lt;invoke&gt;</c> can read variables assigned by
    /// the entering state's <c>&lt;onentry&gt;</c>. Default false: xstate v6 spawns invoked actors
    /// before the entry actions run, so an entry action can address the child by id.
    /// </summary>
    public bool DeferInvokes { get; init; }

    /// <summary>
    /// When true, an unhandled <c>xstate.error.*</c> event is discarded like any other unhandled
    /// event instead of failing the machine. SCXML has no fatal-error rule — an
    /// undelivered/failed invocation surfaces as <c>error.communication</c>, which a session that
    /// does not handle it simply ignores. Default false: xstate v6 fails the actor.
    /// </summary>
    public bool IgnoreUnhandledErrors { get; init; }

    private readonly Dictionary<string, StateNode<TContext>> _idMap = new();

    internal void RegisterNodes(StateNode<TContext> node)
    {
        // Valid keys escape their dots, so two implicit ids cannot coincide; an explicit id that
        // equals another id would make `#id` ambiguous (xstate v6 StateNode.ts).
        if (_idMap.TryGetValue(node.Id, out var existing))
        {
            throw new InvalidOperationException(
                $"Duplicate state id '{node.Id}': the states at '{KeyPath(existing)}' and '{KeyPath(node)}' both have it. " +
                "State ids must be unique; rename the explicit `id`.");
        }

        _idMap[node.Id] = node;
        foreach (var child in node.Children) RegisterNodes(child);
    }

    private static string KeyPath(StateNode<TContext> node) =>
        node.Parent is null ? "" : node.Parent.Parent is null ? node.Key : $"{KeyPath(node.Parent)}.{node.Key}";

    /// <summary>
    /// The node whose id is exactly <paramref name="id"/> (an optional leading <c>#</c> is
    /// stripped). The text is never split into an id plus a path (xstate v6
    /// <c>getStateNodeById</c>).
    /// </summary>
    public StateNode<TContext> GetNodeById(string id)
    {
        var hasPrefix = id.StartsWith('#');
        var key = hasPrefix ? id[1..] : id;

        if (key.Length == 0 && hasPrefix)
        {
            throw new ArgumentException(Id.Length > 0
                ? $"State id '#' is empty. Target the root of {StatePath.DescribeMachine(Id)} with '#{Id}'."
                : "State id '#' is empty: the root of a machine without an `id` cannot be referenced by id. " +
                  "Give the machine an `id` to reference its root as '#<id>'.");
        }

        return _idMap.TryGetValue(key, out var node)
            ? node
            : throw new ArgumentException(DescribeUnknownStateId(key));
    }

    /// <summary>
    /// xstate v6 <c>_describeUnknownStateId</c>: when a prefix of the id is a state id and the
    /// rest resolves beneath it as a relative path, suggests the target's own id.
    /// </summary>
    private string DescribeUnknownStateId(string id)
    {
        var message = $"No state has id \"{id}\".";
        var segments = StatePath.Split(id);
        for (var i = segments.Count - 1; i > 0; i--)
        {
            _idMap.TryGetValue(string.Join('.', segments.Take(i)), out var node);
            for (var j = i; node is not null && j < segments.Count; j++)
            {
                node = node.Children.FirstOrDefault(c => c.Key == segments[j]);
            }

            if (node is not null)
            {
                return node.HasExplicitId
                    ? $"{message} Use \"#{node.Id}\"."
                    : $"{message} Use \"#{node.Id}\", or give that state an id.";
            }
        }

        return message;
    }

    public bool TryGetNodeById(string id, out StateNode<TContext> node) =>
        _idMap.TryGetValue(id, out node!);

    /// <summary>
    /// Canonical node id for a reference: <c>"#id"</c> names a whole state id exactly; anything
    /// else is a full id or a key path relative to the machine root (<c>"a.b"</c>, with
    /// <c>\.</c> escaping a dot inside a key). Null when nothing matches.
    /// </summary>
    internal string? ResolveStateId(string reference)
    {
        if (reference.StartsWith('#'))
        {
            return _idMap.TryGetValue(reference[1..], out var byId) ? byId.Id : null;
        }

        StateNode<TContext>? node = Root;
        foreach (var key in StatePath.Split(reference))
        {
            node = node?.Children.FirstOrDefault(c => c.Key == key);
        }

        if (node is not null)
        {
            return node.Id;
        }

        return _idMap.TryGetValue(reference, out var full) ? full.Id : null;
    }

    internal IEnumerable<StateNode<TContext>> NodesOf(IEnumerable<string> ids) =>
        ids.Select(id => _idMap[id]);

    /// <summary>
    /// The nested state value of a configuration — a port of xstate v6 <c>getStateValue</c>
    /// (<c>stateUtils.ts</c>): a compound state whose active child is atomic collapses to that
    /// child's key, everything else becomes a dictionary of child key → value.
    /// </summary>
    internal object StateValueOf(IEnumerable<string> configuration)
    {
        var adjacency = new Dictionary<StateNode<TContext>, List<StateNode<TContext>>>();

        foreach (var node in NodesOf(configuration).OrderBy(n => n.Order))
        {
            if (!adjacency.ContainsKey(node))
            {
                adjacency[node] = [];
            }

            if (node.Parent is not { } parent)
            {
                continue;
            }

            if (!adjacency.TryGetValue(parent, out var siblings))
            {
                siblings = [];
                adjacency[parent] = siblings;
            }

            siblings.Add(node);
        }

        return ValueOf(Root);

        object ValueOf(StateNode<TContext> node)
        {
            if (!adjacency.TryGetValue(node, out var children))
            {
                return new Dictionary<string, object>(StringComparer.Ordinal);
            }

            if (node.Type is StateNodeType.Compound &&
                children.FirstOrDefault() is { } only &&
                (only.Type is StateNodeType.Atomic or StateNodeType.Final))
            {
                return only.Key;
            }

            var value = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var child in children)
            {
                value[child.Key] = ValueOf(child);
            }

            return value;
        }
    }

    // --- Pure interpretation core (implemented in StateMachine.Algorithm.cs) ---

    /// <summary>Compute the initial state: enter from the root, run entry actions, settle always-transitions.</summary>
    public partial TransitionResult<TContext> GetInitialState(object? input = null);

    /// <summary>
    /// Pure macrostep: process the event, take microsteps (incl. eventless transitions and
    /// raised events) until stable, and return the settled state + ordered effects.
    /// </summary>
    public partial TransitionResult<TContext> Transition(State<TContext> state, MachineEvent evt);

    /// <summary>
    /// Stops the machine: stops every live child, cancels every outstanding timer, and returns a
    /// stopped snapshot. Exit actions do <em>not</em> run (xstate v6 <c>macrostep</c>'s
    /// <c>xstate.stop</c> branch: <c>stopChildren</c> + <c>cancelTimers</c> only).
    /// </summary>
    public TransitionResult<TContext> Stop(State<TContext> state) => Transition(state, new StopEvent());

    (ISnapshot, IReadOnlyList<Effect>) IActorLogic.GetInitialSnapshot(object? input)
    {
        var (state, effects) = GetInitialState(input);
        return (state, effects);
    }

    (ISnapshot, IReadOnlyList<Effect>) IActorLogic.Transition(ISnapshot state, MachineEvent evt)
    {
        if (state is not State<TContext> typed)
        {
            throw new ArgumentException(
                $"Snapshot of type {state.GetType().Name} does not belong to {StatePath.DescribeMachine(Id)} " +
                $"(expected State<{typeof(TContext).Name}>).");
        }

        var (next, effects) = Transition(typed, evt);
        return (next, effects);
    }
}

/// <summary>Entry point for the fluent builder.</summary>
public static class Machine
{
    public static Builder.MachineBuilder<TContext> Create<TContext>(string id) => new(id);

    /// <summary>Context-less machine (context is a unit type).</summary>
    public static Builder.MachineBuilder<Unit> Create(string id) =>
        new Builder.MachineBuilder<Unit>(id).Context(_ => Unit.Value);
}

/// <summary>Unit type for context-less machines.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value = default;
}
