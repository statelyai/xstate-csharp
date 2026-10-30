namespace XState.Persistence;

/// <summary>
/// A persisted snapshot cannot represent a state of the machine it is restored into: an invalid
/// state value, malformed children or timers, a child source the machine cannot restore, or an
/// envelope that is not valid JSON for <see cref="PersistedSnapshot"/> — a port of xstate v6
/// <c>InvalidSnapshotError</c> (<c>StateMachine.ts</c>).
/// <para>
/// Deviation from v6, where <c>createActor(machine, { snapshot })</c> reports it as an
/// <c>'error'</c> snapshot: this port has no runtime, so restore throws it.
/// </para>
/// </summary>
public sealed class InvalidSnapshotException : InvalidOperationException
{
    public InvalidSnapshotException(string message) : base(message)
    {
    }

    public InvalidSnapshotException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
