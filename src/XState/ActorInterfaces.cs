namespace XState;

public enum SnapshotStatus
{
    Active,
    Done,
    Error,
    Stopped
}

/// <summary>Minimal snapshot surface shared by all actor logic types.</summary>
public interface ISnapshot
{
    SnapshotStatus Status { get; }
    object? Output { get; }
    Exception? Error { get; }
}

/// <summary>
/// Describes invokable behavior (the `logic` of an invoked/spawned child): state machines
/// implement this, and consumers can implement it for their own logic kinds (tasks,
/// callbacks, …). XState.NET ships no runtime — the pure pair below is the whole contract, and
/// a host decides how to execute the <see cref="Effect"/>s it hands back.
/// </summary>
public interface IActorLogic
{
    /// <summary>The initial snapshot and the effects that starting the actor produces.</summary>
    (ISnapshot State, IReadOnlyList<Effect> Effects) GetInitialSnapshot(object? input);

    /// <summary>
    /// One macrostep. <paramref name="state"/> must be a snapshot this logic produced.
    /// </summary>
    (ISnapshot State, IReadOnlyList<Effect> Effects) Transition(ISnapshot state, MachineEvent evt);
}

/// <summary>
/// Untyped bridge over <see cref="StateMachine{TContext}"/> so generic tooling (stores,
/// interpreters, test harnesses) can drive any machine without knowing its context type.
/// </summary>
public interface IMachineLogic : IActorLogic
{
    string Id { get; }

    /// <summary>Machine version, for persisted-snapshot migration checks.</summary>
    string? Version { get; }

    /// <summary>Registered actor sources, by source key.</summary>
    IReadOnlyDictionary<string, IActorLogic> Sources { get; }

    /// <summary>
    /// Untyped <see cref="StateMachine{TContext}.Persist"/>, so a host holding a child only as
    /// <see cref="IActorLogic"/> can still embed its snapshot in the parent's checkpoint.
    /// </summary>
    Persistence.PersistedSnapshot Persist(ISnapshot state, Persistence.PersistOptions? options = null);

    /// <summary>
    /// Untyped <see cref="StateMachine{TContext}.Restore"/>. <paramref name="contextConverter"/>
    /// plays the role of <c>RestoreOptions.ContextConverter</c> for a context that came through a
    /// serializer; its result is cast to the machine's context type.
    /// </summary>
    Persistence.IRestoreResult Restore(
        Persistence.PersistedSnapshot snapshot,
        Func<Persistence.PersistedSnapshot, string?, Persistence.PersistedSnapshot>? migrate = null,
        Func<object?, object?>? contextConverter = null,
        string? rootAddress = null);
}
