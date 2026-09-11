using System.Collections.Immutable;
using System.Globalization;

namespace XState;

/// <summary>
/// The pure interpretation core — a port of xstate v6 <c>stateUtils.ts</c>
/// (<c>microstep</c> / <c>macrostep</c>, SCXML §microstepProcedure).
/// </summary>
public sealed partial class StateMachine<TContext>
{
    // --- Mutable working state for one macrostep ---

    private sealed class Run
    {
        public HashSet<StateNode<TContext>> Nodes { get; } = [];
        public TContext Context { get; set; } = default!;

        public ImmutableDictionary<string, ImmutableArray<string>> History { get; set; } =
            ImmutableDictionary<string, ImmutableArray<string>>.Empty;

        public List<Effect> Effects { get; } = [];
        public List<MachineEvent> InternalQueue { get; } = [];
        public SnapshotStatus Status { get; set; } = SnapshotStatus.Active;
        public object? Output { get; set; }
        public Exception? Error { get; set; }

        /// <summary>
        /// Next generated actor index per id prefix (xstate v6 <c>_nextActorIds</c>); carried
        /// across macrosteps on the snapshot. Ordinal keys: an id prefix is an opaque string.
        /// </summary>
        public Dictionary<string, int> NextActorIds { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Next generated delayed send/raise index (xstate v6 <c>_nextTimerId</c>); carried across
        /// macrosteps on the snapshot.
        /// </summary>
        public int NextTimerId { get; set; }

        /// <summary>
        /// Next child incarnation token (xstate v6 <c>matchesActorSession</c>): stamped on every
        /// spawn and echoed by the host on the child's done/error event.
        /// </summary>
        public int NextIncarnation { get; set; }

        /// <summary>Children believed to be running (invoked + spawned), in spawn order.</summary>
        public List<ChildRecord> Children { get; } = [];

        /// <summary>Logical timers believed to be outstanding, in schedule order.</summary>
        public List<LogicalTimer> Timers { get; } = [];

        /// <summary>
        /// Per-state input (xstate v6 <c>_stateInputs</c>), keyed by state id. Written when a
        /// transition carrying <c>input</c> is taken, once per target; never pruned, so a state's
        /// own exit actions can still read the input it was entered with.
        /// </summary>
        public Dictionary<string, object?> StateInputs { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Ids of the children spawned during this macrostep, in spawn order. Their
        /// <see cref="StartEffect"/>s are appended once the macrostep settles (xstate v6
        /// <c>deriveDeferredStarts</c>), so a child never starts mid-macrostep.
        /// </summary>
        public List<string> DeferredStarts { get; } = [];

        /// <summary>
        /// SCXML <c>statesToInvoke</c>: states entered during this macrostep whose invocations are
        /// still to be started, with the event that entered them. Only used when
        /// <see cref="StateMachine{TContext}.DeferInvokes"/> is set.
        /// </summary>
        public List<(StateNode<TContext> Node, MachineEvent Event)> PendingInvokes { get; } = [];
    }

    // --- Public entry points ---

    public partial TransitionResult<TContext> GetInitialState(object? input)
    {
        var run = new Run { Context = ContextFactory(input) };
        var initEvent = new InitEvent(input);

        // Target the root and let AddDescendantStatesToEnter walk the initial-transition
        // chain: compounds entered by default get marked for default entry, while explicit
        // deep initial targets skip intermediate defaults (SCXML initial semantics).
        var initialTransition = new TransitionDefinition<TContext>
        {
            Source = Root,
            Targets = [Root],
            Reenter = true // the root itself is entered
        };

        Microstep(run, [initialTransition], initEvent, isInitial: true);
        Settle(run, initEvent);

        return Complete(run);
    }

    public partial TransitionResult<TContext> Transition(State<TContext> state, MachineEvent evt)
    {
        // --- Boundary (xstate v6 `StateMachine.transition`) ---
        //
        // A rejected event is never delivered: the snapshot comes back untouched and the
        // rejection is reported as a dead letter, so a host can log/route it. Events in the
        // `xstate.`/`@xstate.` namespace are the host's own protocol (`xstate.timer`,
        // `xstate.done.actor`, …) and bypass the user validator.
        if (state.Status is not SnapshotStatus.Active)
        {
            return new TransitionResult<TContext>(state, [new DeadLetterEffect(evt, "stopped")]);
        }

        // An event type the machine declares as internal is its own to raise: one arriving from
        // outside is refused at the boundary (xstate v6 `internalEvents`, #5676). Raised events
        // never pass through here, so the same type stays deliverable from inside.
        if (InternalEvents.Contains(evt.Type))
        {
            return new TransitionResult<TContext>(state, [new DeadLetterEffect(evt, "internalEvent")]);
        }

        if (!IsInternalEventType(evt.Type) && EventValidator?.Invoke(evt) is { } rejection)
        {
            return new TransitionResult<TContext>(state, [new DeadLetterEffect(evt, "invalidEvent", rejection)]);
        }

        var run = NewRun(state);

        // @xstate.stop: stop the children, cancel the timers, done. No state is exited, so no
        // exit action runs (stateUtils.ts `macrostep`, the XSTATE_STOP branch).
        if (evt is StopEvent)
        {
            EmitTeardown(run);
            run.Status = SnapshotStatus.Stopped;
            return Complete(run);
        }

        // A completion from an earlier incarnation of a still-live child id is not this child's
        // (stateUtils.ts `matchesActorSession`): drop it whole, snapshot unchanged.
        if (IsStaleActorEvent(run, evt))
        {
            return new TransitionResult<TContext>(state, []);
        }

        if (evt is TimerEvent timer)
        {
            return FireTimer(run, state, timer);
        }

        // A child that reported done/error is no longer running — keep the ledger honest before
        // any transition can spawn a replacement under the same id.
        switch (evt)
        {
            case DoneActorEvent done:
                run.Children.RemoveAll(c => c.Id == done.ActorId);
                CancelActorTimeout(run, done.ActorId);
                break;
            case ErrorActorEvent errored:
                run.Children.RemoveAll(c => c.Id == errored.ActorId);
                CancelActorTimeout(run, errored.ActorId);
                break;
        }

        ApplyInvokeEventHooks(run, evt);

        var transitions = SelectTransitions(run, evt);

        // stateUtils.ts `macrostep` (`isErrorEvent`): an `xstate.error.*` event nobody handles
        // fails the actor rather than being silently swallowed. Every other unhandled event is
        // discarded. The failed snapshot is the *incoming* one with a terminal status — the
        // error path exits no states and releases nothing; only the termination effect is
        // emitted, and the host tears the actor down.
        if (transitions.Count == 0 && !IgnoreUnhandledErrors && IsErrorEvent(evt))
        {
            var error = ErrorOf(evt);
            var errored = state with { Status = SnapshotStatus.Error, Error = error };

            return new TransitionResult<TContext>(
                errored,
                [new TerminateEffect(SnapshotStatus.Error, null, error)]);
        }

        Microstep(run, transitions, evt, isInitial: false);
        Settle(run, evt);

        return Complete(run);
    }

    /// <summary>
    /// <c>xstate.timer</c> — the host reports that the timer with this id elapsed. An id that is
    /// no longer on the ledger is a stale firing (the timer was cancelled, or its state exited)
    /// and changes nothing. Otherwise the timer is struck off and its event delivered: a
    /// <see cref="TimerKind.Raise"/> onto the internal queue, a <see cref="TimerKind.SendTo"/> as
    /// an immediate send (stateUtils.ts `macrostep`, the XSTATE_TIMER branch).
    /// </summary>
    private TransitionResult<TContext> FireTimer(Run run, State<TContext> state, TimerEvent evt)
    {
        var timer = run.Timers.FirstOrDefault(t => t.Id == evt.Id);
        if (timer is null)
        {
            return new TransitionResult<TContext>(state, []);
        }

        run.Timers.Remove(timer);

        if (timer.Kind is TimerKind.Raise)
        {
            run.InternalQueue.Add(timer.Event);
        }
        else
        {
            Emit(run, new SendToEffect(timer.Target ?? EffectTarget.Self, timer.Event));
        }

        Settle(run, evt);

        return Complete(run);
    }

    /// <summary>
    /// Whether a done/error event names a live child but carries a different incarnation token —
    /// a report from an earlier child of the same id that the host relayed late. A null token
    /// means the host does not track incarnations and the event is accepted (xstate v6
    /// <c>matchesActorSession</c>).
    /// </summary>
    private static bool IsStaleActorEvent(Run run, MachineEvent evt)
    {
        var (actorId, sessionId) = evt switch
        {
            DoneActorEvent done => (done.ActorId, done.SessionId),
            ErrorActorEvent errored => (errored.ActorId, errored.SessionId),
            ActorTimeoutEvent timedOut => (timedOut.ActorId, timedOut.SessionId),
            SnapshotEvent snapshot => (snapshot.ActorId, snapshot.SessionId),
            _ => (null, null)
        };

        return sessionId is not null &&
               run.Children.FirstOrDefault(c => c.Id == actorId) is { } child &&
               child.Incarnation != sessionId;
    }

    /// <summary>utils.ts <c>isErrorEvent</c>.</summary>
    private static bool IsErrorEvent(MachineEvent evt) =>
        evt.Type.StartsWith("xstate.error.", StringComparison.Ordinal);

    private static Exception ErrorOf(MachineEvent evt) => evt switch
    {
        ErrorActorEvent e => e.Error,
        ErrorPlatformEvent { Error: Exception e } => e,
        ErrorPlatformEvent e => new InvalidOperationException(
            e.Error?.ToString() ?? $"Unhandled error event '{e.Type}'."),
        _ => new InvalidOperationException($"Unhandled error event '{evt.Type}'.")
    };

    /// <summary>
    /// Whether a type belongs to the engine/host protocol rather than to the user's event set.
    /// Such events are delivered by the host itself and are never rejected by a user validator.
    /// </summary>
    private static bool IsInternalEventType(string type) =>
        type.StartsWith("xstate.", StringComparison.Ordinal) ||
        type.StartsWith("@xstate.", StringComparison.Ordinal);

    /// <summary>
    /// Closes a macrostep: the deferred <see cref="StartEffect"/>s for everything spawned during
    /// it, then a single <see cref="TerminateEffect"/> if it ended terminal (stateUtils.ts
    /// <c>completeMacrostep</c>).
    /// </summary>
    private TransitionResult<TContext> Complete(Run run)
    {
        foreach (var id in run.DeferredStarts)
        {
            run.Effects.Add(new StartEffect(id));
        }

        if (run.Status is SnapshotStatus.Done or SnapshotStatus.Error)
        {
            run.Effects.Add(new TerminateEffect(run.Status, run.Output, run.Error));
        }

        return ToResult(run);
    }

    private Run NewRun(State<TContext> state)
    {
        var run = new Run
        {
            Context = state.Context,
            History = state.HistoryValue,
            Status = state.Status,
            Output = state.Output,
            Error = state.Error,
            NextTimerId = state.NextTimerId,
            NextIncarnation = state.NextIncarnation
        };

        foreach (var (prefix, next) in state.NextActorIds)
        {
            run.NextActorIds[prefix] = next;
        }

        run.Children.AddRange(state.Children);

        foreach (var (stateId, input) in state.StateInputs)
        {
            run.StateInputs[stateId] = input;
        }

        // `Timers` is keyed by id, so the persisted ledger has no order of its own; restoring it
        // in id order keeps teardown (which cancels every outstanding timer) reproducible.
        run.Timers.AddRange(state.Timers.Values.OrderBy(t => t.Id, StringComparer.Ordinal));

        // Defensive restore floor. The counters travel on the same snapshot as `Children`, so a
        // snapshot this machine produced already reserves every generated-shaped child id — but a
        // hand-constructed or hand-edited snapshot may not, and a counter behind a live child
        // would hand that child's id out twice. O(children), once per macrostep.
        foreach (var child in state.Children)
        {
            ReserveActorId(run, child.Id);
        }

        foreach (var node in NodesOf(state.Configuration))
        {
            run.Nodes.Add(node);
        }

        return run;
    }

    private TransitionResult<TContext> ToResult(Run run) =>
        new(
            new State<TContext>
            {
                // Document order: the configuration must be deterministic, not hash-ordered.
                Configuration = [.. run.Nodes.OrderBy(n => n.Order).Select(n => n.Id)],
                Context = run.Context,
                Status = run.Status,
                Output = run.Output,
                Error = run.Error,
                HistoryValue = run.History,
                Machine = this,
                NextActorIds = run.NextActorIds.ToImmutableDictionary(StringComparer.Ordinal),
                NextTimerId = run.NextTimerId,
                NextIncarnation = run.NextIncarnation,
                Children = [.. run.Children],
                Timers = run.Timers.ToImmutableDictionary(t => t.Id, t => t, StringComparer.Ordinal),
                StateInputs = run.StateInputs.ToImmutableDictionary(StringComparer.Ordinal)
            },
            [.. run.Effects]);

    // --- Macrostep loop (stateUtils.ts `macrostep`) ---

    private void Settle(Run run, MachineEvent evt)
    {
        var nextEvent = evt;
        var shouldSelectEventless = true;
        var iterations = 0;

        while (run.Status is SnapshotStatus.Active)
        {
            if (++iterations > MaxMicrosteps)
            {
                throw new InvalidOperationException(
                    $"Infinite loop detected in machine '{Id}': more than {MaxMicrosteps} microsteps were " +
                    "processed without reaching a stable state. This usually means a cycle of eventless " +
                    "transitions or raised events (e.g. a -> b -> c -> a).");
            }

            var enabled = shouldSelectEventless ? SelectEventlessTransitions(run, nextEvent) : [];
            var hadEventless = enabled.Count > 0;

            var before = Fingerprint(run);

            if (enabled.Count == 0)
            {
                if (run.InternalQueue.Count == 0)
                {
                    break;
                }

                nextEvent = run.InternalQueue[0];
                run.InternalQueue.RemoveAt(0);
                enabled = SelectTransitions(run, nextEvent);
            }

            Microstep(run, enabled, nextEvent, isInitial: false);

            // Eventless transitions are only re-selected if the microstep changed something.
            shouldSelectEventless = !hadEventless || Changed(before, run);
        }

        // SCXML mainEventLoop: the macrostep is stable, so start the invocations of every state
        // entered (and not since exited) during it. A machine that finished mid-macrostep has
        // already torn its children down, so it starts nothing.
        if (DeferInvokes && run.Status is SnapshotStatus.Active)
        {
            foreach (var (node, enteredWith) in run.PendingInvokes.OrderBy(p => p.Node.Order).ToList())
            {
                StartInvocations(run, node, enteredWith);
            }
        }

        run.PendingInvokes.Clear();
    }

    /// <summary>Emits the <see cref="SpawnEffect"/>s for one node's <c>invoke</c>s, in document order.</summary>
    private static void StartInvocations(Run run, StateNode<TContext> node, MachineEvent evt)
    {
        foreach (var invoke in node.Invokes)
        {
            // The invoke's input resolver reads the *state* input of the state it belongs to and
            // returns the *actor* input (stateUtils.ts:1904-1913).
            var args = ArgsFor(run, evt, null, node.Id);
            object? input;
            try
            {
                input = invoke.InputResolver?.Invoke(args);
            }
            catch (SpawnAbortedException)
            {
                // The resolver refused the invocation: no child, no timeout (SCXML §6.4).
                continue;
            }

            var spawned = (SpawnEffect)Emit(
                run,
                new SpawnEffect(
                    invoke.Id,
                    invoke.Logic,
                    input,
                    invoke.Src,
                    Incarnation: "",
                    SyncSnapshot: invoke.OnSnapshot.Count > 0));

            if (invoke.Timeout is null)
            {
                continue;
            }

            // The timeout event is stamped with the incarnation the spawn just minted, so a timer
            // left over from a previous child under this id is dropped when it fires
            // (stateUtils.ts:514-522, `matchesActorSession`).
            Emit(run, new RaiseEffect(
                new ActorTimeoutEvent(invoke.Id, spawned.Incarnation),
                invoke.TimeoutSendId,
                invoke.Timeout.Resolve(args)));
        }
    }

    private static (HashSet<StateNode<TContext>> Nodes, TContext Context, object History) Fingerprint(Run run) =>
        ([.. run.Nodes], run.Context, run.History);

    private static bool Changed(
        (HashSet<StateNode<TContext>> Nodes, TContext Context, object History) before,
        Run run) =>
        !before.Nodes.SetEquals(run.Nodes) ||
        !ReferenceEquals(before.History, run.History) ||
        !EqualityComparer<TContext>.Default.Equals(before.Context, run.Context);

    // --- Microstep (SCXML https://www.w3.org/TR/scxml/#microstepProcedure) ---

    private void Microstep(
        Run run,
        List<TransitionDefinition<TContext>> transitions,
        MachineEvent evt,
        bool isInitial)
    {
        if (transitions.Count == 0)
        {
            return;
        }

        var filtered = RemoveConflictingTransitions(transitions, run);

        if (!isInitial)
        {
            // Exit actions read the state input the state was *entered* with, so they run before
            // this transition's own input is recorded (stateUtils.ts:1573-1583).
            ExitStates(run, filtered, evt);
        }

        // Transition content runs between exit and entry, scoped to the source state's input.
        foreach (var transition in filtered)
        {
            RunActions(run, transition.Actions, evt, transition.Source.Id);
        }

        // xstate v6 stateUtils.ts:1855-1867: the input a transition carries is recorded under
        // *every* target's id, before the entry pass — so the entering state's own entry actions,
        // invokes and output already see it. Entries are never pruned.
        foreach (var transition in filtered)
        {
            ApplyTransitionInput(run, transition, evt);
        }

        EnterStates(run, filtered, evt, isInitial);
    }

    private void ExitStates(Run run, List<TransitionDefinition<TContext>> transitions, MachineEvent evt)
    {
        var statesToExit = ComputeExitSet(transitions, run);
        statesToExit.Sort((a, b) => b.Order.CompareTo(a.Order));

        // Record history *before* removing anything from the configuration.
        var currentNodes = run.Nodes.ToList();
        ImmutableDictionary<string, ImmutableArray<string>>.Builder? changedHistory = null;

        foreach (var exitNode in statesToExit)
        {
            foreach (var historyNode in exitNode.Children.Where(c => c.Type is StateNodeType.History))
            {
                var recorded = historyNode.HistoryType is HistoryType.Deep
                    ? currentNodes.Where(n => IsAtomic(n) && IsDescendant(n, exitNode))
                    : currentNodes.Where(n => ReferenceEquals(n.Parent, exitNode));

                changedHistory ??= run.History.ToBuilder();
                changedHistory[historyNode.Id] = [.. recorded.OrderBy(n => n.Order).Select(n => n.Id)];
            }
        }

        foreach (var exitNode in statesToExit)
        {
            ExitNode(run, exitNode, evt);
            run.Nodes.Remove(exitNode);
        }

        if (changedHistory is not null)
        {
            run.History = changedHistory.ToImmutable();
        }
    }

    /// <summary>
    /// The body of exiting one state node — cancel its delayed transitions, run its exit
    /// actions, stop its invoked children. Shared by <see cref="ExitStates"/> (which also
    /// removes the node from the configuration), the machine-done path and <see cref="Stop"/>.
    /// </summary>
    private void ExitNode(Run run, StateNode<TContext> node, MachineEvent evt)
    {
        CancelDelayedTransitions(run, node);

        if (node.Timeout is not null)
        {
            Emit(run, new CancelEffect(node.TimeoutSendId));
        }

        RunActions(run, node.Exit, evt, node.Id);

        // SCXML exitStates does `statesToInvoke.delete(s)`: a deferred invocation whose state is
        // left before the macrostep settles never starts, so there is nothing to stop either.
        if (run.PendingInvokes.RemoveAll(p => ReferenceEquals(p.Node, node)) > 0)
        {
            return;
        }

        foreach (var invoke in node.Invokes)
        {
            if (invoke.Timeout is not null)
            {
                Emit(run, new CancelEffect(invoke.TimeoutSendId));
            }

            Emit(run, new StopChildEffect(invoke.Id));
        }
    }

    /// <summary>
    /// Cancels the timeout timer of an invoke that has just reported done or error, if one is
    /// outstanding.
    /// <para>
    /// Deviation from xstate v6: <c>StateNode.ts:586-611</c> synthesizes the cancelling transition
    /// for <c>xstate.done.actor</c> unconditionally but for <c>xstate.error.actor</c> only when
    /// the invoke declares <c>onError</c>, so an invoke with a timeout and no error handler leaves
    /// its timer running after the child fails. That asymmetry looks unintended; this port cancels
    /// on both.
    /// </para>
    /// </summary>
    private static void CancelActorTimeout(Run run, string actorId)
    {
        var timerId = $"xstate.timeout.actor.{actorId}";
        if (run.Timers.Any(t => t.Id == timerId))
        {
            Emit(run, new CancelEffect(timerId));
        }
    }

    /// <summary>
    /// Final teardown: cancel every timer still outstanding and stop every child still believed
    /// to be running (xstate v6 <c>cancelTimers</c> + <c>stopChildren</c>).
    /// </summary>
    private static void EmitTeardown(Run run)
    {
        foreach (var timer in run.Timers.ToList())
        {
            Emit(run, new CancelEffect(timer.Id));
        }

        foreach (var child in run.Children.ToList())
        {
            Emit(run, new StopChildEffect(child.Id));
        }
    }

    /// <summary>
    /// Records an effect and keeps the child/timer bookkeeping in sync. The recorded effect may
    /// differ from the one passed in (a spawn is stamped with its incarnation token; an id-less
    /// delayed raise/send is given a generated timer id), and a delayed raise/send that replaces
    /// an outstanding timer of the same id is preceded by its cancel.
    /// </summary>
    private static Effect Emit(Run run, Effect effect)
    {
        switch (effect)
        {
            case SpawnEffect spawn:
                // Every actor registration — invoked or spawned, generated id or explicit — passes
                // through here, so this is where the `{prefix}:{n}` reservation rule belongs.
                ReserveActorId(run, spawn.Id);

                // xstate v6 `assertChildIdFree` (transitionActions.ts) rejects two *live* actors
                // sharing an id: the second would shadow the first in the children map (and in the
                // address space), leaving the first unaddressable and unstoppable. An id whose
                // child this macrostep already stopped is free again — the supervisor/restart
                // pattern stops a child and respawns it under the same name in one transition.
                // `run.Children` is the ledger of live children and is updated eagerly by the
                // `StopChildEffect` case below and by the done/error handling in `Transition`, so
                // membership here *is* v6's "occupied and not stopped earlier in this transition".
                if (run.Children.Any(c => c.Id == spawn.Id))
                {
                    throw new InvalidOperationException(
                        $"Actor id '{spawn.Id}' is already in use by a running child. Give each " +
                        "invoke/spawn in a machine a unique id (ids must be unique across " +
                        "parallel regions too), or stop the existing child before reusing its id.");
                }

                // The incarnation token is minted here, from the snapshot's own counter, so that
                // it is reproduced exactly by a replay — the same rule the generated actor ids
                // follow. It is what tells a late done/error report of a *previous* child under
                // this id apart from one belonging to the child being created now.
                var incarnation = run.NextIncarnation++.ToString(CultureInfo.InvariantCulture);
                var stamped = spawn with { Incarnation = incarnation };
                effect = stamped;

                run.Children.Add(new ChildRecord(stamped.Id, stamped.Src, incarnation));
                run.DeferredStarts.Add(stamped.Id);
                break;

            case StopChildEffect stop:
                run.Children.RemoveAll(c => c.Id == stop.ChildId);
                // A child spawned and stopped in the same macrostep must not be started at its end.
                run.DeferredStarts.RemoveAll(id => id == stop.ChildId);
                break;

            case RaiseEffect raise:
            {
                var timerId = raise.Id ?? MintTimerId(run);
                if (raise.Id is null)
                {
                    effect = raise with { Id = timerId };
                }

                ScheduleTimer(run, new LogicalTimer(timerId, raise.Delay, TimerKind.Raise, raise.Event, null));
                break;
            }

            case SendToEffect { Delay: not null } send:
            {
                var timerId = send.Id ?? MintTimerId(run);
                if (send.Id is null)
                {
                    effect = send with { Id = timerId };
                }

                // The timer lives on the *sender*; `null` records "deliver to myself", matching
                // v6's `target === self ? 'self' : target` normalisation.
                var target = send.Target == EffectTarget.Self ? null : send.Target;
                ScheduleTimer(
                    run,
                    new LogicalTimer(timerId, send.Delay.Value, TimerKind.SendTo, send.Event, target));
                break;
            }

            case CancelEffect cancel:
                run.Timers.RemoveAll(t => t.Id == cancel.SendId);
                break;
        }

        run.Effects.Add(effect);
        return effect;
    }

    /// <summary>
    /// A deterministic handle for an id-less delayed raise/send — without one it could never be
    /// cancelled on teardown. xstate v6 <c>updateLogicalTimers</c> mints the same shape.
    /// </summary>
    private static string MintTimerId(Run run) => $"xstate.timer.auto.{run.NextTimerId++}";

    /// <summary>
    /// Adds a timer to the ledger. Scheduling over an outstanding id replaces it
    /// (setTimeout-by-key), and the replacement is preceded by the cancel of the timer it
    /// supersedes — the same way re-entering a state cancels and reschedules its `after` timers.
    /// </summary>
    private static void ScheduleTimer(Run run, LogicalTimer timer)
    {
        if (run.Timers.RemoveAll(t => t.Id == timer.Id) > 0)
        {
            run.Effects.Add(new CancelEffect(timer.Id));
        }

        run.Timers.Add(timer);
    }

    /// <summary>
    /// The generated-id prefix of a spawned/invoked logic — a port of xstate v6
    /// <c>getActorIdPrefix</c> (<c>system.ts</c>): the logic's own id, unless it is empty or the
    /// anonymous-machine placeholder, in which case the last-resort prefix <c>x</c> is used.
    /// <para>
    /// Deviation from v6: v6 prefers the <em>registered source key</em> when a spawn names its
    /// logic by string (<c>spawn('fetchUser')</c>). This port has no source-name registry — a
    /// spawn always carries the logic itself — so the logic id is the only available prefix.
    /// </para>
    /// <para>
    /// v6 additionally rejects a user source prefix starting with <c>xstate.</c>
    /// (<c>assertUnreservedPrefix</c>), because its runtime numbers internal helper actors
    /// (listeners, subscriptions) from system-level counters under that namespace and the two
    /// id spaces only stay disjoint by convention. This port ships no runtime and mints no
    /// internal actor ids, so there is nothing to collide with and the check is not enforced;
    /// a host that adds internal actors should re-introduce it.
    /// </para>
    /// </summary>
    private static string ActorIdPrefix(IActorLogic logic) =>
        logic is IMachineLogic { Id: { Length: > 0 } logicId } && logicId != ActorAddress.AnonymousMachineId
            ? logicId
            : "x";

    /// <summary>
    /// Allocates the next generated child id (<c>{prefix}:{n}</c>) and advances that prefix's
    /// counter — xstate v6 <c>allocateChildId</c> (<c>transitionActions.ts</c>). The counters ride
    /// on the snapshot, so a replay from the same snapshot allocates the same ids.
    /// </summary>
    private static string AllocateActorId(Run run, IActorLogic logic)
    {
        var prefix = ActorIdPrefix(logic);
        var next = run.NextActorIds.TryGetValue(prefix, out var counter) ? counter : 0;
        run.NextActorIds[prefix] = next + 1;
        return $"{prefix}:{next}";
    }

    /// <summary>
    /// xstate v6 <c>parseGeneratedActorId</c> + <c>reserveChildId</c> (<c>system.ts</c>,
    /// <c>transitionActions.ts</c>): an actor registered under an explicit generated-shaped id
    /// (<c>{prefix}:{n}</c>, split at the <em>last</em> <c>:</c>, non-empty prefix, non-negative
    /// integer suffix) reserves index <c>n</c>, so that prefix's counter jumps to
    /// <c>max(counter, n + 1)</c> and a child restored from a persisted snapshot can never be
    /// shadowed by a later generated spawn. Deliberately broader than the ids generation can
    /// produce: over-reserving only skips numbers, while under-reserving could collide.
    /// <para>
    /// Deviations from v6, both narrowing (they can only skip a reservation, never cause one):
    /// the suffix must be plain decimal digits (v6 runs it through JS <c>Number</c>, which also
    /// accepts <c>1e3</c>, whitespace and <c>0x</c> forms), and <c>n</c> must fit in an
    /// <see cref="int"/> (v6 tests <c>Number.isSafeInteger</c>).
    /// </para>
    /// </summary>
    private static void ReserveActorId(Run run, string id)
    {
        var separator = id.LastIndexOf(':');
        if (separator <= 0 || separator == id.Length - 1)
        {
            return;
        }

        var digits = id.AsSpan(separator + 1);
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return;
            }
        }

        if (!int.TryParse(digits, out var reserved) || reserved == int.MaxValue)
        {
            return;
        }

        var prefix = id[..separator];
        if (!run.NextActorIds.TryGetValue(prefix, out var counter) || counter < reserved + 1)
        {
            run.NextActorIds[prefix] = reserved + 1;
        }
    }

    private void EnterStates(
        Run run,
        List<TransitionDefinition<TContext>> transitions,
        MachineEvent evt,
        bool isInitial)
    {
        var statesToEnter = new HashSet<StateNode<TContext>>();
        var statesForDefaultEntry = new HashSet<StateNode<TContext>>();

        // `reenter` of the transition currently being expanded; see StillActive.
        var reenterCurrent = false;

        // History default transitions whose content is due, keyed by the history node's parent.
        var historyDefaults = new Dictionary<StateNode<TContext>, List<TransitionDefinition<TContext>>>();

        // A node still in the configuration was not exited by this transition, so a non-reenter
        // transition must not enter it again — the same rule the plain-target path applies to an
        // internal self-transition. Without it, targeting a history state whose recorded
        // configuration is still active re-runs entry actions, invocations and `after` timers
        // with no matching exit.
        bool StillActive(StateNode<TContext> node) => !reenterCurrent && run.Nodes.Contains(node);

        void AddAncestorStatesToEnter(IEnumerable<StateNode<TContext>> ancestors, StateNode<TContext>? reentrancyDomain)
        {
            foreach (var ancestor in ancestors)
            {
                if (reentrancyDomain is null || IsDescendant(ancestor, reentrancyDomain))
                {
                    statesToEnter.Add(ancestor);
                }

                if (ancestor.Type is StateNodeType.Parallel)
                {
                    foreach (var child in ProperChildren(ancestor))
                    {
                        if (!statesToEnter.Any(s => IsDescendant(s, child)))
                        {
                            statesToEnter.Add(child);
                            AddDescendantStatesToEnter(child);
                        }
                    }
                }
            }
        }

        void AddDescendantStatesToEnter(StateNode<TContext> node)
        {
            if (node.Type is StateNodeType.History)
            {
                if (run.History.TryGetValue(node.Id, out var recorded) && !recorded.IsDefaultOrEmpty)
                {
                    var historyNodes = NodesOf(recorded).Where(s => !StillActive(s)).ToList();
                    foreach (var s in historyNodes)
                    {
                        statesToEnter.Add(s);
                        AddDescendantStatesToEnter(s);
                    }
                    foreach (var s in historyNodes)
                    {
                        AddAncestorStatesToEnter(ProperAncestors(s, node.Parent), null);
                    }
                }
                else
                {
                    var defaultTransition = ResolveHistoryDefaultTransition(node);

                    // The history state's default transition is *content* the parent runs when the
                    // default is taken (xstate v6 `_historyDefaultTransition`,
                    // stateUtils.ts:1740-1767): it is keyed by the history node's parent and the
                    // parent joins `statesForDefaultEntry`, so the parent's own initial content
                    // runs first and the history default's second.
                    if (node.Parent is { } historyParent)
                    {
                        statesForDefaultEntry.Add(historyParent);

                        if (!ReferenceEquals(defaultTransition, historyParent.InitialTransition) &&
                            defaultTransition.Actions.Count > 0)
                        {
                            if (!historyDefaults.TryGetValue(historyParent, out var list))
                            {
                                list = [];
                                historyDefaults[historyParent] = list;
                            }

                            list.Add(defaultTransition);
                        }
                    }

                    foreach (var s in defaultTransition.Targets)
                    {
                        // The history pseudo-node is never part of the configuration.
                        if (s.Type is not StateNodeType.History)
                        {
                            statesToEnter.Add(s);
                        }

                        AddDescendantStatesToEnter(s);
                    }
                    foreach (var s in defaultTransition.Targets)
                    {
                        AddAncestorStatesToEnter(ProperAncestors(s, node.Parent), null);
                    }
                }

                return;
            }

            if (node.Type is StateNodeType.Compound)
            {
                // An initial transition may name more than one target — a deep initial across the
                // regions of a descendant parallel state (stateUtils.ts:1771-1791).
                var initialStates = node.InitialTransition!.Targets;

                foreach (var initialState in initialStates)
                {
                    if (initialState.Type is not StateNodeType.History)
                    {
                        statesToEnter.Add(initialState);
                        statesForDefaultEntry.Add(initialState);
                    }
                }

                foreach (var initialState in initialStates)
                {
                    AddDescendantStatesToEnter(initialState);
                }

                foreach (var initialState in initialStates)
                {
                    AddAncestorStatesToEnter(ProperAncestors(initialState, node), null);
                }

                return;
            }

            if (node.Type is StateNodeType.Parallel)
            {
                foreach (var child in ProperChildren(node))
                {
                    if (!statesToEnter.Any(s => IsDescendant(s, child)))
                    {
                        statesToEnter.Add(child);
                        statesForDefaultEntry.Add(child);
                        AddDescendantStatesToEnter(child);
                    }
                }
            }
        }

        foreach (var transition in transitions)
        {
            if (transition.Targets.Count == 0)
            {
                // Targetless transition: content only, no state is exited or entered.
                continue;
            }

            var domain = GetTransitionDomain(transition, run);
            var reenter = transition.Reenter;
            reenterCurrent = reenter;

            foreach (var targetNode in transition.Targets)
            {
                if (targetNode.Type is not StateNodeType.History &&
                    (!ReferenceEquals(transition.Source, targetNode) ||
                     !ReferenceEquals(transition.Source, domain) ||
                     reenter))
                {
                    statesToEnter.Add(targetNode);
                    statesForDefaultEntry.Add(targetNode);
                }

                AddDescendantStatesToEnter(targetNode);
            }

            foreach (var s in GetEffectiveTargetStates(transition, run))
            {
                var ancestors = ProperAncestors(s, domain);
                if (domain?.Type is StateNodeType.Parallel)
                {
                    ancestors.Add(domain);
                }

                AddAncestorStatesToEnter(
                    ancestors,
                    transition.Source.Parent is null && reenter ? null : domain);
            }

            if (reenter && ReferenceEquals(domain, transition.Source))
            {
                statesToEnter.Add(transition.Source);
            }
        }

        if (isInitial)
        {
            statesForDefaultEntry.Add(Root);
        }

        var completedNodes = new HashSet<StateNode<TContext>>();

        foreach (var node in statesToEnter.OrderBy(n => n.Order))
        {
            run.Nodes.Add(node);

            ScheduleDelayedTransitions(run, node, evt);

            // Invoked actors are spawned *before* the entry actions run (xstate v6 order), so
            // an entry action can already address the child by id — unless the machine defers
            // invocations to the end of the macrostep (SCXML order), in which case the node is
            // only queued here.
            if (DeferInvokes)
            {
                if (node.Invokes.Count > 0)
                {
                    run.PendingInvokes.Add((node, evt));
                }
            }
            else
            {
                StartInvocations(run, node, evt);
            }

            RunActions(run, node.Entry, evt, node.Id);

            if (statesForDefaultEntry.Contains(node))
            {
                // Default-entry content, in v6's order: the node's own initial transition first,
                // then any history default transition rooted at this node (stateUtils.ts:1963-1999).
                if (node.InitialTransition is { } initial)
                {
                    ApplyTransitionInput(run, initial, evt);
                    RunActions(run, initial.Actions, evt, node.Id);
                }

                if (historyDefaults.TryGetValue(node, out var defaults))
                {
                    foreach (var defaultTransition in defaults)
                    {
                        ApplyTransitionInput(run, defaultTransition, evt);
                        RunActions(run, defaultTransition.Actions, evt, node.Id);
                    }
                }
            }

            if (node.Type is not StateNodeType.Final)
            {
                continue;
            }

            // --- Final state reached: raise done.state events, possibly finish the machine ---
            var parent = node.Parent;
            var ancestorMarker = parent?.Type is StateNodeType.Parallel ? parent : parent?.Parent;
            var rootCompletionNode = ancestorMarker ?? node;

            if (parent?.Type is StateNodeType.Compound)
            {
                run.InternalQueue.Add(new DoneStateEvent(parent.Id, ResolveOutput(node, run, evt)));
            }

            while (ancestorMarker?.Type is StateNodeType.Parallel &&
                   !completedNodes.Contains(ancestorMarker) &&
                   IsInFinalState(run.Nodes, ancestorMarker))
            {
                completedNodes.Add(ancestorMarker);

                run.InternalQueue.Add(new DoneStateEvent(ancestorMarker.Id, ParallelOutput(ancestorMarker, run, evt)));
                rootCompletionNode = ancestorMarker;
                ancestorMarker = ancestorMarker.Parent;
            }

            if (ancestorMarker is not null)
            {
                continue;
            }

            run.Status = SnapshotStatus.Done;
            run.Output = GetMachineOutput(rootCompletionNode, run, evt);

            // The machine is finished: nothing further may be entered, or the configuration
            // would grow states alongside the top-level final state that ended it.
            break;
        }

        if (run.Status is SnapshotStatus.Done)
        {
            // The machine is finished: run remaining exit actions (deepest first), then cancel
            // every outstanding delayed send and stop every child still running.
            foreach (var node in run.Nodes.OrderByDescending(n => n.Order).ToList())
            {
                ExitNode(run, node, evt);
            }

            EmitTeardown(run);
        }
    }

    /// <summary>
    /// Records the input a transition carries under every one of its targets
    /// (xstate v6 <c>_stateInputs</c>, stateUtils.ts:1855-1867). A targetless transition records
    /// nothing, and the ledger is never pruned — an entry is only ever overwritten by a later
    /// entry of the same state.
    /// </summary>
    private static void ApplyTransitionInput(Run run, TransitionDefinition<TContext> transition, MachineEvent evt)
    {
        if (transition.InputResolver is null || transition.Targets.Count == 0)
        {
            return;
        }

        var input = transition.InputResolver(ArgsFor(run, evt, null, transition.Source.Id));

        foreach (var target in transition.Targets)
        {
            run.StateInputs[target.Id] = input;
        }
    }

    private object? ResolveOutput(StateNode<TContext> node, Run run, MachineEvent evt) =>
        node.Output?.Invoke(ArgsFor(run, evt, null, node.Id));

    /// <summary>
    /// The <c>done.state</c> payload of a completed parallel state: each region's key mapped to
    /// that region's own output. Read from the configuration rather than from the internal queue,
    /// because a region may have completed in an earlier microstep (or macrostep), by which time
    /// its <see cref="DoneStateEvent"/> has long been dequeued.
    /// </summary>
    private Dictionary<string, object?> ParallelOutput(StateNode<TContext> parallel, Run run, MachineEvent evt)
    {
        var regionOutput = new Dictionary<string, object?>();

        foreach (var region in ProperChildren(parallel))
        {
            regionOutput[region.Key] = RegionOutput(region, run, evt);
        }

        return regionOutput;
    }

    private object? RegionOutput(StateNode<TContext> region, Run run, MachineEvent evt)
    {
        if (region.Type is StateNodeType.Final)
        {
            return ResolveOutput(region, run, evt);
        }

        if (region.Type is StateNodeType.Parallel)
        {
            return ParallelOutput(region, run, evt);
        }

        var finalChild = ProperChildren(region)
            .FirstOrDefault(s => s.Type is StateNodeType.Final && run.Nodes.Contains(s));

        return finalChild is null ? null : ResolveOutput(finalChild, run, evt);
    }

    private object? GetMachineOutput(StateNode<TContext> rootCompletionNode, Run run, MachineEvent evt)
    {
        object? completionOutput = null;

        if (rootCompletionNode.Output is not null && rootCompletionNode.Parent is not null)
        {
            var rootDoneEvent = run.InternalQueue
                .OfType<DoneStateEvent>()
                .FirstOrDefault(e => e.StateId == Root.Id);

            completionOutput = rootDoneEvent is not null && ReferenceEquals(rootCompletionNode.Parent, Root)
                ? rootDoneEvent.Output
                : ResolveOutput(rootCompletionNode, run, evt);
        }
        else if (rootCompletionNode.Type is StateNodeType.Parallel)
        {
            completionOutput = run.InternalQueue
                .OfType<DoneStateEvent>()
                .FirstOrDefault(e => e.StateId == rootCompletionNode.Id)?.Output;
        }

        if (Root.Output is null)
        {
            return ReferenceEquals(rootCompletionNode.Parent, Root) ? completionOutput : null;
        }

        return Root.Output(new ActionArgs<TContext>(
            run.Context,
            new DoneStateEvent(rootCompletionNode.Id, completionOutput)));
    }

    // --- Actions -> context updates / raised events / effects ---

    /// <summary>Portable action type of the built-in <c>log</c> action.</summary>
    internal const string LogActionType = "xstate.log";

    /// <summary>
    /// Action args scoped to one state: its own <see cref="ActionArgs{TContext}.Input"/> plus the
    /// whole <c>_stateInputs</c> ledger (xstate v6 passes only <c>input</c>; the map is this
    /// port's addition for hosts that want to inspect it).
    /// </summary>
    private static ActionArgs<TContext> ArgsFor(
        Run run,
        MachineEvent evt,
        Func<string, bool>? inPredicate,
        string? stateId) =>
        new(run.Context, evt, inPredicate)
        {
            Input = stateId is not null && run.StateInputs.TryGetValue(stateId, out var input) ? input : null,
            Inputs = run.StateInputs.Count == 0
                ? EmptyInputs
                : new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(run.StateInputs)
        };

    private static readonly IReadOnlyDictionary<string, object?> EmptyInputs =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private void RunActions(
        Run run,
        IReadOnlyList<ActionDefinition<TContext>> actions,
        MachineEvent evt,
        string? inputStateId = null)
    {
        if (actions.Count == 0)
        {
            return;
        }

        // The In() predicate sees the configuration as it stands *now* (mid-transition), and is
        // captured by deferred effects, so it must be a snapshot rather than a live view.
        var inPredicate = MakeInPredicate(run);
        var scoped = ArgsFor(run, evt, inPredicate, inputStateId);

        foreach (var action in actions)
        {
            var args = scoped with { Context = run.Context, Params = action.Params };

            switch (action)
            {
                case AssignAction<TContext> assign:
                    run.Context = assign.Assigner(args);
                    break;

                case RaiseAction<TContext> raise:
                {
                    var raised = raise.EventFactory(args);
                    if (raise.Delay is null)
                    {
                        // An immediate raise never leaves the machine: it goes straight onto the
                        // internal queue and is consumed by this same macrostep.
                        run.InternalQueue.Add(raised);
                    }
                    else
                    {
                        Emit(run, new RaiseEffect(raised, raise.Id, raise.Delay.Resolve(args)));
                    }
                    break;
                }

                case SendToAction<TContext> send:
                    Emit(run, new SendToEffect(
                        send.TargetResolver(args),
                        send.EventFactory(args),
                        send.Id,
                        send.Delay?.Resolve(args)));
                    break;

                case CancelAction<TContext> cancel:
                    Emit(run, new CancelEffect(cancel.SendIdResolver(args)));
                    break;

                case LogAction<TContext> log:
                {
                    // v6 resolves `log` to an ordinary action descriptor (`enq.log` pushes the
                    // system logger as a built-in action), so it travels as a *named* action
                    // whose params a host can read without running it.
                    var entry = new LogParams(log.MessageResolver(args), log.Label);
                    Emit(run, new ActionEffect(LogActionType, entry, () => { }));
                    break;
                }

                case CustomAction<TContext> custom:
                {
                    var bound = args;
                    Emit(run, new ActionEffect(custom.Name, custom.Params, () => custom.Execute(bound)));
                    break;
                }

                case EmitAction<TContext> emit:
                    Emit(run, new EmitEffect(emit.EventFactory(args)));
                    break;

                case SpawnAction<TContext> spawn:
                {
                    var (logic, src) = Builder.MachineResolver.ResolveActorSource(
                        this, spawn.Logic, spawn.Src, Id);

                    Emit(run, new SpawnEffect(
                        // xstate v6 `allocateChildId` (transitionActions.ts): `{prefix}:{n}` off
                        // the snapshot's per-prefix counters, prefix derived from the logic.
                        spawn.Id ?? AllocateActorId(run, logic),
                        logic,
                        spawn.InputResolver?.Invoke(args),
                        src,
                        Incarnation: ""));
                    break;
                }

                case StopChildAction<TContext> stop:
                    Emit(run, new StopChildEffect(stop.ChildIdResolver(args)));
                    break;

                case ScriptAction<TContext> script:
                    ApplyScriptResult(run, script.Run(args));
                    break;
            }
        }
    }

    /// <summary>Applies a <see cref="ScriptResult{TContext}"/>: context, raised events, effects, in order.</summary>
    private static void ApplyScriptResult(Run run, ScriptResult<TContext> result)
    {
        run.Context = result.Context;
        if (result.Raised is { } raised)
        {
            run.InternalQueue.AddRange(raised);
        }
        if (result.Effects is { } effects)
        {
            foreach (var effect in effects)
            {
                Emit(run, effect);
            }
        }
    }

    /// <summary>
    /// SCXML §3.13 main-event-loop steps that run for every active invocation on each external
    /// event, before transition selection: <c>&lt;finalize&gt;</c> (the invoke's
    /// <see cref="InvokeDefinition{TContext}.OnExternalEvent"/> hook) and <c>autoforward</c>.
    /// Only invocations whose child is live in the ledger take part; internal <c>xstate.*</c>
    /// events are never forwarded.
    /// </summary>
    private void ApplyInvokeEventHooks(Run run, MachineEvent evt)
    {
        foreach (var node in run.Nodes.OrderBy(n => n.Order).ToList())
        {
            foreach (var invoke in node.Invokes)
            {
                if (!run.Children.Any(c => c.Id == invoke.Id))
                {
                    continue;
                }

                if (invoke.OnExternalEvent is { } hook &&
                    hook(ArgsFor(run, evt, MakeInPredicate(run), node.Id)) is { } result)
                {
                    ApplyScriptResult(run, result);
                }

                if (invoke.AutoForward && !IsInternalEventType(evt.Type))
                {
                    Emit(run, new SendToEffect(invoke.Id, evt));
                }
            }
        }
    }

    private static void ScheduleDelayedTransitions(Run run, StateNode<TContext> node, MachineEvent evt)
    {
        var args = ArgsFor(run, evt, null, node.Id);

        for (var i = 0; i < node.After.Count; i++)
        {
            var (delay, _) = node.After[i];
            var afterEvent = new AfterEvent(node.AfterKeys[i], node.Id);
            var resolved = delay.Resolve(args);

            // v6 compiles `after` into an entry-time `enq.raise(event, { id, delay })` and an
            // exit-time `enq.cancel(id)`, so a delayed transition is a delayed *raise*: when it
            // fires the event lands on the internal queue of the state that scheduled it.
            Emit(run, new RaiseEffect(afterEvent, afterEvent.SendId, resolved));
        }

        // A state-level `timeout` is its own event category, so it can never collide with an
        // `after` entry on the same state (stateUtils.ts:466-470). The delay is resolved eagerly,
        // here at entry time; `onTimeout` sees the input as it stands when the timer fires.
        if (node.Timeout is not null)
        {
            Emit(run, new RaiseEffect(
                new TimeoutEvent(node.Id),
                node.TimeoutSendId,
                node.Timeout.Resolve(args)));
        }
    }

    private static void CancelDelayedTransitions(Run run, StateNode<TContext> node)
    {
        for (var i = 0; i < node.After.Count; i++)
        {
            Emit(run, new CancelEffect(new AfterEvent(node.AfterKeys[i], node.Id).SendId));
        }
    }

    // --- Transition selection ---

    private List<TransitionDefinition<TContext>> SelectTransitions(Run run, MachineEvent evt)
    {
        var inPredicate = MakeInPredicate(run);
        return TransitionNode(Root, run, evt, inPredicate) ?? [];
    }

    /// <summary>stateUtils.ts `transitionNode`: child transitions win over ancestors.</summary>
    private List<TransitionDefinition<TContext>>? TransitionNode(
        StateNode<TContext> node,
        Run run,
        MachineEvent evt,
        Func<string, bool> inPredicate)
    {
        var activeChildren = node.Children.Where(run.Nodes.Contains).ToList();

        if (activeChildren.Count > 0)
        {
            var inner = new List<TransitionDefinition<TContext>>();
            foreach (var child in activeChildren)
            {
                var result = TransitionNode(child, run, evt, inPredicate);
                if (result is not null)
                {
                    inner.AddRange(result);
                }
            }

            if (inner.Count > 0)
            {
                return inner;
            }
        }

        return Next(node, run, evt, inPredicate);
    }

    private static List<TransitionDefinition<TContext>>? Next(
        StateNode<TContext> node,
        Run run,
        MachineEvent evt,
        Func<string, bool> inPredicate)
    {
        foreach (var candidate in Candidates(node, evt))
        {
            if (PassesGuard(candidate, run, evt, inPredicate))
            {
                return [candidate];
            }
        }

        return null;
    }

    /// <summary>stateUtils.ts `selectEventlessTransitions`.</summary>
    private List<TransitionDefinition<TContext>> SelectEventlessTransitions(Run run, MachineEvent evt)
    {
        var inPredicate = MakeInPredicate(run);
        var enabled = new List<TransitionDefinition<TContext>>();

        foreach (var atomic in run.Nodes.Where(IsAtomic).OrderBy(n => n.Order))
        {
            // A choice state is exactly one eventless transition whose targets the choice
            // function picks (xstate v6 `formatChoiceTransitions`, StateNode.ts:382-411): the
            // machine enters it, resolves it and leaves it inside the same macrostep, so a choice
            // state is never observed in a settled snapshot.
            if (atomic.Type is StateNodeType.Choice)
            {
                enabled.Add(ResolveChoice(run, atomic, evt, inPredicate));
                continue;
            }

            for (StateNode<TContext>? node = atomic; node is not null; node = node.Parent)
            {
                var found = false;
                foreach (var transition in node.Always)
                {
                    if (PassesGuard(transition, run, evt, inPredicate))
                    {
                        if (!enabled.Contains(transition))
                        {
                            enabled.Add(transition);
                        }
                        found = true;
                        break;
                    }
                }

                if (found)
                {
                    break;
                }
            }
        }

        return RemoveConflictingTransitions(enabled, run);
    }

    /// <summary>
    /// Runs a choice state's function and turns its <see cref="ChoiceResult"/> into an ordinary
    /// transition out of the choice node, so the rest of the algorithm (exit set, domain,
    /// ancestors, state input) applies unchanged.
    /// </summary>
    private TransitionDefinition<TContext> ResolveChoice(
        Run run,
        StateNode<TContext> node,
        MachineEvent evt,
        Func<string, bool> inPredicate)
    {
        var result = node.Choice!(ArgsFor(run, evt, inPredicate, node.Id))
                     ?? throw new InvalidOperationException(
                         $"Choice state \"{node.Id}\" must resolve to a target.");

        if (result.Targets.Count == 0)
        {
            throw new InvalidOperationException(
                $"Choice state \"{node.Id}\" must resolve to a target.");
        }

        return new TransitionDefinition<TContext>
        {
            Source = node,
            Event = null,
            TargetIds = [.. result.Targets],
            Targets = [.. result.Targets.Select(t => Builder.MachineResolver.ResolveTarget(this, node, t))],
            Reenter = result.Reenter,
            InputResolver = result.Input is null ? null : _ => result.Input
        };
    }

    private static bool PassesGuard(
        TransitionDefinition<TContext> transition,
        Run run,
        MachineEvent evt,
        Func<string, bool> inPredicate) =>
        transition.Guard is null ||
        transition.Guard(new GuardArgs<TContext>(run.Context, evt, inPredicate));

    private Func<string, bool> MakeInPredicate(Run run)
    {
        var configuration = run.Nodes.Select(n => n.Id).ToHashSet();
        return stateId =>
            configuration.Contains(stateId.StartsWith('#') ? stateId[1..] : stateId) ||
            (ResolveStateId(stateId) is { } resolved && configuration.Contains(resolved));
    }

    /// <summary>
    /// Candidate transitions for an event — a port of xstate v6 <c>getCandidates</c>
    /// (<c>stateUtils.ts</c>): every descriptor that names the event type (or one of its legacy
    /// aliases) exactly comes first in document order, then the descriptors that only matched by
    /// wildcard, <em>longest descriptor string first</em>.
    /// </summary>
    private static IEnumerable<TransitionDefinition<TContext>> Candidates(
        StateNode<TContext> node,
        MachineEvent evt)
    {
        var aliases = EventAliases(evt);
        var matching = node.Transitions
            .Where(t => t.Event is not null && DescriptorMatches(t.Event, evt, aliases))
            .ToList();

        if (matching.Count == 0)
        {
            return [];
        }

        var exact = matching.Where(t => IsExactDescriptor(t.Event!, evt, aliases)).OrderBy(t => t.Order);
        var wildcard = matching
            .Where(t => !IsExactDescriptor(t.Event!, evt, aliases))
            .OrderByDescending(t => DescriptorLength(t.Event!))
            .ThenBy(t => t.Order);

        return exact.Concat(wildcard);
    }

    /// <summary>
    /// Whether a descriptor names this event outright rather than catching it by wildcard.
    /// Non-string descriptors (CLR type, synthesized predicate) are always exact: they are
    /// generated for one event shape and never act as catch-alls.
    /// </summary>
    private static bool IsExactDescriptor(
        EventDescriptor descriptor,
        MachineEvent evt,
        IReadOnlyList<string> aliases) =>
        descriptor is not EventDescriptor.ByPattern pattern ||
        pattern.Pattern == evt.Type ||
        aliases.Contains(pattern.Pattern);

    private static int DescriptorLength(EventDescriptor descriptor) =>
        descriptor is EventDescriptor.ByPattern pattern ? pattern.Pattern.Length : int.MaxValue;

    private static bool DescriptorMatches(EventDescriptor descriptor, MachineEvent evt, IReadOnlyList<string> aliases)
    {
        if (descriptor.Matches(evt))
        {
            return true;
        }

        // Legacy/flattened descriptors: `xstate.done.state.{stateId}`, `xstate.after.{delay}.{stateId}`, ...
        return descriptor is EventDescriptor.ByPattern pattern &&
               aliases.Any(alias => MatchesPattern(pattern.Pattern, alias));
    }

    /// <summary>
    /// utils.ts `matchesEventDescriptor` — strict: only an explicit trailing ".*"
    /// matches a prefix, so "xstate.done.state.a" does not match a child's
    /// "xstate.done.state.a.b".
    /// </summary>
    private static bool MatchesPattern(string pattern, string type)
    {
        if (pattern == "*" || pattern == type)
        {
            return true;
        }

        return pattern.EndsWith(".*", StringComparison.Ordinal) &&
               type.StartsWith(pattern[..^1], StringComparison.Ordinal);
    }

    /// <summary>eventUtils.ts `getEventTypeAliases` (minus the canonical type itself).</summary>
    private static IReadOnlyList<string> EventAliases(MachineEvent evt) => evt switch
    {
        DoneStateEvent e => [$"xstate.done.state.{e.StateId}"],
        DoneActorEvent e => [$"xstate.done.actor.{e.ActorId}"],
        ErrorActorEvent e => [$"xstate.error.actor.{e.ActorId}"],
        AfterEvent e => [$"xstate.after.{e.Delay}.{e.StateId}"],
        TimeoutEvent e => [$"xstate.timeout.{e.StateId}"],
        ActorTimeoutEvent e => [$"xstate.timeout.actor.{e.ActorId}"],
        SnapshotEvent e => [$"xstate.snapshot.{e.ActorId}"],
        _ => []
    };

    // --- Conflict resolution / transition domain (SCXML) ---

    private List<TransitionDefinition<TContext>> RemoveConflictingTransitions(
        List<TransitionDefinition<TContext>> enabled,
        Run run)
    {
        var filtered = new List<TransitionDefinition<TContext>>();
        var exitSets = new Dictionary<TransitionDefinition<TContext>, List<StateNode<TContext>>>();

        List<StateNode<TContext>> ExitSetOf(TransitionDefinition<TContext> t)
        {
            if (!exitSets.TryGetValue(t, out var exitSet))
            {
                exitSet = ComputeExitSet([t], run);
                exitSets[t] = exitSet;
            }
            return exitSet;
        }

        foreach (var t1 in enabled)
        {
            var preempted = false;
            var toRemove = new List<TransitionDefinition<TContext>>();

            foreach (var t2 in filtered)
            {
                if (!ExitSetOf(t1).Intersect(ExitSetOf(t2)).Any())
                {
                    continue;
                }

                if (IsDescendant(t1.Source, t2.Source))
                {
                    toRemove.Add(t2);
                }
                else if (t2.Source.Type is StateNodeType.Final && t1.Source.Type is not StateNodeType.Final)
                {
                    // A transition from a done region yields to one from a live state.
                    toRemove.Add(t2);
                }
                else
                {
                    preempted = true;
                    break;
                }
            }

            if (!preempted)
            {
                foreach (var t3 in toRemove)
                {
                    filtered.Remove(t3);
                }
                filtered.Add(t1);
            }
        }

        return filtered;
    }

    private List<StateNode<TContext>> ComputeExitSet(
        IReadOnlyList<TransitionDefinition<TContext>> transitions,
        Run run)
    {
        var statesToExit = new HashSet<StateNode<TContext>>();

        foreach (var transition in transitions)
        {
            if (transition.Targets.Count == 0)
            {
                continue;
            }

            var domain = GetTransitionDomain(transition, run);
            var reenter = transition.Reenter;

            if (reenter && ReferenceEquals(transition.Source, domain))
            {
                statesToExit.Add(domain!);
            }

            foreach (var node in run.Nodes)
            {
                if (IsDescendant(node, domain))
                {
                    statesToExit.Add(node);
                }
            }
        }

        return [.. statesToExit];
    }

    private StateNode<TContext>? GetTransitionDomain(TransitionDefinition<TContext> transition, Run run)
    {
        var targetStates = GetEffectiveTargetStates(transition, run);
        var reenter = transition.Reenter;

        // An explicit domain (xstate v6 `_transitionDomain`, set by the SCXML compiler from
        // `transition/@type`) replaces the whole heuristic below with literal SCXML
        // `getTransitionDomain`: no `reenter`, no parallel narrowing, and the LCCA must be a
        // *compound* ancestor (stateUtils.ts:1250-1270).
        if (transition.Domain is { } domainKind)
        {
            if (domainKind is TransitionDomain.Internal &&
                transition.Source.Type is StateNodeType.Compound &&
                targetStates.All(t => IsDescendant(t, transition.Source)))
            {
                return transition.Source;
            }

            var candidates = new List<StateNode<TContext>>(targetStates) { transition.Source };
            var first = candidates[0];
            var rest = candidates.Skip(1).ToList();

            foreach (var ancestor in ProperAncestors(first, null))
            {
                if (ancestor.Type is StateNodeType.Compound &&
                    rest.All(sn => IsDescendant(sn, ancestor)))
                {
                    return ancestor;
                }
            }

            return null;
        }

        if (targetStates.All(t => ReferenceEquals(t, transition.Source) || IsDescendant(t, transition.Source)))
        {
            return reenter ? transition.Source : NarrowParallelDomain(transition.Source, targetStates);
        }

        var all = new List<StateNode<TContext>>(targetStates) { transition.Source };
        var head = all[0];
        var tail = all.Skip(1).ToList();

        foreach (var ancestor in ProperAncestors(head, null))
        {
            if (tail.All(sn => IsDescendant(sn, ancestor)))
            {
                return reenter ? ancestor : NarrowParallelDomain(ancestor, targetStates);
            }
        }

        return reenter ? null : NarrowParallelDomain(Root, targetStates);
    }

    private static StateNode<TContext> NarrowParallelDomain(
        StateNode<TContext> domain,
        List<StateNode<TContext>> targetStates)
    {
        var narrowed = domain;
        while (narrowed.Type is StateNodeType.Parallel)
        {
            var region = ProperChildren(narrowed).FirstOrDefault(child =>
                targetStates.All(target => ReferenceEquals(target, child) || IsDescendant(target, child)));

            if (region is null)
            {
                break;
            }

            narrowed = region;
        }

        return narrowed;
    }

    private List<StateNode<TContext>> GetEffectiveTargetStates(
        TransitionDefinition<TContext> transition,
        Run run)
    {
        var result = new List<StateNode<TContext>>();

        foreach (var target in transition.Targets)
        {
            if (target.Type is not StateNodeType.History)
            {
                if (!result.Contains(target))
                {
                    result.Add(target);
                }
                continue;
            }

            if (run.History.TryGetValue(target.Id, out var recorded) && !recorded.IsDefaultOrEmpty)
            {
                foreach (var node in NodesOf(recorded))
                {
                    if (!result.Contains(node))
                    {
                        result.Add(node);
                    }
                }
            }
            else
            {
                foreach (var node in GetEffectiveTargetStates(ResolveHistoryDefaultTransition(target), run))
                {
                    if (!result.Contains(node))
                    {
                        result.Add(node);
                    }
                }
            }
        }

        return result;
    }

    private static TransitionDefinition<TContext> ResolveHistoryDefaultTransition(StateNode<TContext> historyNode)
    {
        var resolved = ResolveHistoryDefaultTransitionCore(historyNode);

        // A default that resolves back to the history node itself (e.g. the parent's initial
        // state *is* the history node) would recurse forever while computing the entry set.
        if (resolved.Targets.Count == 0 || resolved.Targets.Any(t => ReferenceEquals(t, historyNode)))
        {
            throw new InvalidOperationException(
                $"History state '#{historyNode.Id}' has no recorded history and no usable default target: " +
                $"its default target resolves back to itself (state '#{historyNode.Parent?.Id}' uses " +
                $"'{historyNode.Key}' as its initial state). Give the history state an explicit default " +
                "target, or point the parent's initial state at a non-history child.");
        }

        return resolved;
    }

    private static TransitionDefinition<TContext> ResolveHistoryDefaultTransitionCore(StateNode<TContext> historyNode)
    {
        if (historyNode.HistoryDefault is { } explicitDefault)
        {
            return explicitDefault;
        }

        var parent = historyNode.Parent!;

        if (parent.Type is StateNodeType.Parallel)
        {
            return new TransitionDefinition<TContext>
            {
                Source = historyNode,
                Targets = [parent],
                Reenter = false
            };
        }

        return parent.InitialTransition
               ?? throw new InvalidOperationException(
                   $"History state '#{historyNode.Id}' has no default target and its parent has no initial state.");
    }

    // --- Tree helpers ---

    private static IEnumerable<StateNode<TContext>> ProperChildren(StateNode<TContext> node) =>
        node.Children.Where(c => c.Type is not StateNodeType.History);

    private static bool IsAtomic(StateNode<TContext> node) =>
        node.Type is StateNodeType.Atomic or StateNodeType.Final or StateNodeType.Choice;

    private static bool IsDescendant(StateNode<TContext> child, StateNode<TContext>? parent)
    {
        var marker = child;
        while (marker.Parent is not null && !ReferenceEquals(marker.Parent, parent))
        {
            marker = marker.Parent;
        }

        return ReferenceEquals(marker.Parent, parent);
    }

    private static List<StateNode<TContext>> ProperAncestors(StateNode<TContext> node, StateNode<TContext>? to)
    {
        var ancestors = new List<StateNode<TContext>>();
        if (ReferenceEquals(to, node))
        {
            return ancestors;
        }

        var marker = node.Parent;
        while (marker is not null && !ReferenceEquals(marker, to))
        {
            ancestors.Add(marker);
            marker = marker.Parent;
        }

        return ancestors;
    }

    private static bool IsInFinalState(HashSet<StateNode<TContext>> nodes, StateNode<TContext> node) => node.Type switch
    {
        StateNodeType.Compound => ProperChildren(node).Any(s => s.Type is StateNodeType.Final && nodes.Contains(s)),
        StateNodeType.Parallel => ProperChildren(node).All(s => IsInFinalState(nodes, s)),
        _ => node.Type is StateNodeType.Final
    };
}
