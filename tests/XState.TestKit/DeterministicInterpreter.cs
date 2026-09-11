namespace XState.TestKit;

/// <summary>An entry appended by the built-in <c>xstate.log</c> action effect.</summary>
public sealed record LogEntry(string? Label, object? Message);

/// <summary>
/// A logical timer waiting on the interpreter's virtual clock. The host owns only the clock: when
/// the timer is due it delivers <see cref="XState.TimerEvent"/> with <see cref="Id"/> back to the
/// actor that scheduled it, and the machine decides what firing means.
/// </summary>
public sealed record ScheduledSend(
    TimeSpan Time,
    long Seq,
    string SenderId,
    string? Target,
    MachineEvent Event,
    string? Id);

/// <summary>An event a machine rejected at its boundary (<see cref="DeadLetterEffect"/>).</summary>
public sealed record DeadLetter(string ActorId, MachineEvent Event, string Reason, Exception? Error);

/// <summary>Read-only view of a running (or stopped) actor.</summary>
/// <param name="Address">
/// The actor's deterministic address (<see cref="XState.ActorAddress"/>): the <c>/</c>-joined path
/// of encoded actor ids from the root down. This — never <paramref name="SessionId"/> — is the
/// actor's durable identity.
/// </param>
public sealed record ActorView(
    string Id,
    string SessionId,
    string Address,
    string? ParentId,
    ISnapshot? Snapshot,
    bool IsAlive);

/// <summary>
/// A fully deterministic, single-threaded, virtual-time interpreter for
/// <see cref="IMachineLogic"/>. XState.NET ships no runtime; this exists to drive machines
/// in tests (and in the W3C SCXML conformance harness).
/// <para>
/// No <c>Task</c>, no threads, no wall-clock timers: delayed sends land in a scheduled set
/// keyed by (virtual time, monotonic sequence) and are delivered by
/// <see cref="AdvanceTime"/> / <see cref="RunToCompletion"/>.
/// </para>
/// </summary>
public sealed class DeterministicInterpreter
{
    /// <summary>Runaway protection: maximum number of events delivered to actors.</summary>
    public const int MaxProcessedEvents = 100_000;

    private sealed class Actor
    {
        public required string Id { get; init; }

        /// <summary>
        /// Per-process handle. Fresh on every run, and fresh again for a respawn under the same
        /// id — deliberately <em>not</em> a durable identity; see <see cref="Address"/>.
        /// </summary>
        public required string SessionId { get; init; }

        /// <summary>
        /// The actor's deterministic address: root = the root machine's id (or <c>x:0</c> when it
        /// is anonymous), each child = <c>parent.Address + "/" + encoded child id</c>. Reproduced
        /// exactly by a replay, and therefore the actor's durable identity.
        /// </summary>
        public required string Address { get; init; }
        /// <summary>
        /// The incarnation token the parent minted when it spawned this child. Echoed back as the
        /// <c>SessionId</c> of the done/error event relayed to the parent, so the parent can tell
        /// a report from this child apart from one by an earlier child of the same id.
        /// </summary>
        public string? Incarnation { get; init; }

        public required IActorLogic Logic { get; init; }
        public Actor? Parent { get; init; }

        /// <summary>False until the matching <see cref="StartEffect"/> arrives.</summary>
        public bool Started { get; set; }

        /// <summary>Input captured at spawn, consumed by the deferred start.</summary>
        public object? Input { get; init; }

        /// <summary>
        /// Whether the parent asked to observe this child's snapshots
        /// (<see cref="SpawnEffect.SyncSnapshot"/> / xstate v6 <c>syncSnapshot</c>).
        /// </summary>
        public bool SyncSnapshot { get; init; }
        public ISnapshot? Snapshot { get; set; }
        public bool Alive { get; set; } = true;

        /// <summary>External FIFO mailbox.</summary>
        public Queue<MachineEvent> Mailbox { get; } = new();

        /// <summary>Front-of-queue events produced by <see cref="PostTransitionEvents"/>.</summary>
        public Queue<MachineEvent> Internal { get; } = new();

        /// <summary>Children by spawn/invoke id, in spawn order.</summary>
        public List<Actor> Children { get; } = [];

        public bool HasPending => Started && (Internal.Count > 0 || Mailbox.Count > 0);
    }

    private readonly IMachineLogic _rootLogic;
    private readonly object? _input;
    private readonly List<Actor> _actors = [];
    private readonly List<ScheduledSend> _scheduled = [];
    private readonly Dictionary<long, Actor> _senders = [];
    private readonly List<LogEntry> _log = [];
    private readonly List<string> _diagnostics = [];
    private readonly List<DeadLetter> _deadLetters = [];
    private readonly List<MachineEvent> _emitted = [];

    private Actor? _root;
    private int _sessionCounter;
    private long _seqCounter;
    private int _processedEvents;

    public DeterministicInterpreter(IMachineLogic rootLogic, object? input = null)
    {
        _rootLogic = rootLogic;
        _input = input;
    }

    // --- Hooks (set before Start) ---

    /// <summary>
    /// Adapts a non-machine <see cref="IActorLogic"/> spawn into machine logic the interpreter
    /// can run. Return null to record the spawn as unsupported.
    /// </summary>
    public Func<SpawnEffect, IActorLogic?>? SpawnAdapter { get; set; }

    /// <summary>
    /// Called after every actor step (including the initial step). Returned events are pushed
    /// onto that actor's internal front-of-queue and delivered before any mailbox events.
    /// </summary>
    public Func<ISnapshot, IEnumerable<MachineEvent>>? PostTransitionEvents { get; set; }

    /// <summary>
    /// An actor's own protocol-level address, read from its snapshot — SCXML's <c>_sessionid</c>,
    /// which the machine mints itself and which a <c>&lt;send&gt;</c> may use as a target. Treated
    /// as an additional alias in target resolution.
    /// <para>
    /// Unrelated to <see cref="ActorView.Address"/> / <see cref="XState.ActorAddress"/>: this is a
    /// protocol alias the datamodel owns, not the actor's structural, durable address.
    /// </para>
    /// </summary>
    public Func<ISnapshot, string?>? ActorAddress { get; set; }

    /// <summary>
    /// Builds the event delivered back to the <em>sender</em> when a send cannot be delivered
    /// (unknown or stopped target) — SCXML's <c>error.communication</c>. Return null to drop the
    /// send silently (the default when the hook is unset).
    /// </summary>
    public Func<SendToEffect, MachineEvent?>? UndeliverableEvent { get; set; }

    /// <summary>
    /// Rewrites an event on its way from a child actor to its parent, given the child's id — SCXML
    /// stamps such events with the originating <c>invokeid</c>.
    /// </summary>
    public Func<MachineEvent, string, MachineEvent>? DecorateChildEvent { get; set; }

    // --- Public surface ---

    public TimeSpan VirtualTime { get; private set; }

    public IReadOnlyList<LogEntry> Log => _log;

    /// <summary>Non-fatal problems (unresolvable send targets, unsupported spawns, …).</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>Events machines rejected at their boundary, in rejection order.</summary>
    public IReadOnlyList<DeadLetter> DeadLetters => _deadLetters;

    /// <summary>Events emitted by machines (<see cref="EmitEffect"/>), in emission order.</summary>
    public IReadOnlyList<MachineEvent> Emitted => _emitted;

    /// <summary>Delayed sends still waiting, in (time, seq) order.</summary>
    public IReadOnlyList<ScheduledSend> PendingSends =>
        [.. _scheduled.OrderBy(s => s.Time).ThenBy(s => s.Seq)];

    public IReadOnlyList<ActorView> Actors =>
        [.. _actors.Select(a => new ActorView(a.Id, a.SessionId, a.Address, a.Parent?.Id, a.Snapshot, a.Alive))];

    public ISnapshot RootSnapshot =>
        Require(_root).Snapshot ?? throw new InvalidOperationException("Interpreter has not started.");

    /// <summary>Total events delivered to actors so far.</summary>
    public int ProcessedEvents => _processedEvents;

    /// <summary>Starts the root actor (runs its initial step + effects), then pumps to quiescence.</summary>
    public DeterministicInterpreter Start()
    {
        if (_root is not null)
        {
            throw new InvalidOperationException("Interpreter already started.");
        }

        _root = CreateActor(_rootLogic, _rootLogic.Id, parent: null, incarnation: null, input: _input);
        StartActor(_root);
        RunUntilQuiescent();
        return this;
    }

    public static DeterministicInterpreter Start(IMachineLogic logic, object? input = null) =>
        new DeterministicInterpreter(logic, input).Start();

    /// <summary>Enqueues an event into the root mailbox without pumping.</summary>
    public DeterministicInterpreter Enqueue(MachineEvent evt)
    {
        Require(_root).Mailbox.Enqueue(evt);
        return this;
    }

    /// <summary>Enqueues an event into the root mailbox and pumps until quiescent.</summary>
    public DeterministicInterpreter Send(MachineEvent evt)
    {
        Enqueue(evt);
        RunUntilQuiescent();
        return this;
    }

    /// <summary>Sends an event directly to a named actor (by id, then session id).</summary>
    public DeterministicInterpreter SendTo(string actorId, MachineEvent evt)
    {
        var target = ResolveTarget(Require(_root), actorId);
        if (target is null)
        {
            _diagnostics.Add($"Unresolvable send target '{actorId}'.");
            return this;
        }

        target.Mailbox.Enqueue(evt);
        RunUntilQuiescent();
        return this;
    }

    public ISnapshot? SnapshotOf(string actorId) =>
        _actors.LastOrDefault(a => a.Id == actorId)?.Snapshot;

    public bool IsRunning(string actorId) =>
        _actors.Any(a => a.Id == actorId && a.Alive);

    /// <summary>
    /// Delivers pending events until every mailbox is empty. Actors are served in creation
    /// order (root first, then children in spawn order), one event at a time.
    /// </summary>
    public void RunUntilQuiescent()
    {
        while (true)
        {
            Actor? next = null;
            foreach (var actor in _actors)
            {
                if (actor.Alive && actor.HasPending)
                {
                    next = actor;
                    break;
                }
            }

            if (next is null)
            {
                return;
            }

            Step(next);
        }
    }

    /// <summary>
    /// Moves virtual time forward by <paramref name="delta"/>, delivering scheduled sends in
    /// (time, seq) order and pumping to quiescence between deliveries.
    /// </summary>
    public void AdvanceTime(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "Virtual time is monotonic.");
        }

        var target = VirtualTime + delta;
        RunUntilQuiescent();

        while (true)
        {
            var due = _scheduled
                .Where(s => s.Time <= target)
                .OrderBy(s => s.Time)
                .ThenBy(s => s.Seq)
                .FirstOrDefault();

            if (due is null)
            {
                break;
            }

            _scheduled.Remove(due);
            if (due.Time > VirtualTime)
            {
                VirtualTime = due.Time;
            }

            Deliver(due);
            RunUntilQuiescent();
        }

        VirtualTime = target;
    }

    /// <summary>
    /// Pumps, then keeps advancing to the next scheduled send until the root is no longer
    /// active, no work remains, or <paramref name="maxVirtualTime"/> would be exceeded.
    /// </summary>
    public void RunToCompletion(TimeSpan? maxVirtualTime = null)
    {
        RunUntilQuiescent();

        while (_root is { Alive: true } root && root.Snapshot?.Status is SnapshotStatus.Active)
        {
            var next = _scheduled.OrderBy(s => s.Time).ThenBy(s => s.Seq).FirstOrDefault();
            if (next is null)
            {
                return;
            }

            if (maxVirtualTime is { } max && next.Time > max)
            {
                if (max > VirtualTime)
                {
                    VirtualTime = max;
                }

                return;
            }

            AdvanceTime(next.Time - VirtualTime);
        }
    }

    // --- Actor lifecycle ---

    private Actor CreateActor(
        IActorLogic logic,
        string id,
        Actor? parent,
        string? incarnation,
        object? input,
        bool syncSnapshot = false)
    {
        var actor = new Actor
        {
            SyncSnapshot = syncSnapshot,
            Id = id,
            SessionId = $"x:{_sessionCounter++}",
            Address = parent is null
                ? global::XState.ActorAddress.Root((logic as IMachineLogic)?.Id)
                : global::XState.ActorAddress.Child(parent.Address, id),
            Incarnation = incarnation,
            Logic = logic,
            Parent = parent,
            Input = input
        };

        _actors.Add(actor);
        parent?.Children.Add(actor);
        return actor;
    }

    /// <summary>
    /// Runs an actor's initial transition. A child is created by its parent's
    /// <see cref="SpawnEffect"/> but only started by the matching <see cref="StartEffect"/>, which
    /// the parent emits once its macrostep settles — so a child never observes its parent
    /// mid-macrostep.
    /// </summary>
    private void StartActor(Actor actor)
    {
        if (actor.Started || !actor.Alive)
        {
            return;
        }

        actor.Started = true;

        try
        {
            var (snapshot, effects) = actor.Logic.GetInitialSnapshot(actor.Input);
            actor.Snapshot = snapshot;
            ExecuteEffects(actor, effects);
            PumpPostTransitionEvents(actor);
            RelaySnapshot(actor);
        }
        catch (Exception ex) when (actor.Parent is not null)
        {
            Fail(actor, ex);
            return;
        }

        CheckCompletion(actor);
    }

    private void Step(Actor actor)
    {
        var evt = actor.Internal.Count > 0 ? actor.Internal.Dequeue() : actor.Mailbox.Dequeue();

        if (++_processedEvents > MaxProcessedEvents)
        {
            throw new InvalidOperationException(
                $"Runaway interpretation: more than {MaxProcessedEvents} events were processed. " +
                "This usually means two actors are echoing events, or a machine re-sends an " +
                "event it also handles.");
        }

        try
        {
            var (snapshot, effects) = actor.Logic.Transition(actor.Snapshot!, evt);
            actor.Snapshot = snapshot;
            ExecuteEffects(actor, effects);
            PumpPostTransitionEvents(actor);
            RelaySnapshot(actor);
        }
        catch (Exception ex) when (actor.Parent is not null)
        {
            Fail(actor, ex);
            return;
        }

        CheckCompletion(actor);
    }

    /// <summary>
    /// Relays a child's snapshot to its parent when the parent asked to observe it
    /// (xstate v6 <c>createActor</c>'s <c>_syncSnapshot</c> subscription): after the child's
    /// initial step and after every event it processes, but only while it is still active —
    /// finishing or failing is reported through done/error instead.
    /// </summary>
    private void RelaySnapshot(Actor actor)
    {
        if (!actor.SyncSnapshot || !actor.Alive || actor.Parent is not { Alive: true } parent ||
            actor.Snapshot is not { Status: SnapshotStatus.Active } snapshot)
        {
            return;
        }

        parent.Mailbox.Enqueue(new SnapshotEvent(actor.Id, snapshot, actor.Incarnation));
    }

    private void PumpPostTransitionEvents(Actor actor)
    {
        if (PostTransitionEvents is null || actor.Snapshot is null || !actor.Alive)
        {
            return;
        }

        foreach (var evt in PostTransitionEvents(actor.Snapshot))
        {
            actor.Internal.Enqueue(evt);
        }
    }

    /// <summary>
    /// Fallback for logic that reaches a terminal snapshot without emitting a
    /// <see cref="TerminateEffect"/> (a hand-written <see cref="IActorLogic"/> adapter). A machine
    /// emits one, which already retired the actor, so this sees a dead actor and does nothing.
    /// </summary>
    private void CheckCompletion(Actor actor)
    {
        if (!actor.Alive || actor.Snapshot is null)
        {
            return;
        }

        switch (actor.Snapshot.Status)
        {
            case SnapshotStatus.Done:
                Terminate(actor, new TerminateEffect(SnapshotStatus.Done, actor.Snapshot.Output, null));
                break;

            case SnapshotStatus.Error:
                Terminate(actor, new TerminateEffect(SnapshotStatus.Error, null, actor.Snapshot.Error));
                break;

            case SnapshotStatus.Stopped:
                Retire(actor);
                break;
        }
    }

    /// <summary>
    /// The actor finished: report the outcome to its parent (stamped with the incarnation token
    /// the parent minted for it) and retire it.
    /// </summary>
    private void Terminate(Actor actor, TerminateEffect effect)
    {
        if (!actor.Alive)
        {
            return;
        }

        var completion = effect.Status is SnapshotStatus.Done
            ? new DoneActorEvent(actor.Id, effect.Output, actor.Incarnation)
            : (MachineEvent)new ErrorActorEvent(
                actor.Id,
                effect.Error ?? new InvalidOperationException($"Actor '{actor.Id}' errored."),
                actor.Incarnation);

        Retire(actor);
        Notify(actor, completion);
    }

    private void Fail(Actor actor, Exception ex)
    {
        Retire(actor);
        Notify(actor, new ErrorActorEvent(actor.Id, ex, actor.Incarnation));
    }

    private void Notify(Actor actor, MachineEvent evt)
    {
        if (actor.Parent is { Alive: true } parent)
        {
            parent.Mailbox.Enqueue(evt);
        }
    }

    /// <summary>Marks an actor finished: stops descendants, drops its timers and mail.</summary>
    private void Retire(Actor actor)
    {
        actor.Alive = false;
        actor.Mailbox.Clear();
        actor.Internal.Clear();
        _scheduled.RemoveAll(s => _senders.TryGetValue(s.Seq, out var sender) && ReferenceEquals(sender, actor));

        foreach (var child in actor.Children.ToList())
        {
            if (child.Alive)
            {
                Retire(child);
            }
        }
    }

    /// <summary>
    /// <see cref="StopChildEffect"/>: the child is stopped the way any actor is — it processes
    /// <see cref="StopEvent"/> itself, so its own children and timers are torn down through its
    /// effects — and is then retired.
    /// </summary>
    private void StopChild(Actor parent, string childId)
    {
        var child = parent.Children.LastOrDefault(c => c.Id == childId && c.Alive);
        if (child is null)
        {
            return;
        }

        if (child.Started && child.Snapshot is { Status: SnapshotStatus.Active })
        {
            try
            {
                var (snapshot, effects) = child.Logic.Transition(child.Snapshot, new StopEvent());
                child.Snapshot = snapshot;
                ExecuteEffects(child, effects);
            }
            catch (Exception ex)
            {
                _diagnostics.Add($"Actor '{child.Id}' threw while stopping: {ex.Message}");
            }
        }

        Retire(child);
        parent.Children.Remove(child);
    }

    // --- Effects ---

    private void ExecuteEffects(Actor actor, IReadOnlyList<Effect> effects)
    {
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case ActionEffect action:
                    if (action is { Type: "xstate.log", Params: LogParams log })
                    {
                        _log.Add(new LogEntry(log.Label, log.Message));
                    }

                    action.Exec();
                    break;

                case EmitEffect emit:
                    _emitted.Add(emit.Event);
                    break;

                case DeadLetterEffect dead:
                    _deadLetters.Add(new DeadLetter(actor.Id, dead.Event, dead.Reason, dead.Error));
                    _diagnostics.Add(
                        $"Actor '{actor.Id}' dead-lettered '{dead.Event.Type}' ({dead.Reason}).");
                    break;

                case RaiseEffect raise:
                    Schedule(actor, raise.Id, raise.Delay, target: null, raise.Event);
                    break;

                case SendToEffect { Delay: not null } send:
                    Schedule(actor, send.Id, send.Delay.Value, send.Target, send.Event);
                    break;

                case SendToEffect send:
                    DeliverNow(actor, send);
                    break;

                case CancelEffect cancel:
                    Cancel(actor, cancel.SendId);
                    break;

                case SpawnEffect spawn:
                    Spawn(actor, spawn);
                    break;

                case StartEffect start:
                    Start(actor, start.Id);
                    break;

                case StopChildEffect stop:
                    StopChild(actor, stop.ChildId);
                    break;

                case TerminateEffect terminate:
                    Terminate(actor, terminate);
                    break;
            }
        }
    }

    private void DeliverNow(Actor sender, SendToEffect send)
    {
        var target = ResolveTarget(sender, send.Target);

        if (target is null)
        {
            _diagnostics.Add(
                $"Actor '{sender.Id}' sent '{send.Event.Type}' to unresolvable target '{send.Target}'.");
            Undeliverable(sender, send);
            return;
        }

        if (!target.Alive)
        {
            _diagnostics.Add(
                $"Actor '{sender.Id}' sent '{send.Event.Type}' to stopped actor '{target.Id}'.");
            Undeliverable(sender, send);
            return;
        }

        target.Mailbox.Enqueue(
            DecorateChildEvent is not null && ReferenceEquals(target, sender.Parent)
                ? DecorateChildEvent(send.Event, sender.Id)
                : send.Event);
    }

    private void Undeliverable(Actor sender, SendToEffect send)
    {
        if (!sender.Alive || UndeliverableEvent?.Invoke(send) is not { } evt)
        {
            return;
        }

        sender.Internal.Enqueue(evt);
    }

    /// <summary>
    /// Parks a logical timer on the virtual clock. The host tracks only (owner, id, due time):
    /// when it comes due it delivers <see cref="TimerEvent"/> back to the owner, which looks the
    /// id up in its own ledger.
    /// </summary>
    private void Schedule(Actor sender, string? id, TimeSpan delay, string? target, MachineEvent evt)
    {
        var seq = _seqCounter++;
        _senders[seq] = sender;
        _scheduled.Add(new ScheduledSend(VirtualTime + delay, seq, sender.Id, target, evt, id));
    }

    private void Cancel(Actor sender, string sendId)
    {
        _scheduled.RemoveAll(s =>
            s.Id == sendId && _senders.TryGetValue(s.Seq, out var owner) && ReferenceEquals(owner, sender));
    }

    private void Deliver(ScheduledSend scheduled)
    {
        if (!_senders.TryGetValue(scheduled.Seq, out var sender) || !sender.Alive || scheduled.Id is null)
        {
            return;
        }

        // The host does not decide what a fired timer means — it only reports that the id
        // elapsed. The machine strikes it off its ledger and turns it into a raise or a send.
        sender.Mailbox.Enqueue(new TimerEvent(scheduled.Id));
    }

    private void Spawn(Actor parent, SpawnEffect spawn)
    {
        // Any IActorLogic can be run: the contract is the pure (snapshot, effects) pair. The
        // adapter is only for logic that wants to be rewritten before it runs.
        var logic = spawn.Logic is IMachineLogic machine
            ? machine
            : SpawnAdapter?.Invoke(spawn) ?? spawn.Logic;

        CreateActor(logic, spawn.Id, parent, spawn.Incarnation, spawn.Input, spawn.SyncSnapshot);
    }

    private void Start(Actor parent, string childId)
    {
        var child = parent.Children.LastOrDefault(c => c.Id == childId && c.Alive && !c.Started);
        if (child is null)
        {
            return;
        }

        StartActor(child);
    }

    // --- Target resolution ---

    private Actor? ResolveTarget(Actor sender, string? target)
    {
        if (target is null or EffectTarget.Self or "_self")
        {
            return sender;
        }

        if (target is EffectTarget.Parent or "_parent")
        {
            return sender.Parent;
        }

        var child = sender.Children.LastOrDefault(c => c.Id == target && c.Alive);
        if (child is not null)
        {
            return child;
        }

        var byId = _actors.LastOrDefault(a => a.Alive && a.Id == target)
                   ?? _actors.LastOrDefault(a => a.Id == target);
        if (byId is not null)
        {
            return byId;
        }

        return _actors.LastOrDefault(a => a.SessionId == target)
               ?? _actors.LastOrDefault(a => AddressOf(a) == target);
    }

    private string? AddressOf(Actor actor) =>
        actor.Snapshot is null || ActorAddress is null ? null : ActorAddress(actor.Snapshot);

    private static Actor Require(Actor? actor) =>
        actor ?? throw new InvalidOperationException(
            "Interpreter has not started — call Start() before sending events.");
}
