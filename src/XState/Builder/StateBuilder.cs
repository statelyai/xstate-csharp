namespace XState.Builder;

/// <summary>Builds one state node: children, transitions, actions, invocations.</summary>
public sealed class StateBuilder<TContext>
{
    internal string Key { get; }
    internal bool IsRoot { get; }
    internal StateNodeType Type { get; set; } = StateNodeType.Atomic;
    internal HistoryType HistoryType { get; set; }
    internal TransitionDraft<TContext>? InitialDraft { get; set; }

    /// <summary>The single initial target key, when the state declares one the simple way.</summary>
    internal string? InitialKey
    {
        get => InitialDraft is { TargetIds.Count: 1 } d ? d.TargetIds[0] : null;
        set
        {
            if (value is null)
            {
                InitialDraft = null;
                return;
            }

            InitialDraft = new TransitionDraft<TContext>();
            InitialDraft.TargetIds.Add(value);
        }
    }
    internal string? ExplicitId { get; set; }
    internal List<StateBuilder<TContext>> Children { get; } = [];
    internal List<TransitionDraft<TContext>> Transitions { get; } = [];
    internal List<TransitionDraft<TContext>> AlwaysTransitions { get; } = [];
    internal List<TransitionDraft<TContext>> OnDoneTransitions { get; } = [];
    internal List<(DelayRef<TContext> Delay, TransitionDraft<TContext> Transition)> AfterTransitions { get; } = [];
    internal List<ActionDefinition<TContext>> EntryActions { get; } = [];
    internal List<ActionDefinition<TContext>> ExitActions { get; } = [];
    internal List<InvokeDraft<TContext>> Invokes { get; } = [];
    internal Func<ActionArgs<TContext>, object?>? OutputResolver { get; set; }
    internal HashSet<string> StateTags { get; } = [];
    internal string? HistoryDefaultTarget { get; set; }
    internal TransitionDraft<TContext>? HistoryDefaultDraft { get; set; }
    internal object? StateMeta { get; set; }
    internal string? StateDescription { get; set; }
    internal DelayRef<TContext>? TimeoutDelay { get; set; }
    internal List<TransitionDraft<TContext>> OnTimeoutTransitions { get; } = [];
    internal List<TransitionDraft<TContext>> OnErrorTransitions { get; } = [];
    internal Func<ActionArgs<TContext>, ChoiceResult>? ChoiceResolver { get; set; }
    internal TransitionDraft<TContext>? RouteDraft { get; set; }

    internal StateBuilder(string key, bool isRoot = false)
    {
        Key = key;
        IsRoot = isRoot;
    }

    /// <summary>Explicit id (referenced as "#id" in targets).</summary>
    public StateBuilder<TContext> Id(string id)
    {
        ExplicitId = id;
        return this;
    }

    /// <summary>
    /// The state(s) entered by default. More than one target expresses a deep initial across the
    /// regions of a descendant parallel state; a target may be a child key path or an
    /// <c>"#id"</c> reference (xstate v6 <c>initial</c>).
    /// </summary>
    public StateBuilder<TContext> Initial(params string[] stateKeys)
    {
        if (stateKeys.Length == 0)
        {
            throw new ArgumentException("An initial transition needs at least one target.", nameof(stateKeys));
        }

        var draft = new TransitionDraft<TContext>();
        draft.TargetIds.AddRange(stateKeys);
        InitialDraft = draft;
        if (Type is StateNodeType.Atomic) Type = StateNodeType.Compound;
        return this;
    }

    /// <summary>
    /// The initial transition with content: targets, actions and state input. Its actions run
    /// only when the state is entered <em>by default</em> — a transition that targets a
    /// descendant directly does not run them (SCXML <c>defaultHistoryContent</c> semantics).
    /// </summary>
    public StateBuilder<TContext> Initial(Action<TransitionBuilder<TContext, MachineEvent>> configure)
    {
        var t = new TransitionBuilder<TContext, MachineEvent>(null);
        configure(t);
        InitialDraft = t.Draft;
        if (Type is StateNodeType.Atomic) Type = StateNodeType.Compound;
        return this;
    }

    public StateBuilder<TContext> State(string key, Action<StateBuilder<TContext>>? configure = null)
    {
        var child = new StateBuilder<TContext>(key);
        configure?.Invoke(child);
        Children.Add(child);
        if (Type is StateNodeType.Atomic) Type = StateNodeType.Compound;
        return this;
    }

    public StateBuilder<TContext> Final(string key, Action<StateBuilder<TContext>>? configure = null)
    {
        var child = new StateBuilder<TContext>(key) { Type = StateNodeType.Final };
        configure?.Invoke(child);
        Children.Add(child);
        if (Type is StateNodeType.Atomic) Type = StateNodeType.Compound;
        return this;
    }

    public StateBuilder<TContext> Parallel(string key, Action<StateBuilder<TContext>> configure)
    {
        var child = new StateBuilder<TContext>(key) { Type = StateNodeType.Parallel };
        configure(child);
        Children.Add(child);
        if (Type is StateNodeType.Atomic) Type = StateNodeType.Compound;
        return this;
    }

    /// <summary>This state itself is a parallel region container.</summary>
    public StateBuilder<TContext> AsParallel()
    {
        Type = StateNodeType.Parallel;
        return this;
    }

    public StateBuilder<TContext> History(
        string key,
        HistoryType type = XState.HistoryType.Shallow,
        string? defaultTarget = null,
        Action<TransitionBuilder<TContext, MachineEvent>>? configure = null)
    {
        var child = new StateBuilder<TContext>(key)
        {
            Type = StateNodeType.History,
            HistoryType = type,
            HistoryDefaultTarget = defaultTarget
        };

        if (configure is not null)
        {
            // The history state's *default* transition (xstate v6 `_historyDefaultTransition`):
            // its content runs only when the default is taken, i.e. when nothing was recorded.
            var t = new TransitionBuilder<TContext, MachineEvent>(null);
            configure(t);
            if (defaultTarget is not null && t.Draft.TargetIds.Count == 0)
            {
                t.Draft.TargetIds.Add(defaultTarget);
            }
            child.HistoryDefaultDraft = t.Draft;
            child.HistoryDefaultTarget = t.Draft.TargetIds.Count > 0 ? t.Draft.TargetIds[0] : defaultTarget;
        }

        Children.Add(child);
        if (Type is StateNodeType.Atomic) Type = StateNodeType.Compound;
        return this;
    }

    /// <summary>
    /// Makes this a <em>choice</em> state (xstate v6 <c>choice</c>): entering it runs
    /// <paramref name="resolve"/> and immediately transitions to the targets it returns. A choice
    /// state may not declare transitions, entry/exit actions, invokes or children.
    /// </summary>
    public StateBuilder<TContext> Choice(Func<ActionArgs<TContext>, ChoiceResult> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ChoiceResolver = resolve;
        Type = StateNodeType.Choice;
        return this;
    }

    /// <summary>
    /// Makes this state reachable by <see cref="RouteEvent"/> (xstate v6 <c>route</c>): an
    /// <c>xstate.route</c> event whose <c>to</c> is <c>"#{this state's id}"</c> transitions here
    /// from wherever the machine currently is. Requires an explicit <see cref="Id"/>.
    /// <para>
    /// The optional <paramref name="configure"/> adds the usual transition content — a guard that
    /// can refuse the route, <c>Input</c>, <c>Reenter</c>, actions. A refusing guard leaves the
    /// event unhandled and the snapshot unchanged.
    /// </para>
    /// </summary>
    public StateBuilder<TContext> Route(Action<TransitionBuilder<TContext, RouteEvent>>? configure = null)
    {
        var t = new TransitionBuilder<TContext, RouteEvent>(null);
        configure?.Invoke(t);
        RouteDraft = t.Draft;
        return this;
    }

    /// <summary>Arbitrary metadata, surfaced on the snapshot by <see cref="State{TContext}.Meta"/>.</summary>
    public StateBuilder<TContext> Meta(object? meta)
    {
        StateMeta = meta;
        return this;
    }

    /// <summary>Human-readable description of the state.</summary>
    public StateBuilder<TContext> Description(string description)
    {
        StateDescription = description;
        return this;
    }

    /// <summary>
    /// How long this state may stay active before <c>xstate.timeout</c> is raised for it. Requires
    /// a matching <see cref="OnTimeout"/>.
    /// </summary>
    public StateBuilder<TContext> Timeout(TimeSpan delay) => Timeout((DelayRef<TContext>)delay);

    /// <inheritdoc cref="Timeout(TimeSpan)"/>
    public StateBuilder<TContext> Timeout(DelayRef<TContext> delay)
    {
        TimeoutDelay = delay;
        return this;
    }

    /// <summary>Transition taken when this state's <see cref="Timeout(TimeSpan)"/> elapses.</summary>
    public StateBuilder<TContext> OnTimeout(Action<TransitionBuilder<TContext, TimeoutEvent>> configure)
    {
        var t = new TransitionBuilder<TContext, TimeoutEvent>(null);
        configure(t);
        OnTimeoutTransitions.Add(t.Draft);
        return this;
    }

    /// <summary>
    /// Transition taken on any <c>xstate.error.*</c> event reaching this state (xstate v6
    /// state-level <c>onError</c>): an invoked or spawned actor's <c>xstate.error.actor</c>, or a
    /// platform <c>xstate.error.{kind}</c>. An invoke's own <c>onError</c> matches the event
    /// exactly and is always selected first.
    /// </summary>
    public StateBuilder<TContext> OnError(Action<TransitionBuilder<TContext, MachineEvent>> configure)
    {
        var t = new TransitionBuilder<TContext, MachineEvent>(null);
        configure(t);
        OnErrorTransitions.Add(t.Draft);
        return this;
    }

    /// <summary>Output for final states (SCXML donedata).</summary>
    public StateBuilder<TContext> Output(Func<ActionArgs<TContext>, object?> resolver)
    {
        OutputResolver = resolver;
        return this;
    }

    // --- Transitions ---

    /// <summary>Typed transition: matches events of CLR type TEvent.</summary>
    public StateBuilder<TContext> On<TEvent>(Action<TransitionBuilder<TContext, TEvent>>? configure = null)
        where TEvent : MachineEvent
    {
        var t = new TransitionBuilder<TContext, TEvent>(new EventDescriptor.ByClrType(typeof(TEvent)));
        configure?.Invoke(t);
        Transitions.Add(t.Draft);
        return this;
    }

    /// <summary>String transition: matches Event.Type with SCXML wildcard rules ("*", "error.*").</summary>
    public StateBuilder<TContext> On(string eventPattern, Action<TransitionBuilder<TContext, MachineEvent>>? configure = null)
    {
        var t = new TransitionBuilder<TContext, MachineEvent>(new EventDescriptor.ByPattern(eventPattern));
        configure?.Invoke(t);
        Transitions.Add(t.Draft);
        return this;
    }

    /// <summary>
    /// Transition taken when this compound/parallel state is done — i.e. on
    /// <c>xstate.done.state.{thisStateId}</c>, raised once its child region(s) reach a final state.
    /// </summary>
    public StateBuilder<TContext> OnDone(Action<TransitionBuilder<TContext, DoneStateEvent>> configure)
    {
        var t = new TransitionBuilder<TContext, DoneStateEvent>(null);
        configure(t);
        OnDoneTransitions.Add(t.Draft);
        return this;
    }

    /// <summary>Eventless transition, checked after every microstep.</summary>
    public StateBuilder<TContext> Always(Action<TransitionBuilder<TContext, MachineEvent>> configure)
    {
        var t = new TransitionBuilder<TContext, MachineEvent>(null);
        configure(t);
        AlwaysTransitions.Add(t.Draft);
        return this;
    }

    /// <summary>Delayed transition (xstate `after`).</summary>
    public StateBuilder<TContext> After(TimeSpan delay, Action<TransitionBuilder<TContext, MachineEvent>> configure)
        => After((DelayRef<TContext>)delay, configure);

    public StateBuilder<TContext> After(DelayRef<TContext> delay, Action<TransitionBuilder<TContext, MachineEvent>> configure)
    {
        var t = new TransitionBuilder<TContext, MachineEvent>(null);
        configure(t);
        AfterTransitions.Add((delay, t.Draft));
        return this;
    }

    // --- Actions ---

    public StateBuilder<TContext> Entry(Action<ActionArgs<TContext>> action)
    {
        EntryActions.Add(new CustomAction<TContext>(action));
        return this;
    }

    public StateBuilder<TContext> Entry(ActionDefinition<TContext> action)
    {
        EntryActions.Add(action);
        return this;
    }

    /// <summary>Runs the machine-registered action <paramref name="name"/> on entry.</summary>
    public StateBuilder<TContext> Entry(string name, object? @params = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EntryActions.Add(new NamedAction<TContext>(name) { Name = name, Params = @params });
        return this;
    }

    /// <summary>Emits an event to the actor's subscribers on entry.</summary>
    public StateBuilder<TContext> EntryEmit(MachineEvent evt)
    {
        EntryActions.Add(new EmitAction<TContext>(_ => evt));
        return this;
    }

    /// <inheritdoc cref="EntryEmit(MachineEvent)"/>
    public StateBuilder<TContext> EntryEmit(Func<ActionArgs<TContext>, MachineEvent> eventFactory)
    {
        EntryActions.Add(new EmitAction<TContext>(eventFactory));
        return this;
    }

    public StateBuilder<TContext> EntryAssign(Func<ActionArgs<TContext>, TContext> assigner)
    {
        EntryActions.Add(new AssignAction<TContext>(assigner));
        return this;
    }

    public StateBuilder<TContext> Exit(Action<ActionArgs<TContext>> action)
    {
        ExitActions.Add(new CustomAction<TContext>(action));
        return this;
    }

    public StateBuilder<TContext> Exit(ActionDefinition<TContext> action)
    {
        ExitActions.Add(action);
        return this;
    }

    /// <summary>Runs the machine-registered action <paramref name="name"/> on exit.</summary>
    public StateBuilder<TContext> Exit(string name, object? @params = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ExitActions.Add(new NamedAction<TContext>(name) { Name = name, Params = @params });
        return this;
    }

    /// <summary>Emits an event to the actor's subscribers on exit.</summary>
    public StateBuilder<TContext> ExitEmit(MachineEvent evt)
    {
        ExitActions.Add(new EmitAction<TContext>(_ => evt));
        return this;
    }

    /// <inheritdoc cref="ExitEmit(MachineEvent)"/>
    public StateBuilder<TContext> ExitEmit(Func<ActionArgs<TContext>, MachineEvent> eventFactory)
    {
        ExitActions.Add(new EmitAction<TContext>(eventFactory));
        return this;
    }

    // --- Invoke ---

    /// <summary>
    /// Invokes <paramref name="logic"/> for as long as this state is active. When
    /// <paramref name="id"/> is omitted the actor id is xstate v6's <c>createInvokeId</c>
    /// (<c>utils.ts</c>) shape: <c>{index}.{stateNodeId}</c>, where <c>index</c> is this
    /// invocation's position in the state's invoke list.
    /// </summary>
    public StateBuilder<TContext> Invoke(
        IActorLogic logic,
        string? id = null,
        Func<ActionArgs<TContext>, object?>? input = null,
        Action<TransitionBuilder<TContext, DoneActorEvent>>? onDone = null,
        Action<TransitionBuilder<TContext, ErrorActorEvent>>? onError = null,
        DelayRef<TContext>? timeout = null,
        Action<TransitionBuilder<TContext, ActorTimeoutEvent>>? onTimeout = null,
        Action<TransitionBuilder<TContext, SnapshotEvent>>? onSnapshot = null,
        bool autoForward = false,
        Func<ActionArgs<TContext>, ScriptResult<TContext>?>? onExternalEvent = null) =>
        Invoke(
            new InvokeDraft<TContext>
            {
                Id = id, Logic = logic, InputResolver = input, Timeout = timeout,
                AutoForward = autoForward, OnExternalEvent = onExternalEvent
            },
            onDone, onError, onTimeout, onSnapshot);

    /// <summary>
    /// Invokes the logic registered under <paramref name="source"/> by
    /// <see cref="MachineBuilder{TContext}.Actor"/>. Naming the source (rather than passing the
    /// logic by value) is what records a <see cref="ChildRecord.Src"/> on the snapshot, and
    /// therefore what makes a snapshot holding this child persistable. The key is resolved when
    /// the machine is built, and an unregistered one throws there.
    /// </summary>
    public StateBuilder<TContext> Invoke(
        string source,
        string? id = null,
        Func<ActionArgs<TContext>, object?>? input = null,
        Action<TransitionBuilder<TContext, DoneActorEvent>>? onDone = null,
        Action<TransitionBuilder<TContext, ErrorActorEvent>>? onError = null,
        DelayRef<TContext>? timeout = null,
        Action<TransitionBuilder<TContext, ActorTimeoutEvent>>? onTimeout = null,
        Action<TransitionBuilder<TContext, SnapshotEvent>>? onSnapshot = null,
        bool autoForward = false,
        Func<ActionArgs<TContext>, ScriptResult<TContext>?>? onExternalEvent = null) =>
        Invoke(
            new InvokeDraft<TContext>
            {
                Id = id, Src = source, InputResolver = input, Timeout = timeout,
                AutoForward = autoForward, OnExternalEvent = onExternalEvent
            },
            onDone, onError, onTimeout, onSnapshot);

    private StateBuilder<TContext> Invoke(
        InvokeDraft<TContext> draft,
        Action<TransitionBuilder<TContext, DoneActorEvent>>? onDone,
        Action<TransitionBuilder<TContext, ErrorActorEvent>>? onError,
        Action<TransitionBuilder<TContext, ActorTimeoutEvent>>? onTimeout = null,
        Action<TransitionBuilder<TContext, SnapshotEvent>>? onSnapshot = null)
    {
        if (onDone is not null)
        {
            var t = new TransitionBuilder<TContext, DoneActorEvent>(null);
            onDone(t);
            draft.OnDone.Add(t.Draft);
        }
        if (onError is not null)
        {
            var t = new TransitionBuilder<TContext, ErrorActorEvent>(null);
            onError(t);
            draft.OnError.Add(t.Draft);
        }
        if (onTimeout is not null)
        {
            var t = new TransitionBuilder<TContext, ActorTimeoutEvent>(null);
            onTimeout(t);
            draft.OnTimeout.Add(t.Draft);
        }
        if (onSnapshot is not null)
        {
            var t = new TransitionBuilder<TContext, SnapshotEvent>(null);
            onSnapshot(t);
            draft.OnSnapshot.Add(t.Draft);
        }
        Invokes.Add(draft);
        return this;
    }

    public StateBuilder<TContext> Tag(string tag)
    {
        StateTags.Add(tag);
        return this;
    }
}

internal sealed class InvokeDraft<TContext>
{
    public string? Id { get; set; }

    /// <summary>The logic, when it was supplied by value; null when <see cref="Src"/> names it.</summary>
    public IActorLogic? Logic { get; set; }

    /// <summary>The registered source key, when the invoke named one.</summary>
    public string? Src { get; set; }

    public Func<ActionArgs<TContext>, object?>? InputResolver { get; set; }

    public bool AutoForward { get; set; }

    public Func<ActionArgs<TContext>, ScriptResult<TContext>?>? OnExternalEvent { get; set; }

    /// <summary>How long the actor may run before <c>xstate.timeout.actor</c> is raised.</summary>
    public DelayRef<TContext>? Timeout { get; set; }

    public List<TransitionDraft<TContext>> OnDone { get; } = [];
    public List<TransitionDraft<TContext>> OnError { get; } = [];
    public List<TransitionDraft<TContext>> OnTimeout { get; } = [];
    public List<TransitionDraft<TContext>> OnSnapshot { get; } = [];
}
