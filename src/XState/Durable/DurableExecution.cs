using System.Globalization;

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
/// <see cref="ReportRootEvent"/>.
/// </para>
/// </summary>
public sealed class DurableExecution<TContext> : IDurableExecution
{
    private readonly StateMachine<TContext> _machine;
    private readonly IDurableHost _host;
    private readonly Queue<MachineEvent> _rootEvents = new();
    private readonly int _startingTransitionIndex;

    private int _nextTransitionIndex;
    private int? _lastTransitionIndex;
    private int _executing;

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

    /// <summary>The initial transition, tagged.</summary>
    public (State<TContext> State, IReadOnlyList<DurableEffect> Effects) InitialTransition(object? input = null)
    {
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
    /// </summary>
    public void ReportRootEvent(MachineEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        _rootEvents.Enqueue(evt);
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

        try
        {
            foreach (var tagged in effects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Dispatch(tagged).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _executing, 0);
        }
    }

    private ValueTask Dispatch(DurableEffect tagged) => tagged.Effect switch
    {
        SpawnEffect spawn => _host.SpawnActor(tagged, spawn),
        StartEffect start => _host.StartActor(tagged, start),
        StopChildEffect stop => _host.StopActor(tagged, stop),
        TerminateEffect terminate => _host.Terminate(tagged, terminate),

        // A delayed raise is a timer on the raising actor itself; only the elapsed timer, reported
        // back as `xstate.timer`, turns it into an event.
        RaiseEffect raise => _host.ScheduleTimer(tagged, raise.Id!, raise.Delay),

        // A delayed send is a timer on the *sender*, not the target: the sender owns the ledger
        // entry and is the only actor that can cancel it.
        SendToEffect { Delay: { } delay } send => _host.ScheduleTimer(tagged, send.Id!, delay),
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
    /// The snapshot records a timer's <em>declared</em> delay only. A host that persisted the
    /// deadline when it accepted <see cref="IDurableHost.ScheduleTimer"/> passes
    /// <paramref name="remainingDelay"/> to re-arm with what is left (clamped at zero), the way
    /// v6's <c>Actor.start()</c> uses a persisted <c>startedAt</c>; without it every timer restarts
    /// from its full delay, so a worker that crashes repeatedly can postpone it indefinitely.
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
            Effect effect = timer.Kind is TimerKind.Raise
                ? new RaiseEffect(timer.Event, timer.Id, timer.Delay)
                : new SendToEffect(timer.Target ?? EffectTarget.Self, timer.Event, timer.Id, timer.Delay);
            var tagged = new DurableEffect(
                $"restore:{_startingTransitionIndex.ToString(CultureInfo.InvariantCulture)}:{i.ToString(CultureInfo.InvariantCulture)}",
                _startingTransitionIndex,
                i,
                RootAddress,
                effect,
                EffectDescriptor.Of(effect, RootAddress));
            var delay = remainingDelay is null ? timer.Delay : remainingDelay(timer);
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            await _host.ScheduleTimer(tagged, timer.Id, delay).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The next event for the root actor: one a previous batch reported, or — once none are
    /// queued — whatever the host's durable wait produces.
    /// </summary>
    public async Task<MachineEvent> WaitForEvent(CancellationToken cancellationToken = default)
    {
        if (_lastTransitionIndex is not { } index)
        {
            throw new InvalidOperationException("Cannot wait for an event before the first transition.");
        }

        if (_rootEvents.Count > 0)
        {
            return _rootEvents.Dequeue();
        }

        var wait = new DurableWait(
            $"event:{index.ToString(CultureInfo.InvariantCulture)}",
            index);

        return await _host.WaitForEvent(wait, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drives the execution to completion: initial transition, then
    /// <c>Transition(state, await WaitForEvent())</c> until the machine settles.
    /// <para>
    /// Fresh executions only. A resumed one (a nonzero
    /// <see cref="DurableExecutionOptions.TransitionIndex"/>, or one that has already transitioned)
    /// throws <see cref="DurableExecutionResumeException"/>: <c>Run</c> starts from the machine's
    /// initial state, which is not where a checkpoint left off.
    /// </para>
    /// </summary>
    /// <returns>The machine's output when it completes.</returns>
    /// <exception cref="DurableExecutionCancelledException">The machine was stopped.</exception>
    public async Task<object?> Run(object? input = null, CancellationToken cancellationToken = default)
    {
        if (_startingTransitionIndex != 0 || _nextTransitionIndex != _startingTransitionIndex)
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
