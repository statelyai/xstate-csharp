namespace XState;

/// <summary>
/// Arguments available to actions: context, the triggering event, the SCXML <c>In()</c>
/// predicate over the configuration as it stood when the action ran, plus the action's own
/// <see cref="Params"/> and the state input in scope.
/// </summary>
public readonly record struct ActionArgs<TContext>(
    TContext Context,
    MachineEvent Event,
    Func<string, bool>? InPredicate = null)
{
    private readonly IReadOnlyDictionary<string, object?>? _inputs;

    /// <summary>
    /// Whether the given state id/path is in the configuration. Always false when the args
    /// were built outside a transition run (no configuration to consult).
    /// </summary>
    public Func<string, bool> In => InPredicate ?? NeverIn;

    /// <summary>
    /// The parameters declared alongside this action (xstate v6 <c>{ type, params }</c>). Null
    /// for an inline action, or for a named action referenced without params.
    /// </summary>
    public object? Params { get; init; }

    /// <summary>
    /// The state input in scope for this action — the input carried by the transition that
    /// entered the state the action belongs to (xstate v6 <c>_stateInputs</c>). Null for actions
    /// that belong to no state (transition content) or for a state entered without input.
    /// </summary>
    public object? Input { get; init; }

    /// <summary>Every state input currently recorded on the snapshot, keyed by state id.</summary>
    public IReadOnlyDictionary<string, object?> Inputs
    {
        get => _inputs ?? EmptyInputs;
        init => _inputs = value;
    }

    private static readonly Func<string, bool> NeverIn = _ => false;

    private static readonly IReadOnlyDictionary<string, object?> EmptyInputs =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>
/// Declarative action object resolved by the transition algorithm. Assign/raise are
/// interpreted during the microstep; the rest surface as <see cref="Effect"/>s for the
/// runtime to execute in order.
/// </summary>
public abstract record ActionDefinition<TContext>
{
    /// <summary>Optional name (used by JSON/SCXML import and inspection).</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Parameters this action reference declared (xstate v6 <c>{ type, params }</c>). Surfaced to
    /// the implementation as <see cref="ActionArgs{TContext}.Params"/>, and to a durable host as
    /// <see cref="ActionEffect.Params"/>.
    /// </summary>
    public object? Params { get; init; }
}

/// <summary>Pure context update, applied eagerly during transition resolution.</summary>
public sealed record AssignAction<TContext>(Func<ActionArgs<TContext>, TContext> Assigner)
    : ActionDefinition<TContext>;

/// <summary>Raise an event to self (internal queue; with delay → scheduled send-to-self).</summary>
public sealed record RaiseAction<TContext>(
    Func<ActionArgs<TContext>, MachineEvent> EventFactory,
    DelayRef<TContext>? Delay = null,
    string? Id = null) : ActionDefinition<TContext>;

/// <summary>Send an event to another actor, addressed by id (resolution is the executor's concern).</summary>
public sealed record SendToAction<TContext>(
    Func<ActionArgs<TContext>, string> TargetResolver,
    Func<ActionArgs<TContext>, MachineEvent> EventFactory,
    DelayRef<TContext>? Delay = null,
    string? Id = null) : ActionDefinition<TContext>;

/// <summary>Cancel a delayed send by id (SCXML &lt;cancel&gt;).</summary>
public sealed record CancelAction<TContext>(Func<ActionArgs<TContext>, string> SendIdResolver)
    : ActionDefinition<TContext>;

/// <summary>Log a message (surfaces as the built-in <c>xstate.log</c> action effect).</summary>
public sealed record LogAction<TContext>(
    Func<ActionArgs<TContext>, object?> MessageResolver,
    string? Label = null) : ActionDefinition<TContext>;

/// <summary>Arbitrary side effect, deferred until the runtime executes effects.</summary>
public sealed record CustomAction<TContext>(Action<ActionArgs<TContext>> Execute)
    : ActionDefinition<TContext>;

/// <summary>Stop a spawned/invoked child actor.</summary>
public sealed record StopChildAction<TContext>(Func<ActionArgs<TContext>, string> ChildIdResolver)
    : ActionDefinition<TContext>;

/// <summary>
/// Thrown by an invoke's input resolver to refuse the invocation: the child is not spawned and no
/// actor timeout is scheduled, while anything the resolver already raised (an SCXML
/// <c>error.execution</c>, say) is still delivered. The pure counterpart of a runtime cancelling an
/// invocation whose <c>&lt;param&gt;</c>/<c>namelist</c> evaluation failed (SCXML §6.4).
/// </summary>
public sealed class SpawnAbortedException : Exception
{
    public SpawnAbortedException() : base("The invocation was aborted by its input resolver.") { }

    public SpawnAbortedException(string message) : base(message) { }

    public SpawnAbortedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Spawn a child actor (assign the returned ref via a following assign, or by id).</summary>
/// <param name="Src">
/// The registered actor-source key <paramref name="Logic"/> was resolved from, or null for
/// inline logic; see <see cref="InvokeDefinition{TContext}.Src"/>.
/// </param>
public sealed record SpawnAction<TContext>(
    IActorLogic Logic,
    string? Id = null,
    Func<ActionArgs<TContext>, object?>? InputResolver = null,
    string? Src = null) : ActionDefinition<TContext>;

/// <summary>Emit an event to the actor's subscribers (surfaces as an <see cref="EmitEffect"/>).</summary>
public sealed record EmitAction<TContext>(Func<ActionArgs<TContext>, MachineEvent> EventFactory)
    : ActionDefinition<TContext>;

/// <summary>
/// A reference to an action registered on the machine under
/// <see cref="StateMachine{TContext}.Actions"/> (xstate v6 <c>{ type, params }</c>). Replaced by
/// the registered implementation when the machine is built; a name with no implementation is a
/// build-time error listing every missing name at once.
/// </summary>
public sealed record NamedAction<TContext>(string Type) : ActionDefinition<TContext>;

/// <summary>
/// A combined action: atomically update context, raise internal events, and emit effects
/// in one step. The compilation target for imported executable content (SCXML
/// &lt;if&gt;/&lt;foreach&gt; blocks with nested &lt;assign&gt;/&lt;raise&gt;/&lt;send&gt;) whose
/// parts must apply mid-transition, in order.
/// </summary>
public sealed record ScriptAction<TContext>(Func<ActionArgs<TContext>, ScriptResult<TContext>> Run)
    : ActionDefinition<TContext>;

/// <summary>Result of a <see cref="ScriptAction{TContext}"/>.</summary>
public readonly record struct ScriptResult<TContext>(
    TContext Context,
    IReadOnlyList<MachineEvent>? Raised = null,
    IReadOnlyList<Effect>? Effects = null);

/// <summary>A delay: fixed, or resolved from context/event (named delays from JSON/SCXML resolve to this).</summary>
public sealed record DelayRef<TContext>(Func<ActionArgs<TContext>, TimeSpan> Resolve, string? Name = null)
{
    public static implicit operator DelayRef<TContext>(TimeSpan fixedDelay) =>
        new(_ => fixedDelay, fixedDelay.TotalMilliseconds.ToString());
}
