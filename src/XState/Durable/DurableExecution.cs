using System.Globalization;
using System.Runtime.CompilerServices;
using XState.Persistence;

namespace XState.Durable;

/// <summary>How a <see cref="DurableExecution{TContext}"/> is pinned to a journal.</summary>
public sealed record DurableExecutionOptions
{
    /// <summary>
    /// Index assigned to the first transition this execution runs. A worker resuming a checkpoint
    /// passes the persisted <see cref="DurableExecution{TContext}.NextTransitionIndex"/> so effect
    /// ids continue where the journal left off instead of colliding with it.
    /// </summary>
    public int TransitionIndex { get; init; }

    /// <summary>
    /// Opaque identity for this execution, carried for the host's journal keys.
    /// <para>
    /// Deviation from v6 (<c>durable/index.ts</c>:127-134), where <c>executionId</c> also pins
    /// session-id numbering, because v6 session ids embed a random per-process system id and
    /// journaled internal events would otherwise go stale across replays. This port's actor
    /// identity is address-first: addresses and incarnation tokens are already deterministic
    /// functions of the snapshot, so nothing here needs pinning and the value is metadata only.
    /// </para>
    /// </summary>
    public string? ExecutionId { get; init; }

    /// <summary>
    /// Overrides the execution's root address. Defaults to <see cref="ActorAddress.Root(string?)"/>
    /// of the machine's id; a machine running as someone else's child passes its own address so
    /// the effect descriptors it journals name the same actors the parent's do.
    /// </summary>
    public string? RootAddress { get; init; }
}

/// <summary>
/// A host-neutral durable execution around pure transitions — a port of xstate v6
/// <c>createDurable</c> (<c>durable/index.ts</c>).
/// <para>
/// The transition methods only calculate snapshots and tag their effects; persistence, retries,
/// messaging, timers and child execution stay with the <see cref="IDurableHost"/>. What this adds
/// over calling <see cref="StateMachine{TContext}.Transition"/> directly is the identity a durable
/// host needs: a stable id per effect and per wait, so the journal a crashed worker left behind
/// lines up with what a fresh worker recomputes.
/// </para>
/// <para>
/// Deviation from v6: v6 drives a live actor tree, so its <c>executeEffects</c> also waits for
/// operations that child actors initiate while reacting, and captures root-addressed events as
/// they are produced. This port has no runtime — effects are dispatched one at a time, in
/// initiation order, and a host that produces a root-addressed event along the way reports it with
/// <see cref="ReportRootEvent"/>. v6's <c>captureRootEvents: false</c> needs no option here: a
/// host that owns a single FIFO mailbox does not call <see cref="ReportRootEvent"/>, and feeds
/// <see cref="WaitForEvent"/> from that mailbox itself.
/// </para>
/// </summary>
public sealed class DurableExecution<TContext> : IDurableExecution
{
    private readonly StateMachine<TContext> _machine;
    private readonly IDurableHost _host;
    private readonly int _configuredTransitionIndex;

    // Root events captured by settled batches (or reported outside any batch), waiting for the
    // drive loop to take them through `WaitForEvent`. `_batch` buffers the reports of the
    // `ExecuteEffects` call in flight; a failed batch's reports are discarded with it.
    private readonly object _inboxLock = new();
    private readonly Queue<DurableInboxEntry> _pendingRootEvents = new();
    private List<DurableInboxEntry>? _batch;

    // Restored timers that carry a host-stamped `ScheduledAt`: their remaining delay is computed
    // when the re-arm reaches the host, not when `Restore` built it.
    private readonly ConditionalWeakTable<DurableEffect, LogicalTimer> _restoredTimerDeadlines = new();

    private int _startingTransitionIndex;
    private int _nextTransitionIndex;
    private int? _lastTransitionIndex;
    private int _executing;
    private bool _restored;

    public DurableExecution(
        StateMachine<TContext> machine,
        IDurableHost host,
        DurableExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(host);

        options ??= new DurableExecutionOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.TransitionIndex, nameof(options.TransitionIndex));

        _machine = machine;
        _host = host;
        _nextTransitionIndex = options.TransitionIndex;
        _startingTransitionIndex = options.TransitionIndex;
        _configuredTransitionIndex = options.TransitionIndex;
        _lastTransitionIndex = options.TransitionIndex == 0 ? null : options.TransitionIndex - 1;

        RootAddress = options.RootAddress ?? ActorAddress.Root(machine.Id);
        MachineId = machine.Id;
        MachineVersion = machine.Version;
        ExecutionId = options.ExecutionId;

        host.Attach(this);
    }

    /// <summary>
    /// The root actor's durable address. Known before any transition runs, so a host can label
    /// mailboxes and route messages without a snapshot.
    /// </summary>
    public string RootAddress { get; }

    /// <summary>The machine's id, for pinning a journal to the logic that produced it.</summary>
    public string MachineId { get; }

    /// <summary>
    /// The machine's declared version. Persist it with the execution and reject a worker whose
    /// machine version differs: a changed machine reorders effect ids, and memoized results
    /// silently misalign.
    /// </summary>
    public string? MachineVersion { get; }

    /// <summary>See <see cref="DurableExecutionOptions.ExecutionId"/>.</summary>
    public string? ExecutionId { get; }

    /// <summary>Index the next transition will be tagged with. Persist it with checkpoints.</summary>
    public int NextTransitionIndex => _nextTransitionIndex;

    /// <summary>The initial transition, tagged. Throws on a restored execution.</summary>
    public (State<TContext> State, IReadOnlyList<DurableEffect> Effects) InitialTransition(object? input = null)
    {
        if (_restored)
        {
            throw new InvalidOperationException("InitialTransition cannot initialize a restored execution.");
        }

        var (state, effects) = _machine.GetInitialState(input);
        return (state, Tag(effects));
    }

    /// <summary>One macrostep, tagged.</summary>
    public (State<TContext> State, IReadOnlyList<DurableEffect> Effects) Transition(
        State<TContext> state,
        MachineEvent evt)
    {
        var (next, effects) = _machine.Transition(state, evt);
        return (next, Tag(effects));
    }

    /// <summary>
    /// Reports an event addressed to this execution's root that the host produced while executing
    /// effects (an invoked child completing, a child reporting up). Reported events are handed out
    /// by <see cref="WaitForEvent"/>, in order, before it parks on the host — so the drive loop
    /// stays <c>Transition(state, await WaitForEvent())</c>.
    /// <para>
    /// A report made during <see cref="ExecuteEffects"/> belongs to that batch: it becomes
    /// pending only when the batch completes, and is discarded if the batch throws or is cancelled,
    /// because the retrying host re-executes the batch and reports it again. A report made outside
    /// any batch is pending immediately. Pending reports are what <see cref="Checkpoint"/> stores.
    /// </para>
    /// </summary>
    /// <param name="evt">The root-addressed event.</param>
    /// <param name="source">The sender's durable identity, stored with the event in a checkpoint.</param>
    public void ReportRootEvent(MachineEvent evt, SerializedActorRef? source = null)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var entry = new DurableInboxEntry(evt, source);

        lock (_inboxLock)
        {
            if (_batch is { } batch)
            {
                batch.Add(entry);
            }
            else
            {
                _pendingRootEvents.Enqueue(entry);
            }
        }
    }

    /// <summary>
    /// A checkpoint of <paramref name="state"/>: its persisted form, <see cref="NextTransitionIndex"/>
    /// and the inbox of root events captured by settled batches but not yet taken by
    /// <see cref="WaitForEvent"/> (v6 <c>checkpoint()</c>). Call it after
    /// <see cref="ExecuteEffects"/> resolves.
    /// <para>
    /// Child snapshot updates (<see cref="SnapshotEvent"/>) are not stored: a restored child
    /// re-reports its snapshot. Done, error and timeout events for a child that is no longer in
    /// <paramref name="state"/>, or whose incarnation no longer matches the event's
    /// <c>SessionId</c> because a later transition replaced it, are dropped.
    /// </para>
    /// </summary>
    /// <param name="state">The snapshot the last settled batch left the execution in.</param>
    /// <param name="options">
    /// Passed to <see cref="StateMachine{TContext}.Persist"/>. <see cref="PersistOptions.RootAddress"/>
    /// defaults to <see cref="RootAddress"/>.
    /// </param>
    public DurableCheckpoint Checkpoint(State<TContext> state, PersistOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        options = options is null
            ? new PersistOptions { RootAddress = RootAddress }
            : options with { RootAddress = options.RootAddress ?? RootAddress };
        var snapshot = _machine.Persist(state, options);

        DurableInboxEntry[] pending;
        lock (_inboxLock)
        {
            pending = [.. _pendingRootEvents];
        }

        var inbox = new List<DurableInboxEntry>(pending.Length);
        foreach (var entry in pending)
        {
            if (entry.Event is SnapshotEvent)
            {
                continue;
            }

            if (LifecycleTarget(entry.Event) is var (actorId, sessionId))
            {
                var child = FindChild(state, actorId);
                if (child is null || (sessionId is not null && child.Incarnation != sessionId))
                {
                    continue;
                }
            }

            inbox.Add(entry);
        }

        return new DurableCheckpoint(snapshot, _nextTransitionIndex, inbox);
    }

    /// <summary>
    /// Restores <paramref name="checkpoint"/> on a fresh execution (v6 <c>restore()</c>). The
    /// execution continues from <see cref="DurableCheckpoint.NextTransitionIndex"/>, and
    /// <see cref="WaitForEvent"/> hands out the checkpoint's inbox, in order, before it parks on
    /// the host.
    /// <para>
    /// A <see cref="DurableExecutionOptions.TransitionIndex"/> other than 0 must equal the
    /// checkpoint's index. v6 distinguishes an unset <c>transitionIndex</c> from 0; this port's
    /// index is a plain <c>int</c>, so 0 counts as unset.
    /// </para>
    /// <para>
    /// The returned effects re-arm the snapshot's pending timers (see <see cref="RearmTimers"/> for
    /// how the remaining delay is derived). Restoration does not advance the transition index, run
    /// entry actions or send the machine an event. Children are returned for the host to
    /// re-create or re-attach; the pure core does not start them.
    /// </para>
    /// </summary>
    /// <param name="checkpoint">A <see cref="Checkpoint"/> result, typically deserialized.</param>
    /// <param name="options">
    /// Passed to <see cref="StateMachine{TContext}.Restore"/>.
    /// <see cref="RestoreOptions{TContext}.RootAddress"/> defaults to <see cref="RootAddress"/>.
    /// </param>
    public DurableRestoreResult<TContext> Restore(DurableCheckpoint checkpoint, RestoreOptions<TContext>? options = null)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return Restore(checkpoint.Snapshot, checkpoint, options);
    }

    /// <summary>
    /// Restores a bare persisted snapshot on a fresh execution. The execution keeps the
    /// <see cref="DurableExecutionOptions.TransitionIndex"/> it was created with, so pass the
    /// persisted index there. There is no inbox to preload.
    /// </summary>
    /// <param name="snapshot">The persisted snapshot.</param>
    /// <param name="options">
    /// Passed to <see cref="StateMachine{TContext}.Restore"/>.
    /// <see cref="RestoreOptions{TContext}.RootAddress"/> defaults to <see cref="RootAddress"/>.
    /// </param>
    public DurableRestoreResult<TContext> Restore(PersistedSnapshot snapshot, RestoreOptions<TContext>? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Restore(snapshot, null, options);
    }

    private DurableRestoreResult<TContext> Restore(
        PersistedSnapshot snapshot,
        DurableCheckpoint? checkpoint,
        RestoreOptions<TContext>? options)
    {
        if (_restored || _nextTransitionIndex != _startingTransitionIndex || Volatile.Read(ref _executing) != 0)
        {
            throw new InvalidOperationException("Restore requires a fresh durable execution.");
        }

        if (checkpoint is not null)
        {
            if (checkpoint.Inbox is null || !checkpoint.Inbox.All(IsValidInboxEntry))
            {
                throw new InvalidSnapshotException("The checkpoint inbox is invalid.");
            }

            if (_configuredTransitionIndex != 0 && _configuredTransitionIndex != checkpoint.NextTransitionIndex)
            {
                throw new InvalidOperationException(
                    $"The execution's TransitionIndex ({_configuredTransitionIndex.ToString(CultureInfo.InvariantCulture)}) " +
                    "does not match the checkpoint's NextTransitionIndex " +
                    $"({checkpoint.NextTransitionIndex.ToString(CultureInfo.InvariantCulture)}).");
            }

            if (checkpoint.NextTransitionIndex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(checkpoint),
                    checkpoint.NextTransitionIndex,
                    "NextTransitionIndex must be a non-negative integer.");
            }
        }

        options = options is null
            ? new RestoreOptions<TContext> { RootAddress = RootAddress }
            : options with { RootAddress = options.RootAddress ?? RootAddress };
        var restored = _machine.Restore(snapshot, options);
        var state = restored.State;

        if (checkpoint is not null)
        {
            _nextTransitionIndex = checkpoint.NextTransitionIndex;
            _startingTransitionIndex = checkpoint.NextTransitionIndex;
            _lastTransitionIndex = _nextTransitionIndex == 0 ? null : _nextTransitionIndex - 1;

            lock (_inboxLock)
            {
                foreach (var entry in checkpoint.Inbox)
                {
                    var evt = entry.Event;
                    if (LifecycleTarget(evt) is var (actorId, sessionId))
                    {
                        var child = FindChild(state, actorId);
                        if (child is null)
                        {
                            continue;
                        }

                        // v6 re-stamps a stored lifecycle event with the restored child's session.
                        if (sessionId is not null)
                        {
                            evt = WithSessionId(evt, child.Incarnation);
                        }
                    }

                    _pendingRootEvents.Enqueue(entry with { Event = evt });
                }
            }
        }

        var effects = new List<DurableEffect>();
        if (state.Status is SnapshotStatus.Active)
        {
            for (var i = 0; i < restored.Timers.Count; i++)
            {
                var timer = restored.Timers[i];
                var tagged = RearmEffect(timer, _nextTransitionIndex, i);
                if (timer.ScheduledAt is not null)
                {
                    _restoredTimerDeadlines.Add(tagged, timer);
                }

                effects.Add(tagged);
            }
        }

        // v6 tags restoration effects at the checkpoint's index without advancing it, and a
        // restored execution at index 0 can wait (as `event:restore:0`).
        _lastTransitionIndex ??= _nextTransitionIndex;
        _restored = true;

        return new DurableRestoreResult<TContext>(state, effects, restored.Children);
    }

    private static bool IsValidInboxEntry(DurableInboxEntry? entry) =>
        entry is { Event: not null } &&
        (entry.Source is null || !string.IsNullOrEmpty(entry.Source.Actor));

    /// <summary>
    /// For a stored lifecycle event (done, error, timeout) the child id it addresses and the
    /// session it names; null for any other event.
    /// </summary>
    private static (string ActorId, string? SessionId)? LifecycleTarget(MachineEvent evt) => evt switch
    {
        DoneActorEvent done => (done.ActorId, done.SessionId),
        ErrorActorEvent error => (error.ActorId, error.SessionId),
        ActorTimeoutEvent timeout => (timeout.ActorId, timeout.SessionId),
        _ => null
    };

    private static MachineEvent WithSessionId(MachineEvent evt, string sessionId) => evt switch
    {
        DoneActorEvent done => done with { SessionId = sessionId },
        ErrorActorEvent error => error with { SessionId = sessionId },
        ActorTimeoutEvent timeout => timeout with { SessionId = sessionId },
        _ => evt
    };

    private static ChildRecord? FindChild(State<TContext> state, string actorId)
    {
        foreach (var child in state.Children)
        {
            if (child.Id == actorId)
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>
    /// Hands every effect to the host, one at a time, in initiation order.
    /// <para>
    /// Overlapping calls throw: the whole point of the ordering is that a host's step model sees
    /// one effect at a time, and a second batch entering while the first is in flight would
    /// interleave them. A failure aborts the batch and propagates — the effects were computed by a
    /// pure transition, so a retrying host recomputes the identical batch and re-executes it from
    /// the top, skipping whatever it has already journaled.
    /// </para>
    /// </summary>
    public async Task ExecuteEffects(IReadOnlyList<DurableEffect> effects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(effects);

        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "ExecuteEffects calls must not overlap: await the previous call before starting the next batch.");
        }

        var batch = new List<DurableInboxEntry>();
        lock (_inboxLock)
        {
            _batch = batch;
        }

        try
        {
            foreach (var tagged in effects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Dispatch(tagged).ConfigureAwait(false);
            }

            // Only a settled batch feeds the pending queue; a failed one is retried from the top
            // and reports its root events again.
            lock (_inboxLock)
            {
                _batch = null;
                foreach (var entry in batch)
                {
                    _pendingRootEvents.Enqueue(entry);
                }
            }
        }
        finally
        {
            lock (_inboxLock)
            {
                _batch = null;
            }

            Volatile.Write(ref _executing, 0);
        }
    }

    private ValueTask Dispatch(DurableEffect tagged) =>
        _restoredTimerDeadlines.TryGetValue(tagged, out var timer)
            ? _host.ScheduleTimer(tagged, timer.Key, RemainingDelay(timer, null))
            : DispatchEffect(tagged);

    private ValueTask DispatchEffect(DurableEffect tagged) => tagged.Effect switch
    {
        SpawnEffect spawn => _host.SpawnActor(tagged, spawn),
        StartEffect start => _host.StartActor(tagged, start),
        StopChildEffect stop => _host.StopActor(tagged, stop),
        TerminateEffect terminate => _host.Terminate(tagged, terminate),

        // A delayed raise is a timer on the raising actor itself; only the elapsed timer, reported
        // back as `xstate.timer`, turns it into an event.
        RaiseEffect raise => _host.ScheduleTimer(tagged, TimerKey.For(raise.Id, raise.Seq, raise.Event), raise.Delay),

        // A delayed send is a timer on the *sender*, not the target: the sender owns the ledger
        // entry and is the only actor that can cancel it.
        SendToEffect { Delay: { } delay } send =>
            _host.ScheduleTimer(tagged, TimerKey.For(send.Id, send.Seq, send.Event), delay),
        SendToEffect send => _host.SendEvent(tagged, send),

        CancelEffect cancel => _host.CancelTimer(tagged, cancel),
        DeadLetterEffect deadLetter => _host.DeadLetter(tagged, deadLetter),
        EmitEffect emit => _host.EmitEvent(tagged, emit),
        ActionEffect action => _host.ExecuteAction(tagged, action),

        _ => throw new NotSupportedException(
            $"The durable execution does not know how to dispatch a '{tagged.Effect.Kind}' effect.")
    };

    /// <summary>
    /// Re-arms the timers a restored snapshot still holds (<c>RestoreResult.Timers</c>) through the
    /// host's <see cref="IDurableHost.ScheduleTimer"/>, the way xstate v6's <c>Actor.start()</c>
    /// re-schedules <c>snapshot.timers</c> after a restore. Each is tagged
    /// <c>"restore:{transitionIndex}:{i}"</c> so a host that journals by effect id can recognise a
    /// re-arm it already accepted for this checkpoint.
    /// <para>
    /// The core records a timer's <em>declared</em> delay; a host may also have stamped
    /// <see cref="LogicalTimer.ScheduledAt"/> (Unix ms) into the persisted snapshot. A host that
    /// knows the deadline passes <paramref name="remainingDelay"/> to re-arm with what is left
    /// (clamped at zero); without it a timer carrying <c>ScheduledAt</c> re-arms with
    /// <c>ScheduledAt + Delay - now</c>, clamped between zero and the declared delay, the way v6's
    /// <c>Actor.start()</c> uses a persisted <c>scheduledAt</c>, and any other timer restarts from
    /// its full delay — so a worker that crashes repeatedly can postpone it indefinitely.
    /// </para>
    /// <para>
    /// <see cref="Restore(DurableCheckpoint, RestoreOptions{TContext}?)"/> returns the same re-arms
    /// as effects instead; use one or the other, not both.
    /// </para>
    /// </summary>
    public async Task RearmTimers(
        IReadOnlyList<LogicalTimer> timers,
        Func<LogicalTimer, TimeSpan>? remainingDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timers);

        for (var i = 0; i < timers.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timer = timers[i];
            var tagged = RearmEffect(timer, _startingTransitionIndex, i);
            await _host.ScheduleTimer(tagged, timer.Key, RemainingDelay(timer, remainingDelay)).ConfigureAwait(false);
        }
    }

    /// <summary>One restored timer as a re-arm effect, tagged <c>"restore:{transitionIndex}:{i}"</c>.</summary>
    private DurableEffect RearmEffect(LogicalTimer timer, int transitionIndex, int i)
    {
        Effect effect = timer.Kind is TimerKind.Raise
            ? new RaiseEffect(timer.Event, timer.Id, timer.Delay) { Seq = timer.Seq, Occurrence = timer.Occurrence }
            : new SendToEffect(timer.Target ?? EffectTarget.Self, timer.Event, timer.Id, timer.Delay)
            {
                Seq = timer.Seq,
                Occurrence = timer.Occurrence
            };
        return new DurableEffect(
            $"restore:{transitionIndex.ToString(CultureInfo.InvariantCulture)}:{i.ToString(CultureInfo.InvariantCulture)}",
            transitionIndex,
            i,
            RootAddress,
            effect,
            EffectDescriptor.Of(effect, RootAddress));
    }

    private static TimeSpan RemainingDelay(LogicalTimer timer, Func<LogicalTimer, TimeSpan>? remainingDelay)
    {
        if (remainingDelay is not null)
        {
            var remaining = remainingDelay(timer);
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }

        if (timer.ScheduledAt is not { } scheduledAt)
        {
            return timer.Delay;
        }

        var left = TimeSpan.FromMilliseconds(scheduledAt) + timer.Delay -
            TimeSpan.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return left < TimeSpan.Zero ? TimeSpan.Zero : left > timer.Delay ? timer.Delay : left;
    }

    /// <summary>
    /// The next event for the root actor: one a previous batch reported (or a restored checkpoint's
    /// inbox held), or — once none are queued — whatever the host's durable wait produces. After a
    /// restore at index 0 the wait id is <c>"event:restore:0"</c>.
    /// </summary>
    public async Task<MachineEvent> WaitForEvent(CancellationToken cancellationToken = default)
    {
        if (_lastTransitionIndex is not { } index)
        {
            throw new InvalidOperationException("Cannot wait for an event before the first transition or restore.");
        }

        lock (_inboxLock)
        {
            if (_pendingRootEvents.TryDequeue(out var queued))
            {
                return queued.Event;
            }
        }

        var wait = new DurableWait(
            _restored && _nextTransitionIndex == 0
                ? "event:restore:0"
                : $"event:{index.ToString(CultureInfo.InvariantCulture)}",
            index);

        return await _host.WaitForEvent(wait, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drives the execution to completion: initial transition, then
    /// <c>Transition(state, await WaitForEvent())</c> until the machine settles.
    /// <para>
    /// Fresh executions only. A resumed one (a nonzero
    /// <see cref="DurableExecutionOptions.TransitionIndex"/>, a restored one, or one that has
    /// already transitioned)
    /// throws <see cref="DurableExecutionResumeException"/>: <c>Run</c> starts from the machine's
    /// initial state, which is not where a checkpoint left off.
    /// </para>
    /// </summary>
    /// <returns>The machine's output when it completes.</returns>
    /// <exception cref="DurableExecutionCancelledException">The machine was stopped.</exception>
    public async Task<object?> Run(object? input = null, CancellationToken cancellationToken = default)
    {
        if (_restored || _startingTransitionIndex != 0 || _nextTransitionIndex != _startingTransitionIndex)
        {
            throw new DurableExecutionResumeException();
        }

        var (state, effects) = InitialTransition(input);
        await ExecuteEffects(effects, cancellationToken).ConfigureAwait(false);

        while (state.Status is SnapshotStatus.Active)
        {
            var evt = await WaitForEvent(cancellationToken).ConfigureAwait(false);
            (state, effects) = Transition(state, evt);
            await ExecuteEffects(effects, cancellationToken).ConfigureAwait(false);
        }

        return state.Status switch
        {
            SnapshotStatus.Done => state.Output,
            SnapshotStatus.Error => throw state.Error ?? new InvalidOperationException(
                $"Machine '{MachineId}' failed without an error."),
            _ => throw new DurableExecutionCancelledException()
        };
    }

    /// <summary>
    /// Tags one transition's effects. The index advances once per transition, whether or not the
    /// transition produced any effects, so a replay that recomputes the same sequence of
    /// transitions recomputes the same ids.
    /// </summary>
    private IReadOnlyList<DurableEffect> Tag(IReadOnlyList<Effect> effects)
    {
        var transitionIndex = _nextTransitionIndex++;
        _lastTransitionIndex = transitionIndex;

        var tagged = new DurableEffect[effects.Count];
        for (var i = 0; i < effects.Count; i++)
        {
            var effect = effects[i];
            tagged[i] = new DurableEffect(
                $"{transitionIndex.ToString(CultureInfo.InvariantCulture)}:{i.ToString(CultureInfo.InvariantCulture)}",
                transitionIndex,
                i,
                RootAddress,
                effect,
                EffectDescriptor.Of(effect, RootAddress));
        }

        return tagged;
    }
}
