using System.Threading.Channels;
using XState;
using XState.Durable;

namespace XState.Samples.DurableHost;

/// <summary>Stands in for the process dying while the durable loop is parked.</summary>
public sealed class WorkerCrashedException(string waitId)
    : Exception($"The worker died while parked on '{waitId}'.");

/// <summary>
/// A minimal durable host: an in-memory journal keyed by effect id, real timers, and a mailbox the
/// durable loop parks on. Everything a host has to supply, and nothing else.
/// </summary>
public sealed class JournalingHost : IDurableHost
{
    private readonly Channel<MachineEvent> _mailbox = Channel.CreateUnbounded<MachineEvent>();
    private readonly Dictionary<(string Source, string Id), CancellationTokenSource> _timers = new();
    private readonly Dictionary<string, ChildEntry> _children = new(StringComparer.Ordinal);

    /// <summary>Effect ids this host has already carried out. Replaying one is a no-op.</summary>
    private readonly HashSet<string> _done = new(StringComparer.Ordinal);

    /// <summary>What the host was asked to do, in order — the thing a real host would durably append.</summary>
    public List<string> Journal { get; } = [];

    /// <summary>Appends one line to the journal, and shows it.</summary>
    private void Log(string entry)
    {
        Journal.Add(entry);
        Console.WriteLine($"    {entry}");
    }

    /// <summary>
    /// The execution this host serves, handed over by the <see cref="DurableExecution{T}"/>
    /// constructor: a child completing is an event for the execution's own root and goes back
    /// through <see cref="IDurableExecution.ReportRootEvent"/>.
    /// </summary>
    public IDurableExecution? Execution { get; private set; }

    public void Attach(IDurableExecution execution) => Execution = execution;

    /// <summary>Simulates a crash the first time the loop parks on a durable wait.</summary>
    public bool CrashOnPark { get; init; }

    // --- Children ---

    /// <summary>
    /// Re-attaches the children a restored snapshot still expects, so a `StartEffect` that was
    /// journaled after the checkpoint (a spawn whose start had not run yet) finds its child.
    /// </summary>
    public void AttachRestoredChildren(IEnumerable<XState.Persistence.ChildToRestore> children)
    {
        foreach (var child in children)
        {
            if (child.Logic is { } logic)
            {
                _children[child.Id] = new ChildEntry(logic, child.Snapshot?.Context, child.Incarnation);
            }
        }
    }

    public ValueTask SpawnActor(DurableEffect effect, SpawnEffect spawn)
    {
        // The descriptor is the journalable view: the child is an address and a source key, never
        // a live object reference.
        Record(effect, $"actor={effect.Descriptor.Actor} src={spawn.Src}");
        _children[spawn.Id] = new ChildEntry(spawn.Logic, spawn.Input, spawn.Incarnation);
        return default;
    }

    public ValueTask StartActor(DurableEffect effect, StartEffect start)
    {
        Record(effect, $"actor={effect.Descriptor.Actor}");

        if (!_children.TryGetValue(start.Id, out var child))
        {
            throw new InvalidOperationException(
                $"Start for unknown child '{start.Id}': a resumed host must re-attach restored children first.");
        }

        var (snapshot, _) = child.Logic.GetInitialSnapshot(child.Input);

        // This child finishes as soon as it starts. The incarnation token goes back verbatim: it
        // is what lets the parent tell this child's completion from a stale one.
        if (snapshot.Status is SnapshotStatus.Done)
        {
            Execution!.ReportRootEvent(new DoneActorEvent(start.Id, snapshot.Output, child.Incarnation));
        }

        return default;
    }

    public ValueTask StopActor(DurableEffect effect, StopChildEffect stop)
    {
        Record(effect, $"actor={effect.Descriptor.Actor}");
        _children.Remove(stop.ChildId);
        return default;
    }

    public ValueTask Terminate(DurableEffect effect, TerminateEffect terminate)
    {
        Record(effect, $"status={terminate.Status} output={terminate.Output ?? "(none)"}");
        return default;
    }

    // --- Messaging ---
    //
    // `SendEvent` and `DeadLetter` are deliberately not implemented: this workflow never sends to
    // another actor and never rejects an event, and an operation left out throws by name rather
    // than silently doing something local. Add them the day the machine needs them.

    public ValueTask EmitEvent(DurableEffect effect, EmitEffect emit)
    {
        Record(effect, $"subscribers <- {emit.Event}");
        return default;
    }

    // --- Actions ---

    public ValueTask ExecuteAction(DurableEffect effect, ActionEffect action)
    {
        // At-most-once, keyed by effect id: a replay of the same transition recomputes the same
        // id, so a step this host already ran is skipped rather than run twice.
        if (_done.Contains(effect.Id))
        {
            Log($"{effect.Id,-8} action           (already journaled, skipped)");
            return default;
        }

        action.Exec();
        // Completion is recorded only once the action ran: an action that throws is retried on
        // replay rather than skipped. A real host persists this atomically with its journal.
        _done.Add(effect.Id);
        Record(effect, $"type={action.Type ?? "(inline)"}");
        return default;
    }

    // --- Timers ---

    // The machine owns the timer id; the host owns only the clock. The originating effect is
    // passed so the operation can be journaled under its effect id like every other one.

    public ValueTask ScheduleTimer(DurableEffect effect, string id, TimeSpan delay)
    {
        Log($"{effect.Id,-8} schedule         {id} in {delay.TotalMilliseconds}ms");

        // Timer ids are per actor, so the alarm is keyed by the owning actor's address too.
        var cts = new CancellationTokenSource();
        _timers[(effect.Source, id)] = cts;

        // When it elapses, the host tells the actor which id fired — nothing more. The machine
        // looks the id up on its own ledger and decides what firing means.
        _ = Task.Delay(delay, cts.Token).ContinueWith(
            task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    _mailbox.Writer.TryWrite(new TimerEvent(id));
                }
            },
            TaskScheduler.Default);

        return default;
    }

    public ValueTask CancelTimer(DurableEffect effect, CancelEffect cancel)
    {
        var id = cancel.SendId;
        Log($"{effect.Id,-8} cancel           {id}");

        if (_timers.Remove((effect.Source, id), out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        return default;
    }

    // --- The durable wait ---

    public async ValueTask<MachineEvent> WaitForEvent(DurableWait wait, CancellationToken cancellationToken)
    {
        Log($"{wait.Id,-8} park");

        if (CrashOnPark)
        {
            throw new WorkerCrashedException(wait.Id);
        }

        return await _mailbox.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Appends one effect to the journal under the id the machine gave it.</summary>
    private void Record(DurableEffect effect, string detail) =>
        Log($"{effect.Id,-8} {effect.Descriptor.Kind,-16} {detail}");

    private sealed record ChildEntry(IActorLogic Logic, object? Input, string Incarnation);
}
