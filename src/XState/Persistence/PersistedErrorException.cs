namespace XState.Persistence;

/// <summary>
/// The <see cref="Exception"/> a restored errored snapshot carries. A persisted failure is data
/// (<see cref="PersistedError"/>): the original exception's type may not exist in the restoring
/// process, and its stack trace certainly does not. This keeps what survived — the type name and
/// the message — visible instead of flattening it into a bare <see cref="Exception"/>.
/// </summary>
public sealed class PersistedErrorException : Exception
{
    public PersistedErrorException(PersistedError persisted)
        : base(Describe(persisted))
    {
        Persisted = persisted;
    }

    private static string Describe(PersistedError persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        return persisted.Message ?? $"Persisted error of type '{persisted.Type}'.";
    }

    /// <summary>The persisted failure this exception stands in for.</summary>
    public PersistedError Persisted { get; }

    /// <summary>The CLR type name of the exception that originally failed the actor.</summary>
    public string OriginalType => Persisted.Type;
}
