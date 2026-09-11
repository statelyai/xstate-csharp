using System.Collections.Immutable;
using System.Globalization;
using XState.Persistence;

namespace XState;

/// <summary>
/// Persist and restore — a port of xstate v6 <c>State.ts</c> <c>getPersistedSnapshot</c> and
/// <c>StateMachine.ts</c> <c>restoreSnapshot</c>, minus the runtime.
/// <para>
/// v6's versions operate on a live actor tree: persisting recurses into child <em>actors</em>, and
/// restoring re-creates them. This port has no runtime, so the two halves meet the host at the
/// seam instead — <see cref="PersistOptions.ChildSnapshot"/> supplies each child's persisted
/// state on the way out, and <see cref="RestoreResult{TContext}.Children"/> /
/// <see cref="RestoreResult{TContext}.Timers"/> describe what the host must re-create and re-arm
/// on the way back in.
/// </para>
/// </summary>
public sealed partial class StateMachine<TContext>
{
    // --- Persist ---

    /// <summary>
    /// Reduces <paramref name="state"/> to plain data. Throws rather than writing a snapshot that
    /// could never be restored: an inline child (no registered source key) has nothing to name its
    /// logic, and a timer aimed at an actor the snapshot no longer knows has nothing to re-arm
    /// against.
    /// </summary>
    public PersistedSnapshot Persist(State<TContext> state, PersistOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        options ??= new PersistOptions();
        var rootAddress = options.RootAddress ?? ActorAddress.Root(Id);

        var children = new List<PersistedChild>(state.Children.Length);
        foreach (var child in state.Children)
        {
            // v6 State.ts:606-613. The check is about the *source key*, not the logic: without one
            // there is no portable name for what to re-create.
            if (child.Src is null && !options.AllowInlineActors)
            {
                throw new InvalidOperationException("An inline child actor cannot be persisted.");
            }

            var embed = options.EmbedChildren;

            // v6 State.ts:619-625: fail where it is actionable. A by-address reference without a
            // source key could be written but never restored.
            if (!embed && child.Src is null)
            {
                throw new InvalidOperationException(
                    $"Unable to persist child '{child.Id}' by address: it requires a registered source key.");
            }

            PersistedSnapshot? childSnapshot = null;
            if (embed)
            {
                childSnapshot = options.ChildSnapshot?.Invoke(child) ??
                    throw new InvalidOperationException(
                        $"Child '{child.Id}' has no persisted snapshot. Supply one through " +
                        $"{nameof(PersistOptions)}.{nameof(PersistOptions.ChildSnapshot)}, or set " +
                        $"{nameof(PersistOptions.EmbedChildren)} = false to persist the child by address.");
            }

            children.Add(new PersistedChild
            {
                Id = child.Id,
                Address = ActorAddress.Child(rootAddress, child.Id),
                Src = child.Src,
                Incarnation = child.Incarnation,
                Snapshot = childSnapshot,
                Remote = !embed
            });
        }

        var liveChildIds = state.Children.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        var timers = new Dictionary<string, PersistedTimer>(StringComparer.Ordinal);
        foreach (var (id, timer) in state.Timers)
        {
            // v6 State.ts:672-681: a send target that is no longer addressable would restore into
            // a timer with nowhere to fire.
            if (timer.Target is { } target &&
                target != EffectTarget.Parent &&
                target != EffectTarget.Self &&
                !liveChildIds.Contains(target))
            {
                throw new InvalidOperationException(
                    $"Unable to persist timer '{id}': target actor '{target}' is no longer " +
                    "addressable from this snapshot.");
            }

            timers[id] = new PersistedTimer
            {
                Id = timer.Id,
                DelayMs = timer.Delay.TotalMilliseconds,
                Type = timer.Kind is TimerKind.Raise ? "@xstate.raise" : "@xstate.sendTo",
                // v6 State.ts:655-659: the session id on a pending actor-timeout belongs to the
                // incarnation that scheduled it and is re-stamped on restore, so it is stripped
                // here rather than persisted stale.
                Event = timer.Event is ActorTimeoutEvent { SessionId: not null } timeout
                    ? new ActorTimeoutEvent(timeout.ActorId)
                    : timer.Event,
                Target = timer.Target is EffectTarget.Self ? null : timer.Target
            };
        }

        var history = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (id, slice) in state.HistoryValue)
        {
            history[id] = slice.IsDefault ? [] : [.. slice];
        }

        return new PersistedSnapshot
        {
            Status = PersistedStatus.Of(state.Status),
            Output = state.Output,
            Error = state.Error is { } error
                ? new PersistedError(error.GetType().FullName ?? error.GetType().Name, error.Message)
                : null,
            Value = state.Value,
            Context = state.Context,
            Children = children,
            Timers = timers,
            HistoryValue = history,
            NextActorIds = new Dictionary<string, int>(state.NextActorIds, StringComparer.Ordinal),
            NextTimerId = state.NextTimerId,
            NextIncarnation = state.NextIncarnation,
            // v6 State.ts:704-706 writes `stateInputs` only when the map has entries.
            StateInputs = state.StateInputs.IsEmpty
                ? null
                : new Dictionary<string, object?>(state.StateInputs, StringComparer.Ordinal),
            Machine = new PersistedMachineIdentity(Id, Version),
            // v6 State.ts:708-713 mirrors the version at the top level only when the machine
            // declares one; an unversioned machine writes neither field.
            Version = Version
        };
    }

    // --- Restore ---

    /// <summary>
    /// Rebuilds a snapshot from persisted data and reports what the host must bring back with it.
    /// <para>
    /// Every failure throws, where v6's <c>createActor</c> swallows restore errors into an errored
    /// actor: this is a pure function with no error channel to route them to, and a caller that
    /// receives a <see cref="State{TContext}"/> back must be able to trust that it means what it
    /// says.
    /// </para>
    /// </summary>
    public RestoreResult<TContext> Restore(PersistedSnapshot snapshot, RestoreOptions<TContext>? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        options ??= new RestoreOptions<TContext>();

        snapshot = CheckIdentity(snapshot, options);

        var rootAddress = options.RootAddress ?? ActorAddress.Root(Id);

        // 1. Children — resolved through the machine's registered sources.
        var children = new List<ChildToRestore>(snapshot.Children.Count);
        var childrenById = new Dictionary<string, ChildToRestore>(StringComparer.Ordinal);
        foreach (var persisted in snapshot.Children)
        {
            var address = persisted.Address ?? ActorAddress.Child(rootAddress, persisted.Id);
            var incarnation = persisted.Incarnation ?? "0";

            ChildToRestore restored;
            if (persisted.Remote)
            {
                // v6 StateMachine.ts:1424-1431. A by-address child is re-attached, not re-created,
                // so the source key is the only thing a host can route by.
                if (persisted.Src is null)
                {
                    throw new InvalidOperationException(
                        $"Unable to restore remote child '{persisted.Id}': a child referenced by " +
                        "address requires a registered source key.");
                }

                restored = new ChildToRestore(
                    persisted.Id, persisted.Src, address, incarnation, persisted.Snapshot, null, Remote: true);
            }
            else
            {
                if (persisted.Src is null || !Sources.TryGetValue(persisted.Src, out var logic))
                {
                    throw new InvalidOperationException(
                        $"Unable to restore child actor '{persisted.Id}': child source " +
                        $"'{persisted.Src ?? "<unknown>"}' is not provided in machine '{Id}'.");
                }

                restored = new ChildToRestore(
                    persisted.Id, persisted.Src, address, incarnation, persisted.Snapshot, logic, Remote: false);
            }

            children.Add(restored);
            childrenById[persisted.Id] = restored;
        }

        // 2. Timers — re-armed by the host, so every target has to resolve now.
        var timers = new List<LogicalTimer>(snapshot.Timers.Count);
        foreach (var (id, persisted) in snapshot.Timers.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            string? target = persisted.Target;
            if (target is not null && target != EffectTarget.Parent)
            {
                if (target == EffectTarget.Self)
                {
                    target = null;
                }
                else if (!childrenById.ContainsKey(target))
                {
                    throw new InvalidOperationException(
                        $"Unable to restore timer '{id}': target actor '{target}' is unavailable.");
                }
            }

            // v6 StateMachine.ts:1494-1500: an actor-timeout event is re-stamped with the
            // restored child's incarnation, so firing it still matches that child.
            var evt = persisted.Event;
            if (evt is ActorTimeoutEvent timeout && childrenById.TryGetValue(timeout.ActorId, out var child))
            {
                evt = new ActorTimeoutEvent(timeout.ActorId, child.Incarnation);
            }

            timers.Add(new LogicalTimer(
                persisted.Id,
                TimeSpan.FromMilliseconds(persisted.DelayMs),
                persisted.Type == "@xstate.raise" ? TimerKind.Raise : TimerKind.SendTo,
                evt,
                target));
        }

        // 3. History — v6 StateMachine.ts:1508-1544 warns and skips unresolvable ids rather than
        // failing: a history record is a hint about where to return to, not part of the current
        // configuration, and a machine that dropped the state has simply lost the hint.
        var history = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.Ordinal);
        foreach (var (key, ids) in snapshot.HistoryValue)
        {
            if (!_idMap.ContainsKey(key))
            {
                continue;
            }

            var resolved = ids.Where(_idMap.ContainsKey).ToImmutableArray();
            if (!resolved.IsEmpty)
            {
                history[key] = resolved;
            }
        }

        // 4. Value — validated against this machine, then expanded to a full configuration.
        var configuration = ConfigurationOf(snapshot.Value);

        // 5. Counter folding (v6 StateMachine.ts:1590-1614). A legacy single `_nextActorId` has no
        // per-prefix meaning and is dropped on the way in (it is simply not part of this DTO);
        // generated-shaped child ids raise their prefix's counter to a floor, so a snapshot
        // written before per-prefix counters — or edited by hand — can never hand out a live
        // child's id a second time.
        var nextActorIds = new Dictionary<string, int>(snapshot.NextActorIds, StringComparer.Ordinal);
        foreach (var child in snapshot.Children)
        {
            if (TryParseGeneratedActorId(child.Id, out var prefix, out var index) &&
                (!nextActorIds.TryGetValue(prefix, out var counter) || counter <= index))
            {
                nextActorIds[prefix] = index + 1;
            }
        }

        var state = new State<TContext>
        {
            Configuration = configuration,
            Context = options.ContextConverter is { } convert
                ? convert(snapshot.Context)
                : (TContext)snapshot.Context!,
            Status = PersistedStatus.Parse(snapshot.Status),
            Output = snapshot.Output,
            Error = snapshot.Error is { } error ? new PersistedErrorException(error) : null,
            HistoryValue = history.ToImmutable(),
            Machine = this,
            NextActorIds = nextActorIds.ToImmutableDictionary(StringComparer.Ordinal),
            NextTimerId = snapshot.NextTimerId,
            NextIncarnation = snapshot.NextIncarnation,
            Children = [.. children.Select(c => new ChildRecord(c.Id, c.Src, c.Incarnation))],
            Timers = timers.ToImmutableDictionary(t => t.Id, t => t, StringComparer.Ordinal),
            StateInputs = snapshot.StateInputs is null
                ? ImmutableDictionary.Create<string, object?>(StringComparer.Ordinal)
                : snapshot.StateInputs.ToImmutableDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal)
        };

        return new RestoreResult<TContext>(state, children, timers);
    }

    /// <summary>
    /// The identity gate — v6 <c>StateMachine.ts</c>:1357-1402. Runs before anything is rebuilt: a
    /// snapshot from another machine, or another version of this one, must never be half-restored.
    /// </summary>
    private PersistedSnapshot CheckIdentity(PersistedSnapshot snapshot, RestoreOptions<TContext> options)
    {
        var legacyVersion = snapshot.Version;
        var nestedVersion = snapshot.Machine?.Version;

        if (legacyVersion is not null && nestedVersion is not null && legacyVersion != nestedVersion)
        {
            throw new InvalidOperationException(
                $"Persisted snapshot version '{legacyVersion}' conflicts with machine version '{nestedVersion}'.");
        }

        var persistedVersion = nestedVersion ?? legacyVersion;

        if (snapshot.Machine?.Id is { } persistedId && persistedId != Id)
        {
            throw new InvalidOperationException(
                $"Machine ID mismatch: persisted snapshot was created by machine '{persistedId}', " +
                $"but machine '{Id}' was provided.");
        }

        if (persistedVersion != Version)
        {
            if (options.Migrate is not { } migrate)
            {
                throw new InvalidOperationException(
                    $"Persisted snapshot version '{persistedVersion ?? "(none)"}' does not match machine " +
                    $"version '{Version ?? "(none)"}' for machine '{Id}'. Provide a " +
                    $"{nameof(RestoreOptions<TContext>)}.{nameof(RestoreOptions<TContext>.Migrate)} " +
                    "function to migrate old snapshots.");
            }

            snapshot = migrate(snapshot, persistedVersion);
        }

        return snapshot;
    }

    /// <summary>
    /// The full configuration a persisted <c>value</c> denotes — a port of v6 <c>getStateNodes</c>
    /// + <c>getAllStateNodes</c> (<c>stateUtils.ts</c>:150-200, 907-941): the nodes the value names,
    /// their ancestors, and the default descendants of any compound/parallel node the value left
    /// unresolved.
    /// </summary>
    internal IReadOnlyList<string> ConfigurationOf(object value)
    {
        var nodes = new HashSet<StateNode<TContext>> { Root };
        CollectValueNodes(value, Root, [], nodes);

        // A compound node with an active child in the set is already resolved; one without needs
        // its defaults (v6 guards this with `activeParentSet`).
        var activeParents = nodes.Where(n => n.Parent is not null).Select(n => n.Parent!).ToHashSet();
        foreach (var node in nodes.ToList())
        {
            if (node.Type is StateNodeType.Compound && !activeParents.Contains(node))
            {
                AddDefaultDescendants(node, nodes);
            }
            else if (node.Type is StateNodeType.Parallel)
            {
                foreach (var region in node.Children.Where(c => c.Type is not StateNodeType.History))
                {
                    if (nodes.Add(region))
                    {
                        AddDefaultDescendants(region, nodes);
                    }
                }
            }
        }

        foreach (var node in nodes.ToList())
        {
            for (var ancestor = node.Parent; ancestor is not null && nodes.Add(ancestor); ancestor = ancestor.Parent)
            {
            }
        }

        return [.. nodes.OrderBy(n => n.Order).Select(n => n.Id)];
    }

    private void CollectValueNodes(
        object? value,
        StateNode<TContext> node,
        IReadOnlyList<string> path,
        HashSet<StateNode<TContext>> nodes)
    {
        if (StateValueShape.TryAsKey(value, out var key))
        {
            nodes.Add(ChildByKey(node, key) ?? throw MissingState(path, key));
            return;
        }

        foreach (var (childKey, childValue) in StateValueShape.AsMap(value))
        {
            var child = ChildByKey(node, childKey) ?? throw MissingState(path, childKey);
            nodes.Add(child);
            CollectValueNodes(childValue, child, [.. path, childKey], nodes);
        }
    }

    private InvalidOperationException MissingState(IReadOnlyList<string> path, string key) =>
        new($"Persisted snapshot references state '{string.Join('.', path.Append(key))}' which does " +
            $"not exist on machine '{Id}'.");

    private static StateNode<TContext>? ChildByKey(StateNode<TContext> node, string key) =>
        node.Children.FirstOrDefault(c => c.Key == key);

    /// <summary>
    /// The nodes entering <paramref name="node"/> by default would activate — v6
    /// <c>getInitialStateNodes</c>. Only reached for a value that leaves a branch unresolved; a
    /// value this machine produced always names its leaves.
    /// </summary>
    private static void AddDefaultDescendants(StateNode<TContext> node, HashSet<StateNode<TContext>> nodes)
    {
        switch (node.Type)
        {
            case StateNodeType.Parallel:
                // History pseudo-nodes are never part of a configuration.
                foreach (var region in node.Children.Where(c => c.Type is not StateNodeType.History))
                {
                    nodes.Add(region);
                    AddDefaultDescendants(region, nodes);
                }

                break;

            case StateNodeType.Compound:
                var targets = node.InitialTransition?.Targets is { Count: > 0 } explicitTargets
                    ? explicitTargets
                    : node.Children.FirstOrDefault(c => c.Type is not StateNodeType.History) is { } first
                        ? [first]
                        : Array.Empty<StateNode<TContext>>();

                foreach (var target in targets)
                {
                    for (var n = target; n is not null && n != node; n = n.Parent)
                    {
                        nodes.Add(n);
                    }

                    AddDefaultDescendants(target, nodes);
                }

                break;
        }
    }

    /// <summary>
    /// Splits a generated actor id (<c>{prefix}:{n}</c>) — the read half of the algorithm's
    /// <c>ReserveActorId</c>, applied to persisted ids on the way in.
    /// </summary>
    private static bool TryParseGeneratedActorId(string id, out string prefix, out int index)
    {
        prefix = string.Empty;
        index = 0;

        var separator = id.LastIndexOf(':');
        if (separator <= 0 || separator == id.Length - 1)
        {
            return false;
        }

        var digits = id.AsSpan(separator + 1);
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out index) ||
            index == int.MaxValue)
        {
            return false;
        }

        prefix = id[..separator];
        return true;
    }

    PersistedSnapshot IMachineLogic.Persist(ISnapshot state, PersistOptions? options)
    {
        if (state is not State<TContext> typed)
        {
            throw new ArgumentException(
                $"Snapshot of type {state.GetType().Name} does not belong to machine '{Id}' " +
                $"(expected State<{typeof(TContext).Name}>).");
        }

        return Persist(typed, options);
    }

    IRestoreResult IMachineLogic.Restore(
        PersistedSnapshot snapshot,
        Func<PersistedSnapshot, string?, PersistedSnapshot>? migrate,
        Func<object?, object?>? contextConverter,
        string? rootAddress) =>
        Restore(snapshot, new RestoreOptions<TContext>
        {
            Migrate = migrate,
            ContextConverter = contextConverter is null ? null : value => (TContext)contextConverter(value)!,
            RootAddress = rootAddress
        });
}
