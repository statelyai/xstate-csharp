namespace XState.Json;

/// <summary>
/// The named implementations a JSON machine config refers to by name: guards, actions,
/// actor logic (invoke <c>src</c>) and delays — plus an optional context factory used by
/// <see cref="MachineConfig.FromJson{TContext}"/>.
/// <code>
/// var impls = new MachineImplementations&lt;JsonElement&gt;()
///     .Guard("isReady", a =&gt; a.Context.GetProperty("ready").GetBoolean())
///     .Action("track", a =&gt; Console.WriteLine(a.Event.Type))
///     .ActorLogic("fetchUser", fetchUserMachine)
///     .Delay("slow", TimeSpan.FromSeconds(5));
/// </code>
/// </summary>
public sealed class MachineImplementations<TContext>
{
    /// <summary>Guard name → predicate (JSON <c>"guard": "name"</c> or <c>{ "type": "name" }</c>).</summary>
    public Dictionary<string, Guard<TContext>> Guards { get; } = new(StringComparer.Ordinal);

    /// <summary>Action name → action (JSON <c>"actions": "name"</c> or <c>{ "type": "name" }</c>).</summary>
    public Dictionary<string, ActionDefinition<TContext>> Actions { get; } = new(StringComparer.Ordinal);

    /// <summary>Actor source name → logic (JSON <c>"invoke": { "src": "name" }</c>).</summary>
    public Dictionary<string, IActorLogic> Actors { get; } = new(StringComparer.Ordinal);

    /// <summary>Delay name → delay (JSON <c>"after": { "name": … }</c>). Overrides the config's own <c>delays</c>.</summary>
    public Dictionary<string, DelayRef<TContext>> Delays { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Builds the machine context. Receives the actor's input when one is supplied, otherwise
    /// the config's <c>context</c> value as a <see cref="System.Text.Json.JsonElement"/>.
    /// Required by <see cref="MachineConfig.FromJson{TContext}"/>; optional for the
    /// JsonElement-context overload.
    /// </summary>
    public Func<object?, TContext>? ContextFactory { get; set; }

    public MachineImplementations<TContext> Guard(string name, Guard<TContext> guard)
    {
        Guards[name] = guard;
        return this;
    }

    public MachineImplementations<TContext> Action(string name, ActionDefinition<TContext> action)
    {
        Actions[name] = action with { Name = name };
        return this;
    }

    public MachineImplementations<TContext> Action(string name, Action<ActionArgs<TContext>> action)
    {
        Actions[name] = new CustomAction<TContext>(action) { Name = name };
        return this;
    }

    public MachineImplementations<TContext> ActorLogic(string name, IActorLogic logic)
    {
        Actors[name] = logic;
        return this;
    }

    public MachineImplementations<TContext> Delay(string name, DelayRef<TContext> delay)
    {
        Delays[name] = delay;
        return this;
    }

    public MachineImplementations<TContext> Delay(string name, TimeSpan delay)
    {
        Delays[name] = new DelayRef<TContext>(_ => delay, name);
        return this;
    }

    /// <summary>Sets <see cref="ContextFactory"/>.</summary>
    public MachineImplementations<TContext> Context(Func<object?, TContext> factory)
    {
        ContextFactory = factory;
        return this;
    }
}
