namespace XState;

/// <summary>Arguments available to guard predicates.</summary>
public readonly record struct GuardArgs<TContext>(
    TContext Context,
    MachineEvent Event,
    // SCXML In() predicate: whether the given state id is in the current configuration.
    Func<string, bool> In)
{
    /// <summary>
    /// The parameters declared alongside this guard reference (xstate v6 <c>{ type, params }</c>).
    /// Null for an inline predicate.
    /// </summary>
    public object? Params { get; init; }
}

public delegate bool Guard<TContext>(GuardArgs<TContext> args);

/// <summary>
/// A guard with a portable identity: the predicate plus the name and parameters it was
/// referenced by (xstate v6 <c>{ type, params }</c>). Inline guards carry a null
/// <see cref="Name"/>.
/// </summary>
public sealed record GuardDefinition<TContext>(
    Guard<TContext> Predicate,
    string? Name = null,
    object? Params = null)
{
    /// <summary>Evaluates the predicate with <see cref="Params"/> bound onto the args.</summary>
    public bool Evaluate(GuardArgs<TContext> args) =>
        Predicate(Params is null ? args : args with { Params = Params });
}

public static class Guards
{
    /// <summary>Negates a guard (xstate `not`).</summary>
    public static Guard<TContext> Not<TContext>(Guard<TContext> guard) => args => !guard(args);

    /// <summary>All guards must pass (xstate `and`).</summary>
    public static Guard<TContext> And<TContext>(params Guard<TContext>[] guards) =>
        args => guards.All(g => g(args));

    /// <summary>Any guard must pass (xstate `or`).</summary>
    public static Guard<TContext> Or<TContext>(params Guard<TContext>[] guards) =>
        args => guards.Any(g => g(args));

    /// <summary>SCXML In() as a standalone guard.</summary>
    public static Guard<TContext> In<TContext>(string stateId) => args => args.In(stateId);
}
