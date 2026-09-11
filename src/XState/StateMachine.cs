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
    /// looping (xstate v6 <c>macrostep</c> throws above 1000).
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
        RegisterIds(node);
        RegisterAliases(node);
    }

    private void RegisterIds(StateNode<TContext> node)
    {
        _idMap[node.Id] = node;
        foreach (var child in node.Children) RegisterIds(child);
    }

    /// <summary>
    /// A node with an explicit <c>.Id(...)</c> is still addressable by its key path
    /// ("machineId.key1.key2"): the explicit id is an additional alias, not a re-rooting.
    /// Registered after every canonical id, so an explicit id always wins a collision.
    /// </summary>
    private void RegisterAliases(StateNode<TContext> node)
    {
        if (node.Path != node.Id)
        {
            _idMap.TryAdd(node.Path, node);
        }

        foreach (var child in node.Children) RegisterAliases(child);
    }

    /// <summary>Registers an additional name for a node; never overwrites a canonical id.</summary>
    internal void RegisterAlias(string alias, StateNode<TContext> node) => _idMap.TryAdd(alias, node);

    public StateNode<TContext> GetNodeById(string id) =>
        _idMap.TryGetValue(id, out var node)
            ? node
            : throw new ArgumentException($"State node '#{id}' not found on machine '{Id}'.");

    public bool TryGetNodeById(string id, out StateNode<TContext> node) =>
        _idMap.TryGetValue(id, out node!);

    /// <summary>
    /// Canonical node id for a reference written as a full id, an explicit "#id", or a key path
    /// relative to the machine root ("a.b"). Null when nothing matches.
    /// </summary>
    internal string? ResolveStateId(string reference)
    {
        var key = reference.StartsWith('#') ? reference[1..] : reference;

        if (_idMap.TryGetValue(key, out var node))
        {
            return node.Id;
        }

        return _idMap.TryGetValue($"{Id}.{key}", out var relative) ? relative.Id : null;
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
    /// <c>@xstate.stop</c> branch: <c>stopChildren</c> + <c>cancelTimers</c> only).
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
                $"Snapshot of type {state.GetType().Name} does not belong to machine '{Id}' " +
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
