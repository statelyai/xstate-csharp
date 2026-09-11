namespace XState.Builder;

/// <summary>Typed view of transition args: Event is the concrete matched event type.</summary>
public readonly record struct TransitionArgs<TContext, TEvent>(
    TContext Context,
    TEvent Event,
    Func<string, bool> In) where TEvent : MachineEvent
{
    /// <summary>Parameters of the named action/guard this args instance was built for.</summary>
    public object? Params { get; init; }

    /// <summary>The state input in scope (see <see cref="ActionArgs{TContext}.Input"/>).</summary>
    public object? Input { get; init; }
}

/// <summary>Unresolved transition accumulated by the builder; resolved at Build().</summary>
public sealed class TransitionDraft<TContext>
{
    public EventDescriptor? Event { get; init; }
    public Guard<TContext>? Guard { get; set; }

    /// <summary>Name of a machine-registered guard, when the transition referenced one.</summary>
    public string? GuardName { get; set; }

    /// <summary>Params passed alongside <see cref="GuardName"/>.</summary>
    public object? GuardParams { get; set; }

    public List<string> TargetIds { get; } = [];
    public bool Reenter { get; set; }
    public TransitionDomain? Domain { get; set; }
    public List<ActionDefinition<TContext>> Actions { get; } = [];
    public Func<ActionArgs<TContext>, object?>? InputResolver { get; set; }
    public object? Meta { get; set; }
    public string? Description { get; set; }
}

/// <summary>Fluent transition config: target, guard, actions — in any order.</summary>
public sealed class TransitionBuilder<TContext, TEvent> where TEvent : MachineEvent
{
    internal TransitionDraft<TContext> Draft { get; }

    internal TransitionBuilder(EventDescriptor? eventDescriptor)
    {
        Draft = new TransitionDraft<TContext> { Event = eventDescriptor };
    }

    public TransitionBuilder<TContext, TEvent> Target(params string[] stateKeys)
    {
        Draft.TargetIds.AddRange(stateKeys);
        return this;
    }

    /// <summary>
    /// Guard with the typed event. Given priority over the <see cref="Guard{TContext}"/>
    /// overload so that a lambda (which is convertible to both) binds here.
    /// </summary>
    [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
    public TransitionBuilder<TContext, TEvent> Guard(Func<TransitionArgs<TContext, TEvent>, bool> predicate)
    {
        Draft.Guard = args => predicate(Typed(args));
        Draft.GuardName = null;
        Draft.GuardParams = null;
        return this;
    }

    public TransitionBuilder<TContext, TEvent> Guard(Guard<TContext> guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        Draft.Guard = guard;
        Draft.GuardName = null;
        Draft.GuardParams = null;
        return this;
    }

    /// <summary>
    /// Guard registered on the machine under <paramref name="name"/> by
    /// <see cref="MachineBuilder{TContext}.Guard(string, XState.Guard{TContext})"/>, with optional
    /// parameters (xstate v6 <c>{ type, params }</c>). Resolved when the machine is built; an
    /// unregistered name is a build-time error.
    /// </summary>
    public TransitionBuilder<TContext, TEvent> Guard(string name, object? @params = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Draft.Guard = null;
        Draft.GuardName = name;
        Draft.GuardParams = @params;
        return this;
    }

    /// <summary>
    /// Re-enter the source state when targeting itself or a descendant (xstate v6 `reenter: true`;
    /// SCXML external-transition behavior). Default is false: such transitions do not exit/re-enter
    /// the source.
    /// </summary>
    public TransitionBuilder<TContext, TEvent> Reenter()
    {
        Draft.Reenter = true;
        return this;
    }

    /// <summary>
    /// Forces the transition's domain to the source state — the source is not exited, whatever
    /// the targets are (xstate v6 <c>_transitionDomain: 'internal'</c>).
    /// </summary>
    public TransitionBuilder<TContext, TEvent> Internal()
    {
        Draft.Domain = TransitionDomain.Internal;
        return this;
    }

    /// <summary>
    /// Forces the transition's domain to the source's parent — the source is always exited and
    /// re-entered (xstate v6 <c>_transitionDomain: 'external'</c>).
    /// </summary>
    public TransitionBuilder<TContext, TEvent> External()
    {
        Draft.Domain = TransitionDomain.External;
        return this;
    }

    /// <summary>
    /// The state input this transition carries: recorded under every target's id and surfaced to
    /// that state's entry/exit actions, invokes and output as <c>args.Input</c>.
    /// </summary>
    public TransitionBuilder<TContext, TEvent> Input(object? input)
    {
        Draft.InputResolver = _ => input;
        return this;
    }

    /// <inheritdoc cref="Input(object?)"/>
    public TransitionBuilder<TContext, TEvent> Input(Func<TransitionArgs<TContext, TEvent>, object?> resolver)
    {
        Draft.InputResolver = args => resolver(Typed(args));
        return this;
    }

    /// <summary>Arbitrary metadata carried on the transition definition.</summary>
    public TransitionBuilder<TContext, TEvent> Meta(object? meta)
    {
        Draft.Meta = meta;
        return this;
    }

    /// <summary>Human-readable description of the transition.</summary>
    public TransitionBuilder<TContext, TEvent> Description(string description)
    {
        Draft.Description = description;
        return this;
    }

    // --- Actions (executed in declaration order) ---

    /// <summary>The typed view of one action invocation — including the live In() predicate.</summary>
    private static TransitionArgs<TContext, TEvent> Typed(ActionArgs<TContext> args) =>
        new(args.Context, (TEvent)args.Event, args.In) { Params = args.Params, Input = args.Input };

    private static TransitionArgs<TContext, TEvent> Typed(GuardArgs<TContext> args) =>
        new(args.Context, (TEvent)args.Event, args.In) { Params = args.Params };

    public TransitionBuilder<TContext, TEvent> Assign(Func<TransitionArgs<TContext, TEvent>, TContext> assigner)
    {
        Draft.Actions.Add(new AssignAction<TContext>(args => assigner(Typed(args))));
        return this;
    }

    public TransitionBuilder<TContext, TEvent> Do(Action<TransitionArgs<TContext, TEvent>> effect)
    {
        Draft.Actions.Add(new CustomAction<TContext>(args => effect(Typed(args))));
        return this;
    }

    public TransitionBuilder<TContext, TEvent> Do(ActionDefinition<TContext> action)
    {
        Draft.Actions.Add(action);
        return this;
    }

    /// <summary>
    /// Runs the action registered on the machine under <paramref name="name"/> by
    /// <see cref="MachineBuilder{TContext}.Action(string, ActionDefinition{TContext})"/>, with
    /// optional parameters (xstate v6 <c>{ type, params }</c>).
    /// </summary>
    public TransitionBuilder<TContext, TEvent> Do(string name, object? @params = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Draft.Actions.Add(new NamedAction<TContext>(name) { Name = name, Params = @params });
        return this;
    }

    /// <summary>Adds several actions in order.</summary>
    public TransitionBuilder<TContext, TEvent> Actions(params ActionDefinition<TContext>[] actions)
    {
        Draft.Actions.AddRange(actions);
        return this;
    }

    public TransitionBuilder<TContext, TEvent> Raise(MachineEvent evt, TimeSpan? delay = null, string? id = null)
    {
        Draft.Actions.Add(new RaiseAction<TContext>(
            _ => evt,
            delay is { } d ? (DelayRef<TContext>)d : null,
            id));
        return this;
    }

    public TransitionBuilder<TContext, TEvent> Raise(Func<TransitionArgs<TContext, TEvent>, MachineEvent> eventFactory)
    {
        Draft.Actions.Add(new RaiseAction<TContext>(args => eventFactory(Typed(args))));
        return this;
    }

    public TransitionBuilder<TContext, TEvent> SendTo(
        string targetId,
        Func<TransitionArgs<TContext, TEvent>, MachineEvent> eventFactory,
        TimeSpan? delay = null,
        string? id = null)
    {
        Draft.Actions.Add(new SendToAction<TContext>(
            _ => targetId,
            args => eventFactory(Typed(args)),
            delay is { } d ? (DelayRef<TContext>)d : null,
            id));
        return this;
    }

    /// <summary>Emit an event to the actor's subscribers.</summary>
    public TransitionBuilder<TContext, TEvent> Emit(Func<TransitionArgs<TContext, TEvent>, MachineEvent> eventFactory)
    {
        Draft.Actions.Add(new EmitAction<TContext>(args => eventFactory(Typed(args))));
        return this;
    }

    public TransitionBuilder<TContext, TEvent> Emit(MachineEvent evt)
    {
        Draft.Actions.Add(new EmitAction<TContext>(_ => evt));
        return this;
    }

    public TransitionBuilder<TContext, TEvent> Log(Func<TransitionArgs<TContext, TEvent>, object?> message, string? label = null)
    {
        Draft.Actions.Add(new LogAction<TContext>(args => message(Typed(args)), label));
        return this;
    }
}
