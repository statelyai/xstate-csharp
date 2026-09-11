namespace XState.Durable;

/// <summary>
/// What a durable host must be able to do with the effects a machine produces — a port of xstate
/// v6's <c>DurableExecutionAdapter</c> plus the runtime operations it inherits
/// (<c>durable/index.ts</c>, <c>system.ts</c> <c>RUNTIME_OPERATIONS</c>).
/// <para>
/// Only <see cref="ExecuteAction"/> and <see cref="WaitForEvent"/> have no sensible default: every
/// other operation throws <see cref="NotSupportedException"/> naming itself, matching v6's rule
/// that "with no runtime operations at all, unsupported operations throw" rather than silently
/// running local behaviour on a durable host.
/// </para>
/// </summary>
public interface IDurableHost
{
    /// <summary>Creates a child actor (not yet started).</summary>
    ValueTask SpawnActor(DurableEffect effect, SpawnEffect spawn) => throw Unsupported(nameof(SpawnActor));

    /// <summary>Starts a child spawned earlier in the same macrostep.</summary>
    ValueTask StartActor(DurableEffect effect, StartEffect start) => throw Unsupported(nameof(StartActor));

    /// <summary>Stops a child actor.</summary>
    ValueTask StopActor(DurableEffect effect, StopChildEffect stop) => throw Unsupported(nameof(StopActor));

    /// <summary>
    /// The actor reached a terminal status; the host relays the corresponding done/error event to
    /// the parent.
    /// </summary>
    ValueTask Terminate(DurableEffect effect, TerminateEffect terminate) => throw Unsupported(nameof(Terminate));

    /// <summary>Delivers an event to another actor now.</summary>
    ValueTask SendEvent(DurableEffect effect, SendToEffect send) => throw Unsupported(nameof(SendEvent));

    /// <summary>
    /// Arms a timer on the actor at <see cref="DurableEffect.Source"/>. When it elapses the host
    /// sends that actor <see cref="TimerEvent"/> with <paramref name="id"/>; the machine decides
    /// what firing means, and ignores an id it no longer holds. The originating effect (a
    /// <see cref="RaiseEffect"/> or a delayed <see cref="SendToEffect"/>) is passed so the host can
    /// journal the operation under <see cref="DurableEffect.Id"/> like every other one.
    /// </summary>
    ValueTask ScheduleTimer(DurableEffect effect, string id, TimeSpan delay) =>
        throw Unsupported(nameof(ScheduleTimer));

    /// <summary>Cancels a timer armed by <see cref="ScheduleTimer"/>.</summary>
    ValueTask CancelTimer(DurableEffect effect, CancelEffect cancel) => throw Unsupported(nameof(CancelTimer));

    /// <summary>
    /// Called once by the <see cref="DurableExecution{TContext}"/> constructor with the execution
    /// this host serves, so a host that produces root-addressed events (an invoked child
    /// completing) can hand them to <see cref="IDurableExecution.ReportRootEvent"/> without
    /// knowing the machine's context type. Default: no-op.
    /// </summary>
    void Attach(IDurableExecution execution)
    {
    }

    /// <summary>An event was rejected at the actor boundary and never delivered.</summary>
    ValueTask DeadLetter(DurableEffect effect, DeadLetterEffect deadLetter) => throw Unsupported(nameof(DeadLetter));

    /// <summary>Emits an event to the actor's subscribers.</summary>
    ValueTask EmitEvent(DurableEffect effect, EmitEffect emit) => throw Unsupported(nameof(EmitEvent));

    /// <summary>
    /// Runs one action. Required: this is where a durable host's step/activity model plugs in, and
    /// it should memoize or deduplicate by <see cref="DurableEffect.Id"/> so a replay does not run
    /// the action twice.
    /// </summary>
    ValueTask ExecuteAction(DurableEffect effect, ActionEffect action);

    /// <summary>
    /// Waits durably for the next event addressed to this execution's root actor. Required: a
    /// durable loop is defined by being able to park here and come back in another process.
    /// </summary>
    ValueTask<MachineEvent> WaitForEvent(DurableWait wait, CancellationToken cancellationToken);

    private static NotSupportedException Unsupported(string operation) =>
        new($"The durable host does not support the {operation} operation.");
}

/// <summary>
/// A durable host that runs actions in-process. Convenient for tests and for hosts whose actions
/// are genuinely local; every other operation keeps <see cref="IDurableHost"/>'s throwing default,
/// so an unimplemented one fails loudly instead of being silently skipped.
/// </summary>
public abstract class DurableHostBase : IDurableHost
{
    /// <summary>Runs the action's local closure.</summary>
    public virtual ValueTask ExecuteAction(DurableEffect effect, ActionEffect action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action.Exec();
        return default;
    }

    /// <inheritdoc cref="IDurableHost.WaitForEvent" />
    public abstract ValueTask<MachineEvent> WaitForEvent(DurableWait wait, CancellationToken cancellationToken);
}
