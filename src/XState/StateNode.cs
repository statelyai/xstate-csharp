namespace XState;

public enum StateNodeType
{
    Atomic,
    Compound,
    Parallel,
    Final,
    History,

    /// <summary>
    /// A transient pseudo-state: entering it runs a function that picks the real target
    /// (xstate v6 <c>choice</c> states). A choice state is never part of a settled configuration.
    /// </summary>
    Choice
}

public enum HistoryType
{
    Shallow,
    Deep
}

/// <summary>
/// Explicit override of a transition's <em>domain</em> — the state whose descendants the
/// transition exits and re-enters (xstate v6 <c>_transitionDomain</c>).
/// </summary>
public enum TransitionDomain
{
    /// <summary>The source state itself is the domain: the source is not exited.</summary>
    Internal,

    /// <summary>The source's parent is the domain: the source is exited and re-entered.</summary>
    External
}

/// <summary>Matches events against a transition: by CLR type, or by string pattern with SCXML wildcard rules.</summary>
public abstract record EventDescriptor
{
    public abstract bool Matches(MachineEvent evt);

    /// <summary>Specificity for transition selection ordering (exact &gt; prefix &gt; wildcard).</summary>
    public abstract int Specificity { get; }

    public sealed record ByClrType(Type EventType) : EventDescriptor
    {
        public override bool Matches(MachineEvent evt) => EventType.IsInstanceOfType(evt);
        public override int Specificity => int.MaxValue;
    }

    /// <summary>
    /// "TOGGLE", "error.*", "*" — string matching on Event.Type. Default is xstate rules
    /// (exact, or explicit trailing ".*" wildcard). <see cref="TokenPrefix"/> enables SCXML
    /// token-prefix matching, where a bare "error" also matches "error.execution"
    /// (used by the SCXML importer).
    /// </summary>
    public sealed record ByPattern(string Pattern) : EventDescriptor
    {
        /// <summary>SCXML §3.12.1 prefix matching: bare patterns match dot-separated descendants.</summary>
        public bool TokenPrefix { get; init; }

        public override bool Matches(MachineEvent evt)
        {
            var type = evt.Type;
            if (Pattern == "*") return true;
            if (Pattern == type) return true;
            if (Pattern.EndsWith(".*", StringComparison.Ordinal))
                return type.StartsWith(Pattern[..^1], StringComparison.Ordinal);
            return TokenPrefix && type.StartsWith(Pattern + ".", StringComparison.Ordinal);
        }

        public override int Specificity => Pattern == "*" ? 0 : Pattern.Count(c => c == '.') + 1;
    }

    /// <summary>
    /// Matches by predicate. Used for synthesized descriptors that must also match a payload
    /// field (e.g. <c>xstate.after</c> for one state+delay, <c>xstate.done.actor</c> for one invoke id).
    /// </summary>
    internal sealed record ByPredicate(Func<MachineEvent, bool> Predicate) : EventDescriptor
    {
        public override bool Matches(MachineEvent evt) => Predicate(evt);
        public override int Specificity => int.MaxValue;
    }
}

/// <summary>A transition between states (or a targetless/internal self-transition).</summary>
public sealed class TransitionDefinition<TContext>
{
    public required StateNode<TContext> Source { get; init; }

    /// <summary>Null for eventless ("always") and initial transitions.</summary>
    public EventDescriptor? Event { get; init; }

    public Guard<TContext>? Guard { get; internal set; }

    /// <summary>
    /// The guard's portable identity when it was referenced by name (<c>{ type, params }</c>);
    /// null for an inline predicate. <see cref="Guard"/> is the resolved predicate either way.
    /// </summary>
    public GuardDefinition<TContext>? GuardDefinition { get; internal set; }

    /// <summary>Resolved target nodes. Empty → targetless transition (actions only).</summary>
    public IReadOnlyList<StateNode<TContext>> Targets { get; internal set; } = [];

    /// <summary>Unresolved target ids/keys (resolved to <see cref="Targets"/> at build time).</summary>
    public IReadOnlyList<string> TargetIds { get; init; } = [];

    /// <summary>
    /// xstate v6 `reenter`. Default false: transitions targeting the source itself or its
    /// descendants do not exit/re-enter the source. True restores SCXML external-transition
    /// behavior (the SCXML importer sets this per document semantics).
    /// </summary>
    public bool Reenter { get; init; }

    /// <summary>
    /// Explicit domain override (xstate v6 <c>_transitionDomain</c>). Null → the domain is the
    /// LCCA of the source and the effective targets, as SCXML computes it.
    /// </summary>
    public TransitionDomain? Domain { get; init; }

    public IReadOnlyList<ActionDefinition<TContext>> Actions { get; internal set; } = [];

    /// <summary>
    /// The state input this transition carries (xstate v6 <c>input</c> on a transition): resolved
    /// once when the transition is taken and recorded under every target's id in
    /// <see cref="State{TContext}.StateInputs"/>.
    /// </summary>
    public Func<ActionArgs<TContext>, object?>? InputResolver { get; init; }

    /// <summary>Arbitrary metadata (xstate v6 <c>meta</c>).</summary>
    public object? Meta { get; init; }

    /// <summary>Human-readable description (xstate v6 <c>description</c>).</summary>
    public string? Description { get; init; }

    /// <summary>Document order index for deterministic selection.</summary>
    public int Order { get; init; }
}

/// <summary>An invoked actor definition on a state node.</summary>
public sealed class InvokeDefinition<TContext>
{
    public required string Id { get; init; }
    public required IActorLogic Logic { get; init; }

    /// <summary>
    /// The registered actor-source key <see cref="Logic"/> was resolved from, or null when the
    /// logic was supplied by value and is not registered under any name. It rides on the
    /// <see cref="SpawnEffect"/> and the <see cref="ChildRecord"/>: a child without a source key
    /// is runnable but not persistable.
    /// </summary>
    public string? Src { get; init; }

    public Func<ActionArgs<TContext>, object?>? InputResolver { get; init; }

    /// <summary>
    /// SCXML §3.13 <c>autoforward</c>: while the child is running, every external event the parent
    /// processes is also forwarded to the child as a <see cref="SendToEffect"/>, before transition
    /// selection. Internal <c>xstate.*</c> events are never forwarded.
    /// </summary>
    public bool AutoForward { get; init; }

    /// <summary>
    /// Pre-selection hook run for every external event while the child is running, in document
    /// order of the invoking states, before transition selection (SCXML §3.13 <c>&lt;finalize&gt;</c>:
    /// the hook decides from the event whether it originated from this child). It may update the
    /// context, raise internal events and emit effects; a null result means "nothing to do".
    /// </summary>
    public Func<ActionArgs<TContext>, ScriptResult<TContext>?>? OnExternalEvent { get; init; }

    /// <summary>Transitions taken on xstate.done.actor for this invoke.</summary>
    public IReadOnlyList<TransitionDefinition<TContext>> OnDone { get; internal set; } = [];

    /// <summary>Transitions taken on xstate.error.actor for this invoke.</summary>
    public IReadOnlyList<TransitionDefinition<TContext>> OnError { get; internal set; } = [];

    /// <summary>
    /// How long this actor may run before <c>xstate.timeout.actor</c> is raised (xstate v6
    /// invoke <c>timeout</c>). Scheduled when the actor is spawned, cancelled when it is stopped
    /// or reports done/error.
    /// </summary>
    public DelayRef<TContext>? Timeout { get; init; }

    /// <summary>Transitions taken on xstate.timeout.actor for this invoke.</summary>
    public IReadOnlyList<TransitionDefinition<TContext>> OnTimeout { get; internal set; } = [];

    /// <summary>
    /// Transitions taken on <c>xstate.snapshot.actor</c> for this invoke (xstate v6 <c>onSnapshot</c>).
    /// A non-empty list is what sets <see cref="SpawnEffect.SyncSnapshot"/>.
    /// </summary>
    public IReadOnlyList<TransitionDefinition<TContext>> OnSnapshot { get; internal set; } = [];

    /// <summary>Timer id of this invoke's timeout (<c>xstate.timeout.actor.{invokeId}</c>).</summary>
    public string TimeoutSendId => $"xstate.timeout.actor.{Id}";
}

/// <summary>
/// What a <see cref="StateNodeType.Choice"/> state resolved to: the states to transition to,
/// whether to re-enter them, and the state input to carry.
/// </summary>
public sealed record ChoiceResult(
    IReadOnlyList<string> Targets,
    bool Reenter = false,
    object? Input = null)
{
    public ChoiceResult(string target, bool reenter = false, object? input = null)
        : this([target], reenter, input)
    {
    }
}

/// <summary>An immutable node in the state machine tree.</summary>
public sealed class StateNode<TContext>
{
    public required string Key { get; init; }

    /// <summary>Unique id: explicit, or "{machineId}.{path...}".</summary>
    public required string Id { get; init; }

    public StateNode<TContext>? Parent { get; internal set; }
    public required StateNodeType Type { get; init; }

    /// <summary>Document order (stable sort key for entry/exit ordering).</summary>
    public required int Order { get; init; }

    /// <summary>Child nodes in document order (empty for atomic/final/history).</summary>
    public IReadOnlyList<StateNode<TContext>> Children { get; internal set; } = [];

    /// <summary>Initial transition for compound states.</summary>
    public TransitionDefinition<TContext>? InitialTransition { get; internal set; }

    /// <summary>Event-driven transitions in document order.</summary>
    public IReadOnlyList<TransitionDefinition<TContext>> Transitions { get; internal set; } = [];

    /// <summary>Eventless ("always") transitions in document order.</summary>
    public IReadOnlyList<TransitionDefinition<TContext>> Always { get; internal set; } = [];

    /// <summary>Delayed transitions: delay → transitions (xstate `after`).</summary>
    public IReadOnlyList<(DelayRef<TContext> Delay, TransitionDefinition<TContext> Transition)> After { get; internal set; } = [];

    /// <summary>
    /// Timer key per <see cref="After"/> entry, unique within this node (duplicate delays are
    /// disambiguated). Used for the <c>xstate.after</c> event/send id.
    /// </summary>
    internal IReadOnlyList<string> AfterKeys { get; set; } = [];

    /// <summary>
    /// How long this state may stay active before <c>xstate.timeout</c> is raised (xstate v6
    /// state <c>timeout</c>). Scheduled on entry, cancelled on exit.
    /// </summary>
    public DelayRef<TContext>? Timeout { get; internal set; }

    /// <summary>Transitions taken on this state's own <c>xstate.timeout</c>.</summary>
    public IReadOnlyList<TransitionDefinition<TContext>> OnTimeout { get; internal set; } = [];

    /// <summary>Timer id of this state's timeout (<c>xstate.timeout.{stateId}</c>).</summary>
    public string TimeoutSendId => $"xstate.timeout.{Id}";

    public IReadOnlyList<ActionDefinition<TContext>> Entry { get; internal set; } = [];
    public IReadOnlyList<ActionDefinition<TContext>> Exit { get; internal set; } = [];

    public IReadOnlyList<InvokeDefinition<TContext>> Invokes { get; internal set; } = [];

    /// <summary>For history nodes.</summary>
    public HistoryType HistoryType { get; init; }

    /// <summary>Default target for history nodes when no history recorded.</summary>
    public TransitionDefinition<TContext>? HistoryDefault { get; internal set; }

    /// <summary>
    /// The function a <see cref="StateNodeType.Choice"/> state runs on entry to pick its target.
    /// </summary>
    public Func<ActionArgs<TContext>, ChoiceResult>? Choice { get; internal set; }

    /// <summary>Output resolver for final states (donedata).</summary>
    public Func<ActionArgs<TContext>, object?>? Output { get; init; }

    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();

    /// <summary>Arbitrary metadata (xstate v6 <c>meta</c>); surfaced by <see cref="State{TContext}.Meta"/>.</summary>
    public object? Meta { get; init; }

    /// <summary>Human-readable description (xstate v6 <c>description</c>).</summary>
    public string? Description { get; init; }

    public string Path => Parent is null ? Key : $"{Parent.Path}.{Key}";

    public override string ToString() => $"#{Id}";
}
