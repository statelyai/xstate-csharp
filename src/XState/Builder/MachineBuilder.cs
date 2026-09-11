namespace XState.Builder;

/// <summary>
/// Fluent machine definition:
/// <code>
/// var machine = Machine.Create&lt;ToggleContext&gt;("toggle")
///     .Context(_ =&gt; new ToggleContext(Count: 0))
///     .Initial("inactive")
///     .State("inactive", s =&gt; s
///         .On&lt;Toggle&gt;(t =&gt; t.Target("active")))
///     .State("active", s =&gt; s
///         .On&lt;Toggle&gt;(t =&gt; t
///             .Assign(a =&gt; a.Context with { Count = a.Context.Count + 1 })
///             .Target("inactive")))
///     .Build();
/// </code>
/// </summary>
public sealed class MachineBuilder<TContext>
{
    private readonly StateBuilder<TContext> _root;
    private readonly Dictionary<string, IActorLogic> _sources = new(StringComparer.Ordinal);
    private Func<object?, TContext>? _contextFactory;
    private bool _deferInvokes;
    private bool _ignoreUnhandledErrors;
    private string? _version;
    private int _maxMicrosteps = 1000;
    private Func<MachineEvent, Exception?>? _eventValidator;
    private readonly Dictionary<string, ActionDefinition<TContext>> _actions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guard<TContext>> _guards = new(StringComparer.Ordinal);
    private readonly HashSet<string> _internalEvents = new(StringComparer.Ordinal);

    public string Id { get; }

    public MachineBuilder(string id)
    {
        Id = id;
        _root = new StateBuilder<TContext>(id, isRoot: true);
    }

    /// <summary>
    /// The machine's version (xstate v6 <c>machineVersion</c>), recorded on the definition so a
    /// durable host can refuse a snapshot persisted by an incompatible one.
    /// </summary>
    public MachineBuilder<TContext> Version(string version)
    {
        _version = version;
        return this;
    }

    /// <summary>
    /// Registers actor logic under a source key. An <c>Invoke("key", …)</c> or spawn that names
    /// the key records it on the resulting <see cref="SpawnEffect"/> and
    /// <see cref="ChildRecord"/> — which is what makes a snapshot holding that child persistable.
    /// </summary>
    public MachineBuilder<TContext> Actor(string source, IActorLogic logic)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentNullException.ThrowIfNull(logic);

        if (!_sources.TryAdd(source, logic))
        {
            throw new InvalidOperationException(
                $"Machine '{Id}' already registers an actor source named '{source}'.");
        }

        return this;
    }

    /// <summary>
    /// Registers an action implementation under a name, so states and transitions can refer to it
    /// with <c>.Do("name", params)</c> / <c>.Entry("name", params)</c> (xstate v6
    /// <c>setup({ actions })</c> plus <c>{ type, params }</c> references).
    /// </summary>
    public MachineBuilder<TContext> Action(string name, ActionDefinition<TContext> action)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(action);
        _actions[name] = action with { Name = name };
        return this;
    }

    /// <inheritdoc cref="Action(string, ActionDefinition{TContext})"/>
    public MachineBuilder<TContext> Action(string name, Action<ActionArgs<TContext>> action)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(action);
        _actions[name] = new CustomAction<TContext>(action) { Name = name };
        return this;
    }

    /// <summary>
    /// Registers a guard implementation under a name, so transitions can refer to it with
    /// <c>.Guard("name", params)</c>. The reference's params reach the predicate as
    /// <see cref="GuardArgs{TContext}.Params"/>.
    /// </summary>
    public MachineBuilder<TContext> Guard(string name, Guard<TContext> guard)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(guard);
        _guards[name] = guard;
        return this;
    }

    /// <summary>
    /// Declares event types the machine only ever raises to itself. One arriving from outside is
    /// rejected at the boundary with a <see cref="DeadLetterEffect"/> (<c>"internalEvent"</c>).
    /// </summary>
    public MachineBuilder<TContext> InternalEvents(params string[] eventTypes)
    {
        foreach (var type in eventTypes)
        {
            ArgumentException.ThrowIfNullOrEmpty(type);
            _internalEvents.Add(type);
        }

        return this;
    }

    /// <summary>
    /// Caps the microsteps one macrostep may take before the machine is declared to be looping
    /// (xstate v6's hard cap is 1000).
    /// </summary>
    public MachineBuilder<TContext> MaxMicrosteps(int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        _maxMicrosteps = max;
        return this;
    }

    /// <summary>
    /// Checks every incoming event at the boundary. A non-null result rejects the event: the
    /// snapshot is returned unchanged with a <see cref="DeadLetterEffect"/>.
    /// </summary>
    public MachineBuilder<TContext> ValidateEvents(Func<MachineEvent, Exception?> validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        _eventValidator = validate;
        return this;
    }

    public MachineBuilder<TContext> Context(Func<object?, TContext> factory)
    {
        _contextFactory = factory;
        return this;
    }

    public MachineBuilder<TContext> Context(Func<TContext> factory) => Context(_ => factory());

    public MachineBuilder<TContext> Initial(params string[] stateKeys)
    {
        _root.Initial(stateKeys);
        return this;
    }

    /// <inheritdoc cref="StateBuilder{TContext}.Initial(Action{TransitionBuilder{TContext, MachineEvent}})"/>
    public MachineBuilder<TContext> Initial(Action<TransitionBuilder<TContext, MachineEvent>> configure)
    {
        _root.Initial(configure);
        return this;
    }

    public MachineBuilder<TContext> State(string key, Action<StateBuilder<TContext>>? configure = null)
    {
        _root.State(key, configure);
        return this;
    }

    public MachineBuilder<TContext> Final(string key, Action<StateBuilder<TContext>>? configure = null)
    {
        _root.Final(key, configure);
        return this;
    }

    public MachineBuilder<TContext> Parallel(string key, Action<StateBuilder<TContext>> configure)
    {
        _root.Parallel(key, configure);
        return this;
    }

    /// <summary>
    /// Start invoked actors at the end of the macrostep rather than on entry
    /// (see <see cref="StateMachine{TContext}.DeferInvokes"/>).
    /// </summary>
    public MachineBuilder<TContext> DeferInvokes(bool defer = true)
    {
        _deferInvokes = defer;
        return this;
    }

    /// <summary>
    /// Discard unhandled <c>xstate.error.actor</c> events instead of failing the machine
    /// (see <see cref="StateMachine{TContext}.IgnoreUnhandledErrors"/>).
    /// </summary>
    public MachineBuilder<TContext> IgnoreUnhandledErrors(bool ignore = true)
    {
        _ignoreUnhandledErrors = ignore;
        return this;
    }

    /// <summary>Machine-level (root) transitions, entry, invoke, etc.</summary>
    public MachineBuilder<TContext> Root(Action<StateBuilder<TContext>> configure)
    {
        configure(_root);
        return this;
    }

    public StateMachine<TContext> Build()
    {
        if (_contextFactory is null && typeof(TContext) != typeof(Unit))
            throw new InvalidOperationException(
                $"Machine '{Id}' has context type {typeof(TContext).Name} but no .Context(...) factory.");

        return MachineResolver.Resolve(
            Id,
            _root,
            _contextFactory ?? (_ => default!),
            _deferInvokes,
            _ignoreUnhandledErrors,
            _version,
            _sources,
            _maxMicrosteps,
            _eventValidator,
            _actions,
            _guards,
            _internalEvents);
    }
}
