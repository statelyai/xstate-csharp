using System.Globalization;

namespace XState.Builder;

/// <summary>
/// Turns a <see cref="StateBuilder{TContext}"/> tree into an immutable
/// <see cref="StateNode{TContext}"/> tree: document order, ids, parent wiring,
/// target resolution, initial/always/after/invoke/history transitions.
/// </summary>
internal static class MachineResolver
{
    public static StateMachine<TContext> Resolve<TContext>(
        string id,
        StateBuilder<TContext> root,
        Func<object?, TContext> contextFactory,
        bool deferInvokes = false,
        bool ignoreUnhandledErrors = false,
        string? version = null,
        IReadOnlyDictionary<string, IActorLogic>? sources = null,
        int maxMicrosteps = 1000,
        Func<MachineEvent, Exception?>? eventValidator = null,
        IReadOnlyDictionary<string, ActionDefinition<TContext>>? actions = null,
        IReadOnlyDictionary<string, Guard<TContext>>? guards = null,
        IReadOnlySet<string>? internalEvents = null)
    {
        var order = 0;
        var pairs = new List<(StateBuilder<TContext> Builder, StateNode<TContext> Node)>();
        var aliases = new List<(string Alias, StateNode<TContext> Node)>();
        var rootNode = BuildNode(root, null, id, id, ref order, pairs, aliases);
        var actorSources = sources is null
            ? new Dictionary<string, IActorLogic>(StringComparer.Ordinal)
            : new Dictionary<string, IActorLogic>(sources, StringComparer.Ordinal);

        var machine = new StateMachine<TContext>
        {
            Id = id,
            Version = version,
            Root = rootNode,
            ContextFactory = contextFactory,
            DeferInvokes = deferInvokes,
            IgnoreUnhandledErrors = ignoreUnhandledErrors,
            Sources = actorSources,
            MaxMicrosteps = maxMicrosteps,
            EventValidator = eventValidator,
            Actions = actions is null
                ? new Dictionary<string, ActionDefinition<TContext>>(StringComparer.Ordinal)
                : new Dictionary<string, ActionDefinition<TContext>>(actions, StringComparer.Ordinal),
            Guards = guards is null
                ? new Dictionary<string, Guard<TContext>>(StringComparer.Ordinal)
                : new Dictionary<string, Guard<TContext>>(guards, StringComparer.Ordinal),
            InternalEvents = internalEvents is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(internalEvents, StringComparer.Ordinal)
        };
        machine.RegisterNodes(rootNode);

        foreach (var (alias, node) in aliases)
        {
            machine.RegisterAlias(alias, node);
        }

        // Named actions and guards are resolved against the machine's registries as the tree is
        // walked, and every unresolved name is reported together — the same aggregated style the
        // JSON reader uses, so one build reports every missing implementation rather than the
        // first.
        var missing = new List<string>();

        foreach (var (builder, node) in pairs)
        {
            ResolveNode(machine, builder, node, missing);
        }

        AppendRouteTransitions(machine, rootNode, pairs, missing);

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Machine '{id}' references implementations that were not provided:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, missing.Select(m => "  - " + m)));
        }

        return machine;
    }

    // --- Pass 1: node tree ---

    private static StateNode<TContext> BuildNode<TContext>(
        StateBuilder<TContext> builder,
        StateNode<TContext>? parent,
        string machineId,
        string parentScopedId,
        ref int order,
        List<(StateBuilder<TContext> Builder, StateNode<TContext> Node)> pairs,
        List<(string Alias, StateNode<TContext> Node)> aliases)
    {
        // Ids compose from the *key path* from the machine root ("machineId.key1.key2…"), never
        // from an ancestor's explicit id — an explicit .Id() names one node, it does not re-root
        // its subtree (stateUtils.ts: `config.id ?? [machine.key, ...path].join('.')`).
        var id = builder.ExplicitId ?? (parent is null ? machineId : $"{parent.Path}.{builder.Key}");

        // An id-less node stays addressable relative to its nearest explicitly-id'd ancestor
        // ("#loaded.child"), registered as an alias so such references keep resolving.
        var scopedId = builder.ExplicitId ?? (parent is null ? machineId : $"{parentScopedId}.{builder.Key}");

        var node = new StateNode<TContext>
        {
            Key = builder.Key,
            Id = id,
            Type = builder.Type,
            Order = order++,
            HistoryType = builder.HistoryType,
            Output = builder.OutputResolver,
            Tags = builder.StateTags.ToHashSet(),
            Meta = builder.StateMeta,
            Description = builder.StateDescription,
            Parent = parent,
            Entry = [.. builder.EntryActions],
            Exit = [.. builder.ExitActions],
            Choice = builder.ChoiceResolver,
            Timeout = builder.TimeoutDelay
        };

        pairs.Add((builder, node));

        if (scopedId != id)
        {
            aliases.Add((scopedId, node));
        }

        var children = new List<StateNode<TContext>>(builder.Children.Count);
        foreach (var childBuilder in builder.Children)
        {
            children.Add(BuildNode(childBuilder, node, machineId, scopedId, ref order, pairs, aliases));
        }
        node.Children = children;

        return node;
    }

    // --- Pass 2: transitions ---

    private static void ResolveNode<TContext>(
        StateMachine<TContext> machine,
        StateBuilder<TContext> builder,
        StateNode<TContext> node,
        List<string> missing)
    {
        var order = 0;
        var transitions = new List<TransitionDefinition<TContext>>();

        node.Entry = ResolveActions(machine, node.Entry, $"#{node.Id} entry", missing);
        node.Exit = ResolveActions(machine, node.Exit, $"#{node.Id} exit", missing);

        ValidateChoice(builder, node);

        foreach (var draft in builder.Transitions)
        {
            transitions.Add(BuildTransition(machine, node, draft, draft.Event, order++, missing));
        }

        // `onDone` — this state's own completion event, raised when its region(s) reach a final
        // state. The descriptor is only known now that the node has an id.
        if (builder.OnDoneTransitions.Count > 0)
        {
            // Matched by predicate, not by the `xstate.done.state.{id}` string: an `OnDone` handler
            // is typed on DoneStateEvent (its guards/actions cast the event), so a user-sent event
            // that merely *spells* the done type must not select it.
            var doneStateId = node.Id;
            var doneDescriptor = new EventDescriptor.ByPredicate(
                evt => evt is DoneStateEvent e && e.StateId == doneStateId);
            foreach (var draft in builder.OnDoneTransitions)
            {
                transitions.Add(BuildTransition(machine, node, draft, doneDescriptor, order++, missing));
            }
        }

        // `after` — each delayed transition matches the `xstate.after` event raised
        // on entry of this state for the corresponding delay.
        var after = new List<(DelayRef<TContext> Delay, TransitionDefinition<TContext> Transition)>();
        var afterKeys = DelayKeys([.. builder.AfterTransitions.Select(a => a.Delay)]);
        for (var i = 0; i < builder.AfterTransitions.Count; i++)
        {
            var (delay, draft) = builder.AfterTransitions[i];
            var key = afterKeys[i];
            var stateId = node.Id;
            var descriptor = new EventDescriptor.ByPredicate(
                evt => evt is AfterEvent a && a.StateId == stateId && Equals(a.Delay, key));
            var transition = BuildTransition(machine, node, draft, descriptor, order++, missing);
            after.Add((delay, transition));
            transitions.Add(transition);
        }
        node.After = after;
        node.AfterKeys = afterKeys;

        // `timeout` / `onTimeout` — a dedicated event category, so a state-level timeout can never
        // collide with an `after` entry on the same state (stateUtils.ts:466-470).
        if (node.Timeout is not null && builder.OnTimeoutTransitions.Count == 0)
        {
            throw new InvalidOperationException(
                $"State \"{node.Id}\" has `timeout` but no `onTimeout` transition.");
        }

        if (node.Timeout is not null)
        {
            var stateId = node.Id;
            var descriptor = new EventDescriptor.ByPredicate(
                evt => evt is TimeoutEvent t && t.StateId == stateId);
            var onTimeout = new List<TransitionDefinition<TContext>>(builder.OnTimeoutTransitions.Count);
            foreach (var draft in builder.OnTimeoutTransitions)
            {
                var t = BuildTransition(machine, node, draft, descriptor, order++, missing);
                onTimeout.Add(t);
                transitions.Add(t);
            }
            node.OnTimeout = onTimeout;
        }

        // invokes
        var invokes = new List<InvokeDefinition<TContext>>(builder.Invokes.Count);
        for (var i = 0; i < builder.Invokes.Count; i++)
        {
            var draft = builder.Invokes[i];
            // xstate v6 `createInvokeId` (utils.ts): `${index}.${stateNodeId}`, static — no counter.
            var invokeId = draft.Id ?? $"{i}.{node.Id}";
            var (logic, src) = ResolveActorSource(machine, draft.Logic, draft.Src, node.Id);

            if (draft.Timeout is not null && draft.OnTimeout.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Invoke on state \"{node.Id}\" has `timeout` but no `onTimeout` transition.");
            }

            var invoke = new InvokeDefinition<TContext>
            {
                Id = invokeId,
                Logic = logic,
                Src = src,
                InputResolver = draft.InputResolver,
                Timeout = draft.Timeout,
                AutoForward = draft.AutoForward,
                OnExternalEvent = draft.OnExternalEvent
            };

            var onDone = new List<TransitionDefinition<TContext>>(draft.OnDone.Count);
            foreach (var d in draft.OnDone)
            {
                var t = BuildTransition(
                    machine, node, d,
                    new EventDescriptor.ByPredicate(evt => evt is DoneActorEvent e && e.ActorId == invokeId),
                    order++, missing);
                onDone.Add(t);
                transitions.Add(t);
            }

            var onError = new List<TransitionDefinition<TContext>>(draft.OnError.Count);
            foreach (var d in draft.OnError)
            {
                var t = BuildTransition(
                    machine, node, d,
                    new EventDescriptor.ByPredicate(evt => evt is ErrorActorEvent e && e.ActorId == invokeId),
                    order++, missing);
                onError.Add(t);
                transitions.Add(t);
            }

            var onTimeout = new List<TransitionDefinition<TContext>>(draft.OnTimeout.Count);
            foreach (var d in draft.OnTimeout)
            {
                var t = BuildTransition(
                    machine, node, d,
                    new EventDescriptor.ByPredicate(evt => evt is ActorTimeoutEvent e && e.ActorId == invokeId),
                    order++, missing);
                onTimeout.Add(t);
                transitions.Add(t);
            }

            var onSnapshot = new List<TransitionDefinition<TContext>>(draft.OnSnapshot.Count);
            foreach (var d in draft.OnSnapshot)
            {
                var t = BuildTransition(
                    machine, node, d,
                    new EventDescriptor.ByPredicate(evt => evt is SnapshotEvent e && e.ActorId == invokeId),
                    order++, missing);
                onSnapshot.Add(t);
                transitions.Add(t);
            }

            invoke.OnDone = onDone;
            invoke.OnError = onError;
            invoke.OnTimeout = onTimeout;
            invoke.OnSnapshot = onSnapshot;
            invokes.Add(invoke);
        }
        node.Invokes = invokes;

        // State-level `onError` (StateNode.ts:476-483) is a plain `xstate.error.*` wildcard
        // transition: it catches every error event reaching this state — an invoked *or* spawned
        // actor's `xstate.error.actor`, and the platform's `xstate.error.{kind}`. An invoke's own
        // `onError` matches exactly and therefore always outranks it (`getCandidates` puts exact
        // descriptors before wildcards), whichever is declared first.
        if (builder.OnErrorTransitions.Count > 0)
        {
            var descriptor = new EventDescriptor.ByPattern("xstate.error.*");
            foreach (var draft in builder.OnErrorTransitions)
            {
                transitions.Add(BuildTransition(machine, node, draft, descriptor, order++, missing));
            }
        }

        node.Transitions = transitions;
        node.Always = [.. builder.AlwaysTransitions.Select(d => BuildTransition(machine, node, d, null, order++, missing))];

        if (node.Type is StateNodeType.Compound)
        {
            var draft = builder.InitialDraft;
            IReadOnlyList<StateNode<TContext>> targets = draft is { TargetIds.Count: > 0 }
                ? [.. draft.TargetIds.Select(t => ResolveRelative(machine, node, t))]
                : [node.Children.FirstOrDefault(c => c.Type is not StateNodeType.History)
                   ?? throw new InvalidOperationException(
                       $"Compound state '#{node.Id}' has no child states to use as its initial state.")];

            node.InitialTransition = new TransitionDefinition<TContext>
            {
                Source = node,
                Targets = targets,
                Reenter = false,
                InputResolver = draft?.InputResolver,
                Actions = draft is null ? [] : ResolveActions(machine, draft.Actions, $"#{node.Id} initial", missing)
            };
        }

        if (node.Type is StateNodeType.History)
        {
            var draft = builder.HistoryDefaultDraft;
            var targetKeys = draft is { TargetIds.Count: > 0 }
                ? draft.TargetIds
                : builder.HistoryDefaultTarget is { } single ? [single] : (IReadOnlyList<string>)[];

            if (targetKeys.Count > 0)
            {
                node.HistoryDefault = new TransitionDefinition<TContext>
                {
                    Source = node,
                    Targets = [.. targetKeys.Select(t => ResolveRelative(machine, node.Parent!, t))],
                    Reenter = false,
                    InputResolver = draft?.InputResolver,
                    Actions = draft is null
                        ? []
                        : ResolveActions(machine, draft.Actions, $"#{node.Id} history default", missing)
                };
            }
        }
    }

    /// <summary>
    /// Collects every routable state — one that declares both an explicit id and
    /// <c>Route(...)</c> — into transitions on the machine <em>root</em>, keyed on
    /// <c>xstate.route</c> and guarded by exact equality of the event's <c>to</c> with
    /// <c>"#{id}"</c> (xstate v6 <c>formatRouteTransitions</c>, stateUtils.ts:686-745).
    /// <para>
    /// Because the source is the root, the ordinary domain computation does the rest: routing to
    /// a state under a parallel ancestor narrows to that region and leaves the others alone,
    /// routing to the state you are already in re-enters it, and a route nobody matches is simply
    /// an unhandled event.
    /// </para>
    /// </summary>
    private static void AppendRouteTransitions<TContext>(
        StateMachine<TContext> machine,
        StateNode<TContext> rootNode,
        List<(StateBuilder<TContext> Builder, StateNode<TContext> Node)> pairs,
        List<string> missing)
    {
        // `pairs` is in document order, which is the pre-order DFS v6 collects routes in.
        var routes = pairs.Where(p => p.Builder.RouteDraft is not null && p.Node.Parent is not null).ToList();
        if (routes.Count == 0)
        {
            return;
        }

        var descriptor = new EventDescriptor.ByPattern("xstate.route");
        var transitions = new List<TransitionDefinition<TContext>>(rootNode.Transitions);
        var order = transitions.Count == 0 ? 0 : transitions.Max(t => t.Order) + 1;

        foreach (var (builder, node) in routes)
        {
            if (builder.ExplicitId is null)
            {
                throw new InvalidOperationException(
                    $"State '#{node.Id}' declares a route but has no explicit id. Give it one with " +
                    ".Id(\"name\") — a route is addressed as \"#name\".");
            }

            var draft = builder.RouteDraft!;
            var to = $"#{builder.ExplicitId}";
            var declared = draft.Guard;

            var routed = new TransitionDraft<TContext>
            {
                Event = descriptor,
                // Exact string equality on `to` — no path resolution, no prefixes.
                Guard = args => args.Event is RouteEvent route && route.To == to &&
                                (declared is null || declared(args)),
                GuardName = draft.GuardName,
                GuardParams = draft.GuardParams,
                Reenter = draft.Reenter,
                Domain = draft.Domain,
                InputResolver = draft.InputResolver,
                Meta = draft.Meta,
                Description = draft.Description
            };
            routed.TargetIds.Add(to);
            routed.Actions.AddRange(draft.Actions);

            var transition = BuildTransition(machine, rootNode, routed, descriptor, order++, missing);

            // A named guard replaces the whole predicate, so re-apply the route match around it.
            if (routed.GuardName is not null && transition.Guard is { } named)
            {
                transition.Guard = args => args.Event is RouteEvent route && route.To == to && named(args);
            }

            transitions.Add(transition);
        }

        rootNode.Transitions = transitions;
    }

    /// <summary>
    /// A choice state is a pure branch point: it is entered, its function runs, and the machine
    /// immediately continues to the targets it returns. Anything that would make it observable as
    /// a state of its own — transitions, entry/exit content, invokes, children — is rejected here
    /// (xstate v6 <c>CHOICE_CONFIG_KEYS</c>, StateNode.ts).
    /// </summary>
    private static void ValidateChoice<TContext>(StateBuilder<TContext> builder, StateNode<TContext> node)
    {
        if (node.Type is not StateNodeType.Choice)
        {
            if (builder.ChoiceResolver is not null)
            {
                throw new InvalidOperationException(
                    $"State '#{node.Id}' declares a choice function but is not a choice state.");
            }

            return;
        }

        if (node.Choice is null)
        {
            throw new InvalidOperationException(
                $"Choice state '#{node.Id}' must declare a choice function.");
        }

        var offending = new List<string>();
        if (builder.Transitions.Count > 0 || builder.AlwaysTransitions.Count > 0 ||
            builder.AfterTransitions.Count > 0 || builder.OnDoneTransitions.Count > 0)
        {
            offending.Add("transitions");
        }
        if (builder.EntryActions.Count > 0) offending.Add("entry actions");
        if (builder.ExitActions.Count > 0) offending.Add("exit actions");
        if (builder.Invokes.Count > 0) offending.Add("invocations");
        if (builder.Children.Count > 0) offending.Add("child states");

        if (offending.Count > 0)
        {
            throw new InvalidOperationException(
                $"Choice state '#{node.Id}' may not declare {string.Join(", ", offending)}: a choice " +
                "state is entered, resolved and left within the same microstep.");
        }
    }

    /// <summary>
    /// Replaces every <see cref="NamedAction{TContext}"/> with the machine's registered template,
    /// carrying the reference's own <c>params</c>. An unregistered name is recorded rather than
    /// thrown, so one build reports every missing implementation at once.
    /// </summary>
    private static IReadOnlyList<ActionDefinition<TContext>> ResolveActions<TContext>(
        StateMachine<TContext> machine,
        IReadOnlyList<ActionDefinition<TContext>> actions,
        string where,
        List<string> missing)
    {
        if (actions.Count == 0 || !actions.Any(a => a is NamedAction<TContext>))
        {
            return actions;
        }

        var resolved = new List<ActionDefinition<TContext>>(actions.Count);
        foreach (var action in actions)
        {
            if (action is not NamedAction<TContext> named)
            {
                resolved.Add(action);
                continue;
            }

            if (machine.Actions.TryGetValue(named.Type, out var template))
            {
                resolved.Add(template with { Name = named.Type, Params = named.Params });
                continue;
            }

            missing.Add($"action '{named.Type}' (at {where})");
            resolved.Add(new CustomAction<TContext>(_ => { }) { Name = named.Type, Params = named.Params });
        }

        return resolved;
    }

    /// <summary>
    /// Pairs an invoke/spawn with the actor source it should record. Naming a key resolves it
    /// from the machine's registry (an unknown key is a build-time error); passing logic by value
    /// still records a key when that very instance is registered, so the by-value shorthand does
    /// not silently cost persistability.
    /// </summary>
    internal static (IActorLogic Logic, string? Src) ResolveActorSource<TContext>(
        StateMachine<TContext> machine,
        IActorLogic? logic,
        string? src,
        string nodeId)
    {
        if (src is not null)
        {
            return machine.Sources.TryGetValue(src, out var registered)
                ? (registered, src)
                : throw new InvalidOperationException(
                    $"State '#{nodeId}' invokes actor source '{src}', which machine " +
                    $"'{machine.Id}' does not register. Add it with .Actor(\"{src}\", logic).");
        }

        if (logic is null)
        {
            throw new InvalidOperationException(
                $"State '#{nodeId}' has an invoke with neither logic nor an actor source.");
        }

        foreach (var (key, candidate) in machine.Sources)
        {
            if (ReferenceEquals(candidate, logic))
            {
                return (logic, key);
            }
        }

        return (logic, null);
    }

    /// <summary>
    /// Timer keys for one state's <c>after</c> transitions. Two <c>After(...)</c> entries with the
    /// same duration (or the same named delay) would otherwise share a key and collapse onto one
    /// timer, so duplicates are disambiguated with the same <c>$index</c> suffix convention the
    /// JSON reader uses: the first occurrence keeps the plain key, later ones get <c>$1</c>, <c>$2</c>…
    /// </summary>
    internal static IReadOnlyList<string> DelayKeys<TContext>(IReadOnlyList<DelayRef<TContext>> delays)
    {
        var keys = new string[delays.Count];
        var used = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < delays.Count; i++)
        {
            var name = delays[i].Name ?? i.ToString(CultureInfo.InvariantCulture);
            var key = name;
            var occurrence = 0;

            while (!used.Add(key))
            {
                key = $"{name}${++occurrence}";
            }

            keys[i] = key;
        }

        return keys;
    }

    private static TransitionDefinition<TContext> BuildTransition<TContext>(
        StateMachine<TContext> machine,
        StateNode<TContext> source,
        TransitionDraft<TContext> draft,
        EventDescriptor? descriptor,
        int order,
        List<string> missing)
    {
        var transition = new TransitionDefinition<TContext>
        {
            Source = source,
            Event = descriptor,
            Guard = draft.Guard,
            TargetIds = [.. draft.TargetIds],
            Targets = [.. draft.TargetIds.Select(t => ResolveTransitionTarget(machine, source, t))],
            Reenter = draft.Reenter,
            Domain = draft.Domain,
            InputResolver = draft.InputResolver,
            Meta = draft.Meta,
            Description = draft.Description,
            Actions = ResolveActions(machine, draft.Actions, $"#{source.Id} transition", missing),
            Order = order
        };

        if (draft.GuardName is { } guardName)
        {
            if (machine.Guards.TryGetValue(guardName, out var predicate))
            {
                var definition = new GuardDefinition<TContext>(predicate, guardName, draft.GuardParams);
                transition.GuardDefinition = definition;
                transition.Guard = definition.Evaluate;
            }
            else
            {
                missing.Add($"guard '{guardName}' (at #{source.Id} transition)");
                transition.Guard = _ => false;
            }
        }

        return transition;
    }

    // --- Target resolution (mirrors stateUtils.ts `resolveTarget`) ---

    internal static StateNode<TContext> ResolveTarget<TContext>(
        StateMachine<TContext> machine,
        StateNode<TContext> source,
        string target) => ResolveTransitionTarget(machine, source, target);

    private static StateNode<TContext> ResolveTransitionTarget<TContext>(
        StateMachine<TContext> machine,
        StateNode<TContext> source,
        string target)
    {
        if (target.StartsWith('#'))
        {
            return machine.GetNodeById(target[1..]);
        }

        if (target.StartsWith('.'))
        {
            // Relative to the source state itself.
            return source.Parent is null
                ? ResolveRelative(machine, source, target[1..])
                : ResolveRelative(machine, source.Parent, source.Key + target);
        }

        if (source.Parent is null)
        {
            throw new ArgumentException(
                $"Invalid target '{target}' from the root node of machine '{machine.Id}'. Did you mean '.{target}'?");
        }

        return ResolveRelative(machine, source.Parent, target);
    }

    /// <summary>Resolves a dotted path (or "#id") relative to <paramref name="from"/>'s children.</summary>
    private static StateNode<TContext> ResolveRelative<TContext>(
        StateMachine<TContext> machine,
        StateNode<TContext> from,
        string path)
    {
        if (path.StartsWith('#'))
        {
            return machine.GetNodeById(path[1..]);
        }

        var current = from;
        foreach (var key in path.Split('.'))
        {
            if (key.Length == 0)
            {
                break;
            }

            current = current.Children.FirstOrDefault(c => c.Key == key)
                      ?? throw new ArgumentException(
                          $"Child state '{key}' does not exist on '#{current.Id}'.");
        }

        return current;
    }
}
