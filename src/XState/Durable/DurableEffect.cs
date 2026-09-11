namespace XState.Durable;

/// <summary>
/// One effect of a durable execution, tagged with the identity a host journals it under.
/// <para>
/// <see cref="Id"/> is <c>"{transitionIndex}:{effectIndex}"</c>: a pure transition produces the
/// same effects in the same order every time it is replayed from the same snapshot and event, so
/// the position of an effect <em>is</em> its identity. That is what lets a host memoize a
/// completed step and skip it on replay.
/// </para>
/// </summary>
/// <param name="Source">The address of the actor that produced the effect.</param>
/// <param name="Descriptor">
/// The serializable view of <paramref name="Effect"/> (<see cref="EffectDescriptor"/>) — what a
/// journal records, with live logic and closures dropped and actor references resolved to
/// addresses.
/// </param>
public sealed record DurableEffect(
    string Id,
    int TransitionIndex,
    int EffectIndex,
    string Source,
    Effect Effect,
    EffectDescriptor Descriptor);

/// <summary>
/// The context-type-agnostic face of a <see cref="DurableExecution{TContext}"/>: what a host needs
/// to label its journal and to hand root-addressed events back to the loop.
/// </summary>
public interface IDurableExecution
{
    /// <summary>The root actor's durable address.</summary>
    string RootAddress { get; }

    /// <summary>The machine's id.</summary>
    string MachineId { get; }

    /// <summary>The machine's declared version, if any.</summary>
    string? MachineVersion { get; }

    /// <summary>Index the next transition will be tagged with.</summary>
    int NextTransitionIndex { get; }

    /// <summary>
    /// Reports an event addressed to the execution's root that the host produced while executing
    /// effects; <c>WaitForEvent</c> hands reported events out, in order, before parking on the host.
    /// </summary>
    void ReportRootEvent(MachineEvent evt);
}

/// <summary>
/// One durable wait, tagged the way <see cref="DurableEffect"/> is: <see cref="Id"/> is
/// <c>"event:{transitionIndex}"</c>, the transition whose effects have just been executed, so a
/// replaying host can hand back the event it recorded for that point instead of waiting again.
/// </summary>
public sealed record DurableWait(string Id, int TransitionIndex);

/// <summary>
/// A durable execution ended in <see cref="SnapshotStatus.Stopped"/> — the actor was stopped
/// rather than completing or failing, so there is no output to return and no error to throw.
/// </summary>
/// <remarks>
/// Named for the .NET convention rather than v6's <c>DurableExecutionCancelledError</c>.
/// </remarks>
public sealed class DurableExecutionCancelledException : Exception
{
    public DurableExecutionCancelledException()
        : base("Durable execution was stopped")
    {
    }

    public DurableExecutionCancelledException(string message) : base(message)
    {
    }

    public DurableExecutionCancelledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// <see cref="DurableExecution{TContext}.Run"/> was called on an execution that is not fresh.
/// Resuming a checkpoint means feeding the restored snapshot back through
/// <see cref="DurableExecution{TContext}.Transition"/>; <c>Run</c> owns the whole loop from the
/// initial transition and cannot pick one up halfway.
/// </summary>
/// <remarks>
/// Named for the .NET convention rather than v6's <c>DurableExecutionResumeError</c>.
/// </remarks>
public sealed class DurableExecutionResumeException : Exception
{
    public DurableExecutionResumeException()
        : base("Run() can only start a fresh durable execution; resume checkpoints with " +
               "Transition() and the persisted snapshot")
    {
    }

    public DurableExecutionResumeException(string message) : base(message)
    {
    }

    public DurableExecutionResumeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
