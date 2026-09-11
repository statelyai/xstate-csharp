using System.Text.Json;
using XState.Builder;

namespace XState.Json;

/// <summary>
/// Reads an XState v6 JSON machine config (see <c>machine.schema.json</c>) into the builder
/// drafts and hands them to <see cref="MachineResolver"/>. Named guards/actions/actors/delays
/// are looked up in <see cref="MachineImplementations{TContext}"/>; every missing name is
/// collected so a single error can report all of them.
/// <para>
/// Every object is held to the schema's property list: an unknown key is a typo and throws, and a
/// key the schema defines but this port cannot honour throws <see cref="NotSupportedException"/>.
/// </para>
/// </summary>
internal sealed class JsonMachineReader<TContext>
{
    private readonly MachineImplementations<TContext> _impls;
    private readonly JsonConfigOptions _options;
    private readonly Dictionary<string, DelayRef<TContext>> _configDelays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _actionDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _guardDefinitions = new(StringComparer.Ordinal);
    private readonly List<string> _missing = [];
    private string _machineId = "machine";

    internal JsonMachineReader(
        MachineImplementations<TContext>? implementations,
        JsonConfigOptions? options = null)
    {
        _impls = implementations ?? new MachineImplementations<TContext>();
        _options = options ?? JsonConfigOptions.Default;
    }

    /// <param name="contextFactorySelector">
    /// Given the config's <c>context</c> value, produces the machine's context factory.
    /// </param>
    internal StateMachine<TContext> Read(
        string json,
        Func<JsonElement, Func<object?, TContext>> contextFactorySelector)
    {
        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

        // Cloned so the machine can keep the source config after the document is disposed
        // (xstate v6 `serializeMachine`'s `_json` path).
        var root = document.RootElement.Clone();
        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidOperationException("A machine config must be a JSON object.");
        }

        _machineId = JsonConfig.GetString(root, "id") ?? "machine";
        ReadDefinitions(root);

        var context = JsonConfig.TryProp(root, "context", out var contextElement)
            ? contextElement.Clone()
            : JsonConfig.EmptyObject;

        var builder = new MachineBuilder<TContext>(_machineId).Context(contextFactorySelector(context));

        if (JsonConfig.GetString(root, "version") is { } version)
        {
            builder.Version(version);
        }

        if (JsonConfig.TryProp(root, "internalEvents", out var internalEvents))
        {
            if (internalEvents.ValueKind is not JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $"'internalEvents' at $ in machine '{_machineId}' must be an array of event types.");
            }

            builder.InternalEvents([.. internalEvents.EnumerateArray().Select(e => e.GetString()!)]);
        }

        // The named implementations become the machine's own registries, so a guard/action
        // reference keeps its portable identity ({ type, params }) after the build.
        foreach (var (name, guard) in _impls.Guards)
        {
            builder.Guard(name, guard);
        }

        foreach (var (name, action) in _impls.Actions)
        {
            builder.Action(name, action);
        }

        builder.Root(node => ConfigureState(node, root, "$", isRoot: true));

        if (_missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Machine '{_machineId}' references implementations that were not provided:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, _missing.Select(m => "  - " + m)));
        }

        var machine = builder.Build();
        machine.SourceJson = root;
        return machine;
    }

    // --- State nodes ---

    private void ConfigureState(StateBuilder<TContext> state, JsonElement node, string path, bool isRoot)
    {
        // v4 keys first: they carry behaviour that a bare "unknown key" message would not explain.
        foreach (var (legacy, replacement) in JsonSchemaKeys.LegacyStateKeys)
        {
            RejectLegacyKey(node, legacy, replacement, path);
        }

        JsonSchemaKeys.Validate(node, path, JsonSchemaKeys.StateNode, _machineId, _options);
        RejectUnsupported(node, path, isRoot);

        switch (JsonConfig.GetString(node, "type"))
        {
            case null or "atomic" or "compound":
                break;
            case "parallel":
                state.AsParallel();
                break;
            case "final":
                state.Type = StateNodeType.Final;
                break;
            case "history":
                state.Type = StateNodeType.History;
                state.HistoryType = JsonConfig.GetString(node, "history") == "deep"
                    ? HistoryType.Deep
                    : HistoryType.Shallow;
                state.HistoryDefaultTarget = JsonConfig.GetString(node, "target");
                break;
            case "choice":
                throw new NotSupportedException(
                    $"Choice states at {path} in machine '{_machineId}' are not supported: a JSON " +
                    "`choice` is a function (`@code`), and this port has no expression evaluator. " +
                    "Build the machine with `.Choice(...)` instead.");
            case var other:
                throw new NotSupportedException(
                    $"State type '{other}' at {path} in machine '{_machineId}' is not supported.");
        }

        if (JsonConfig.GetString(node, "history") is { } historyType && state.Type is not StateNodeType.History)
        {
            // `history` without `type: "history"` is still a history state in the schema.
            state.Type = StateNodeType.History;
            state.HistoryType = historyType == "deep" ? HistoryType.Deep : HistoryType.Shallow;
            state.HistoryDefaultTarget = JsonConfig.GetString(node, "target");
        }

        if (state.Type is StateNodeType.History && state.HistoryDefaultTarget is null &&
            JsonConfig.TryProp(node, "target", out var historyTarget) &&
            historyTarget.ValueKind is JsonValueKind.Array &&
            historyTarget.EnumerateArray().FirstOrDefault().ValueKind is JsonValueKind.String)
        {
            // A history default may name several targets (one per parallel region); keep them all.
            var targets = historyTarget.EnumerateArray().Select(t => t.GetString()!).ToList();
            state.HistoryDefaultTarget = targets[0];
            if (targets.Count > 1)
            {
                var draft = new Builder.TransitionDraft<TContext>();
                draft.TargetIds.AddRange(targets);
                state.HistoryDefaultDraft = draft;
            }
        }

        if (!isRoot && JsonConfig.GetString(node, "id") is { } explicitId)
        {
            state.Id(explicitId);
        }

        if (JsonConfig.TryProp(node, "initial", out var initial))
        {
            ReadInitial(state, initial, $"{path}.initial");
        }

        foreach (var action in ReadActions(node, "entry", path))
        {
            state.Entry(action);
        }

        foreach (var action in ReadActions(node, "exit", path))
        {
            state.Exit(action);
        }

        if (JsonConfig.TryProp(node, "tags", out var tags))
        {
            foreach (var tag in tags.ValueKind is JsonValueKind.Array
                         ? tags.EnumerateArray().Select(t => t.GetString())
                         : new[] { tags.GetString() })
            {
                if (tag is not null)
                {
                    state.Tag(tag);
                }
            }
        }

        if (JsonConfig.TryProp(node, "meta", out var meta))
        {
            state.Meta(meta.Clone());
        }

        if (JsonConfig.GetString(node, "description") is { } description)
        {
            state.Description(description);
        }

        if (JsonConfig.TryProp(node, "output", out var output))
        {
            JsonConfig.RejectExpression(output, $"{path}.output", _machineId);
            var value = output.Clone();
            state.Output(_ => value);
        }

        if (JsonConfig.TryProp(node, "on", out var on) && on.ValueKind is JsonValueKind.Object)
        {
            foreach (var entry in on.EnumerateObject())
            {
                var descriptor = new EventDescriptor.ByPattern(entry.Name);
                state.Transitions.AddRange(
                    ReadTransitions(entry.Value, $"{path}.on.{entry.Name}", descriptor));
            }
        }

        if (JsonConfig.TryProp(node, "always", out var always))
        {
            state.AlwaysTransitions.AddRange(ReadTransitions(always, $"{path}.always", null));
        }

        // State-level `onDone`: taken when this compound/parallel state's region(s) are done.
        if (JsonConfig.TryProp(node, "onDone", out var onDone))
        {
            state.OnDoneTransitions.AddRange(ReadTransitions(onDone, $"{path}.onDone", null));
        }

        // State-level `onError`: any `xstate.error.*` reaching this state.
        if (JsonConfig.TryProp(node, "onError", out var onError))
        {
            state.OnErrorTransitions.AddRange(ReadTransitions(onError, $"{path}.onError", null));
        }

        if (JsonConfig.TryProp(node, "timeout", out var timeout))
        {
            state.Timeout(ReadDuration(timeout, $"{path}.timeout"));
        }

        if (JsonConfig.TryProp(node, "onTimeout", out var onTimeout))
        {
            state.OnTimeoutTransitions.AddRange(ReadTransitions(onTimeout, $"{path}.onTimeout", null));
        }

        if (JsonConfig.TryProp(node, "after", out var after) && after.ValueKind is JsonValueKind.Object)
        {
            foreach (var entry in after.EnumerateObject())
            {
                var delayPath = $"{path}.after.{entry.Name}";
                var delay = ResolveDelay(entry.Name, delayPath);

                foreach (var transition in ReadTransitions(entry.Value, delayPath, null))
                {
                    // Each `after` transition gets its own timer; the resolver disambiguates the
                    // repeated key ("1000", "1000$1", …). Candidates under one delay stay in
                    // document order because their timers share a duration.
                    state.AfterTransitions.Add((delay, transition));
                }
            }
        }

        if (JsonConfig.TryProp(node, "invoke", out var invoke))
        {
            if (invoke.ValueKind is JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in invoke.EnumerateArray())
                {
                    ReadInvoke(state, item, $"{path}.invoke[{index++}]");
                }
            }
            else
            {
                ReadInvoke(state, invoke, $"{path}.invoke");
            }
        }

        if (JsonConfig.TryProp(node, "states", out var states) && states.ValueKind is JsonValueKind.Object)
        {
            foreach (var entry in states.EnumerateObject())
            {
                state.State(
                    entry.Name,
                    child => ConfigureState(child, entry.Value, $"{path}.states.{entry.Name}", isRoot: false));
            }
        }
    }

    /// <summary>
    /// Keys the v6 schema defines that this port cannot honour. Each throws where it stands,
    /// naming the key and its path: silently dropping any of them would change behaviour.
    /// </summary>
    private void RejectUnsupported(JsonElement node, string path, bool isRoot)
    {
        JsonConfig.RejectExpression(node, path, _machineId);

        if (JsonConfig.TryProp(node, "@exprLang", out _))
        {
            throw new NotSupportedException(
                $"'@exprLang' at {path} in machine '{_machineId}' is not supported: this port has no " +
                "expression evaluator. Reference named implementations instead.");
        }

        if (JsonConfig.TryProp(node, "route", out _))
        {
            throw new NotSupportedException(
                $"'route' at {path} in machine '{_machineId}' is not supported by the JSON reader. " +
                "Build the machine with `.Route(...)` instead.");
        }

        if (JsonConfig.TryProp(node, "choice", out _))
        {
            throw new NotSupportedException(
                $"'choice' at {path} in machine '{_machineId}' is not supported: a JSON `choice` is a " +
                "function (`@code`), and this port has no expression evaluator. Build the machine " +
                "with `.Choice(...)` instead.");
        }

        if (JsonConfig.TryProp(node, "input", out _))
        {
            throw new NotSupportedException(
                $"'input' at {path} in machine '{_machineId}' is not supported on a state node; " +
                "state input is carried by the transition that enters the state (`input` on a " +
                "transition object).");
        }

        if (JsonConfig.TryProp(node, "schemas", out var schemas) &&
            schemas.ValueKind is JsonValueKind.Object &&
            schemas.EnumerateObject().Any())
        {
            throw new NotSupportedException(
                $"'schemas' at {path} in machine '{_machineId}' is not supported: this port does not " +
                "validate context/event payloads against JSON Schema.");
        }

        if (isRoot)
        {
            return;
        }

        foreach (var key in JsonSchemaKeys.RootOnly)
        {
            if (JsonConfig.TryProp(node, key, out _))
            {
                throw new NotSupportedException(
                    $"'{key}' at {path} in machine '{_machineId}' is only supported on the machine root.");
            }
        }
    }

    private void ReadInitial(StateBuilder<TContext> state, JsonElement initial, string path)
    {
        if (initial.ValueKind is JsonValueKind.String)
        {
            state.Initial(initial.GetString()!);
            return;
        }

        JsonConfig.RejectExpression(initial, path, _machineId);
        JsonSchemaKeys.Validate(initial, path, JsonSchemaKeys.Initial, _machineId, _options);

        if (!JsonConfig.TryProp(initial, "target", out var target))
        {
            throw new InvalidOperationException(
                $"'initial' at {path} in machine '{_machineId}' must be a state key or an object with a 'target'.");
        }

        var targets = target.ValueKind is JsonValueKind.Array
            ? target.EnumerateArray().Select(t => t.GetString()!).ToArray()
            : [target.GetString() ?? throw new InvalidOperationException(
                $"'target' at {path} in machine '{_machineId}' must be a string or an array of strings.")];

        if (JsonConfig.TryProp(initial, "input", out var input))
        {
            JsonConfig.RejectExpression(input, $"{path}.input", _machineId);
            var value = input.Clone();
            state.Initial(t => t.Target(targets).Input(value));
        }
        else
        {
            state.Initial(targets);
        }
    }

    // --- Transitions ---

    private List<TransitionDraft<TContext>> ReadTransitions(
        JsonElement value,
        string path,
        EventDescriptor? descriptor)
    {
        if (value.ValueKind is not JsonValueKind.Array)
        {
            return [ReadTransition(value, path, descriptor)];
        }

        var index = 0;
        return [.. value.EnumerateArray().Select(item => ReadTransition(item, $"{path}[{index++}]", descriptor))];
    }

    private TransitionDraft<TContext> ReadTransition(JsonElement element, string path, EventDescriptor? descriptor)
    {
        var draft = new TransitionDraft<TContext> { Event = descriptor };

        if (element.ValueKind is JsonValueKind.String)
        {
            draft.TargetIds.Add(element.GetString()!);
            return draft;
        }

        JsonConfig.RejectExpression(element, path, _machineId);

        if (element.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Transition at {path} in machine '{_machineId}' must be a target string or a transition object.");
        }

        foreach (var (legacy, replacement) in JsonSchemaKeys.LegacyTransitionKeys)
        {
            RejectLegacyKey(element, legacy, replacement, path);
        }

        JsonSchemaKeys.Validate(element, path, JsonSchemaKeys.TransitionObject, _machineId, _options);

        if (JsonConfig.TryProp(element, "matches", out _))
        {
            throw new NotSupportedException(
                $"'matches' at {path} in machine '{_machineId}' is not supported: this port selects " +
                "transitions by event type and guard, not by event-payload matching.");
        }

        if (JsonConfig.TryProp(element, "target", out var target))
        {
            foreach (var id in target.ValueKind is JsonValueKind.Array
                         ? target.EnumerateArray().Select(t => t.GetString())
                         : new[] { target.GetString() })
            {
                draft.TargetIds.Add(id ?? throw new InvalidOperationException(
                    $"'target' at {path} in machine '{_machineId}' must be a string or an array of strings."));
            }
        }

        if (JsonConfig.TryProp(element, "guard", out var guard))
        {
            ReadGuard(draft, guard, $"{path}.guard");
        }

        // A transition `context` is a patch merged into the context *before* the transition's own
        // actions run (createMachineFromConfig.ts `getTransitionConfig`).
        if (JsonConfig.TryProp(element, "context", out var patch))
        {
            JsonConfig.RejectExpression(patch, $"{path}.context", _machineId);
            draft.Actions.Add(AssignPatch(patch.Clone(), $"{path}.context"));
        }

        draft.Actions.AddRange(ReadActions(element, "actions", path));

        if (JsonConfig.TryProp(element, "reenter", out var reenter))
        {
            draft.Reenter = reenter.ValueKind is JsonValueKind.True;
        }

        if (JsonConfig.TryProp(element, "input", out var input))
        {
            JsonConfig.RejectExpression(input, $"{path}.input", _machineId);
            var value = input.Clone();
            draft.InputResolver = _ => value;
        }

        if (JsonConfig.TryProp(element, "meta", out var meta))
        {
            draft.Meta = meta.Clone();
        }

        if (JsonConfig.GetString(element, "description") is { } description)
        {
            draft.Description = description;
        }

        return draft;
    }

    /// <summary>
    /// Fails loudly on an XState v4 key the reader does not understand: a v4 key carries behaviour
    /// that would otherwise be dropped, so it is reported with its v5/v6 replacement rather than
    /// with the generic "unknown key" message.
    /// </summary>
    private void RejectLegacyKey(JsonElement owner, string legacyKey, string replacement, string path)
    {
        if (JsonConfig.TryProp(owner, legacyKey, out _))
        {
            throw new InvalidOperationException(
                $"'{legacyKey}' at {path} in machine '{_machineId}' is an XState v4 key and is not supported; " +
                $"use '{replacement}' instead.");
        }
    }

    // --- Guards ---

    /// <summary>
    /// Binds a transition's guard. A plain name is bound by name (so its <c>params</c> reach the
    /// predicate as <see cref="GuardArgs{TContext}.Params"/>); the built-ins and the machine's own
    /// <c>guards</c> definitions compile to a closure.
    /// </summary>
    private void ReadGuard(TransitionDraft<TContext> draft, JsonElement element, string path)
    {
        var (name, @params) = ReadReference(element, "Guard", path);

        if (name.StartsWith("@xstate.", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Guard type '{name}' at {path} in machine '{_machineId}' uses the reserved " +
                "'@xstate.' prefix.");
        }

        if (_guardDefinitions.ContainsKey(name) ||
            name is "xstate.stateIn" or "xstate.not")
        {
            draft.Guard = CompileGuard(element, path, []);
            return;
        }

        if (_impls.Guards.ContainsKey(name))
        {
            draft.GuardName = name;
            draft.GuardParams = @params;
            return;
        }

        _missing.Add($"guard '{name}' (at {path})");
        draft.Guard = _ => false;
    }

    /// <summary>
    /// Compiles a guard reference to a predicate: the built-ins <c>xstate.stateIn</c> /
    /// <c>xstate.not</c> (createMachineFromConfig.ts:1060-1079), a machine-level
    /// <c>guards: { name: { when } }</c> definition, or a named implementation.
    /// </summary>
    private Guard<TContext> CompileGuard(JsonElement element, string path, List<string> stack)
    {
        var (name, @params) = ReadReference(element, "Guard", path);

        switch (name)
        {
            case "xstate.stateIn":
            {
                var stateId = JsonConfig.GetString(@params ?? default, "stateId")
                              ?? throw new InvalidOperationException(
                                  $"'xstate.stateIn' at {path} in machine '{_machineId}' requires " +
                                  "params.stateId.");
                return args => args.In(stateId);
            }

            case "xstate.not":
            {
                if (@params is not { } p || !JsonConfig.TryProp(p, "guard", out var inner))
                {
                    throw new InvalidOperationException(
                        $"'xstate.not' at {path} in machine '{_machineId}' requires params.guard.");
                }

                var negated = CompileGuard(inner, $"{path}.params.guard", stack);
                return args => !negated(args);
            }
        }

        if (_guardDefinitions.TryGetValue(name, out var definition))
        {
            if (stack.Contains(name, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Circular guard reference: {string.Join(" -> ", stack.Append(name))}");
            }

            var when = JsonConfig.TryProp(definition, "when", out var w)
                ? w
                : throw new InvalidOperationException(
                    $"Guard definition '$.guards.{name}' in machine '{_machineId}' requires a 'when'.");

            var inner = CompileGuard(when, $"$.guards.{name}.when", [.. stack, name]);
            // The reference's params are in scope for the definition body (v6 passes them down).
            return @params is null ? inner : args => inner(args with { Params = @params });
        }

        if (_impls.Guards.TryGetValue(name, out var guard))
        {
            return @params is null ? guard : args => guard(args with { Params = @params });
        }

        _missing.Add($"guard '{name}' (at {path})");
        return _ => false;
    }

    // --- Actions ---

    private List<ActionDefinition<TContext>> ReadActions(JsonElement owner, string key, string path)
    {
        if (!JsonConfig.TryProp(owner, key, out var value))
        {
            return [];
        }

        var actions = new List<ActionDefinition<TContext>>();

        if (value.ValueKind is not JsonValueKind.Array)
        {
            ReadAction(actions, value, $"{path}.{key}", [], null);
            return actions;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            ReadAction(actions, item, $"{path}.{key}[{index++}]", [], null);
        }

        return actions;
    }

    /// <summary>
    /// Appends the action(s) an action reference stands for. A reference to a machine-level
    /// <c>actions: { name: … }</c> definition expands into that definition's actions, with the
    /// reference's <c>params</c> in scope (createMachineFromConfig.ts `executeActions`).
    /// </summary>
    private void ReadAction(
        List<ActionDefinition<TContext>> into,
        JsonElement element,
        string path,
        List<string> stack,
        JsonElement? inheritedParams)
    {
        var (type, @params) = ReadReference(element, "Action", path);
        @params ??= inheritedParams;

        if (type.StartsWith("@xstate.", StringComparison.Ordinal))
        {
            into.Add(ReadBuiltInAction(element, type, path));
            return;
        }

        if (_actionDefinitions.TryGetValue(type, out var definition))
        {
            if (stack.Contains(type, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Circular action reference: {string.Join(" -> ", stack.Append(type))}");
            }

            var nested = new List<string>(stack) { type };
            if (definition.ValueKind is JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in definition.EnumerateArray())
                {
                    ReadAction(into, item, $"$.actions.{type}[{index++}]", nested, @params);
                }
            }
            else
            {
                ReadAction(into, definition, $"$.actions.{type}", nested, @params);
            }

            return;
        }

        if (_impls.Actions.TryGetValue(type, out var action))
        {
            into.Add(action with { Name = type, Params = @params });
            return;
        }

        _missing.Add($"action '{type}' (at {path})");
        into.Add(new CustomAction<TContext>(_ => { }) { Name = type, Params = @params });
    }

    /// <summary>The v6 built-in JSON actions (createMachineFromConfig.ts:785-826).</summary>
    private ActionDefinition<TContext> ReadBuiltInAction(JsonElement element, string type, string path)
    {
        if (!JsonSchemaKeys.BuiltInActions.TryGetValue(type, out var keys))
        {
            throw new NotSupportedException(
                $"Built-in action '{type}' at {path} in machine '{_machineId}' is not one of the " +
                $"actions the XState v6 JSON config defines ({string.Join(", ", JsonSchemaKeys.BuiltInActions.Keys)}).");
        }

        JsonSchemaKeys.Validate(element, path, keys, _machineId, _options);

        switch (type)
        {
            case "@xstate.raise":
            {
                var evt = ReadEvent(element, $"{path}.event");
                var delay = JsonConfig.TryProp(element, "delay", out var d)
                    ? ReadDuration(d, $"{path}.delay")
                    : null;
                return new RaiseAction<TContext>(_ => evt, delay, JsonConfig.GetString(element, "id"))
                {
                    Name = type
                };
            }

            case "@xstate.cancel":
            {
                var id = JsonConfig.GetString(element, "id")
                         ?? throw new InvalidOperationException(
                             $"'@xstate.cancel' at {path} in machine '{_machineId}' requires an 'id'.");
                return new CancelAction<TContext>(_ => id) { Name = type };
            }

            case "@xstate.log":
            {
                var args = JsonConfig.TryProp(element, "args", out var a) ? a.Clone() : default;
                object? message = args.ValueKind is JsonValueKind.Array && args.GetArrayLength() == 1
                    ? args.EnumerateArray().First()
                    : args.ValueKind is JsonValueKind.Undefined ? null : args;
                return new LogAction<TContext>(_ => message) { Name = type };
            }

            case "@xstate.emit":
            {
                var evt = ReadEvent(element, $"{path}.event");
                return new EmitAction<TContext>(_ => evt) { Name = type };
            }

            case "@xstate.assign":
            {
                if (!JsonConfig.TryProp(element, "context", out var patch))
                {
                    throw new InvalidOperationException(
                        $"'@xstate.assign' at {path} in machine '{_machineId}' requires a 'context'.");
                }

                JsonConfig.RejectExpression(patch, $"{path}.context", _machineId);
                return AssignPatch(patch.Clone(), $"{path}.context") with { Name = type };
            }

            default:
                throw new NotSupportedException(
                    $"Built-in action '{type}' at {path} in machine '{_machineId}' is not supported.");
        }
    }

    /// <summary>
    /// A context patch (<c>@xstate.assign</c>, or a transition's <c>context</c>) shallow-merged
    /// into the JSON context, mirroring v6's <c>Object.assign</c>.
    /// </summary>
    private AssignAction<TContext> AssignPatch(JsonElement patch, string path)
    {
        if (typeof(TContext) != typeof(JsonElement))
        {
            throw new NotSupportedException(
                $"A context patch at {path} in machine '{_machineId}' merges into a JSON context; " +
                $"machines with a {typeof(TContext).Name} context must assign through a named action.");
        }

        if (patch.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"The context patch at {path} in machine '{_machineId}' must be a JSON object.");
        }

        return new AssignAction<TContext>(args =>
            (TContext)(object)JsonConfig.Merge((JsonElement)(object)args.Context!, patch));
    }

    private MachineEvent ReadEvent(JsonElement owner, string path)
    {
        if (!JsonConfig.TryProp(owner, "event", out var evt) || evt.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"The event at {path} in machine '{_machineId}' must be an object with a 'type'.");
        }

        JsonConfig.RejectExpression(evt, path, _machineId);

        var type = JsonConfig.GetString(evt, "type")
                   ?? throw new InvalidOperationException(
                       $"The event at {path} in machine '{_machineId}' must be an object with a 'type'.");

        return new NamedEvent(type, evt.Clone());
    }

    /// <summary>Reads a <c>"name"</c> / <c>{ type, params }</c> reference (action or guard).</summary>
    private (string Type, JsonElement? Params) ReadReference(JsonElement element, string kind, string path)
    {
        if (element.ValueKind is JsonValueKind.String)
        {
            // Shorthand: a bare name. The v6 schema requires an object, but the shorthand is what
            // every hand-written config uses, so the port keeps reading it.
            return (element.GetString()!, null);
        }

        JsonConfig.RejectExpression(element, path, _machineId);

        if (element.ValueKind is not JsonValueKind.Object || JsonConfig.GetString(element, "type") is not { } type)
        {
            throw new InvalidOperationException(
                $"{kind} at {path} in machine '{_machineId}' must be a name or an object with a 'type'.");
        }

        if (kind == "Guard")
        {
            JsonSchemaKeys.Validate(element, path, JsonSchemaKeys.GuardObject, _machineId, _options);
        }
        else if (!type.StartsWith("@xstate.", StringComparison.Ordinal))
        {
            JsonSchemaKeys.Validate(element, path, JsonSchemaKeys.ActionObject, _machineId, _options);
        }

        if (!JsonConfig.TryProp(element, "params", out var @params))
        {
            return (type, null);
        }

        JsonConfig.RejectExpression(@params, $"{path}.params", _machineId);
        return (type, @params.Clone());
    }

    // --- Invoke ---

    private void ReadInvoke(StateBuilder<TContext> state, JsonElement element, string path)
    {
        if (element.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"'invoke' at {path} in machine '{_machineId}' must be an object or an array of objects.");
        }

        JsonConfig.RejectExpression(element, path, _machineId);
        JsonSchemaKeys.Validate(element, path, JsonSchemaKeys.InvokeObject, _machineId, _options);

        if (JsonConfig.TryProp(element, "registryKey", out _))
        {
            throw new NotSupportedException(
                $"'registryKey' at {path} in machine '{_machineId}' is not supported: this port has no " +
                "actor system registry.");
        }

        var src = JsonConfig.GetString(element, "src")
                  ?? throw new InvalidOperationException(
                      $"'invoke' at {path} in machine '{_machineId}' requires a 'src' name.");

        IActorLogic logic;
        if (_impls.Actors.TryGetValue(src, out var provided))
        {
            logic = provided;
        }
        else
        {
            _missing.Add($"actor '{src}' (at {path}.src)");
            logic = JsonConfig.MissingActorLogic;
        }

        var draft = new InvokeDraft<TContext>
        {
            Id = JsonConfig.GetString(element, "id"),
            Logic = logic
        };

        if (JsonConfig.TryProp(element, "input", out var input))
        {
            JsonConfig.RejectExpression(input, $"{path}.input", _machineId);
            var value = input.Clone();
            draft.InputResolver = _ => value;
        }

        if (JsonConfig.TryProp(element, "timeout", out var timeout))
        {
            draft.Timeout = ReadDuration(timeout, $"{path}.timeout");
        }

        if (JsonConfig.TryProp(element, "onDone", out var onDone))
        {
            draft.OnDone.AddRange(ReadTransitions(onDone, $"{path}.onDone", null));
        }

        if (JsonConfig.TryProp(element, "onError", out var onError))
        {
            draft.OnError.AddRange(ReadTransitions(onError, $"{path}.onError", null));
        }

        if (JsonConfig.TryProp(element, "onTimeout", out var onTimeout))
        {
            draft.OnTimeout.AddRange(ReadTransitions(onTimeout, $"{path}.onTimeout", null));
        }

        if (JsonConfig.TryProp(element, "onSnapshot", out var onSnapshot))
        {
            draft.OnSnapshot.AddRange(ReadTransitions(onSnapshot, $"{path}.onSnapshot", null));
        }

        state.Invokes.Add(draft);
    }

    // --- Definition maps (delays / actions / guards) ---

    private void ReadDefinitions(JsonElement root)
    {
        if (JsonConfig.TryProp(root, "actions", out var actions) && actions.ValueKind is JsonValueKind.Object)
        {
            foreach (var entry in actions.EnumerateObject())
            {
                _actionDefinitions[entry.Name] = entry.Value.Clone();
            }
        }

        if (JsonConfig.TryProp(root, "guards", out var guards) && guards.ValueKind is JsonValueKind.Object)
        {
            foreach (var entry in guards.EnumerateObject())
            {
                JsonSchemaKeys.Validate(
                    entry.Value, $"$.guards.{entry.Name}", JsonSchemaKeys.GuardDefinition, _machineId, _options);

                if (!JsonConfig.TryProp(entry.Value, "when", out _))
                {
                    throw new InvalidOperationException(
                        $"Guard definition '$.guards.{entry.Name}' in machine '{_machineId}' requires a 'when'.");
                }

                _guardDefinitions[entry.Name] = entry.Value.Clone();
            }
        }

        ReadDelayDefinitions(root);
    }

    private void ReadDelayDefinitions(JsonElement root)
    {
        if (!JsonConfig.TryProp(root, "delays", out var delays) || delays.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        foreach (var entry in delays.EnumerateObject())
        {
            var path = $"$.delays.{entry.Name}";
            var value = entry.Value;

            if (value.ValueKind is JsonValueKind.Object)
            {
                JsonConfig.RejectExpression(value, path, _machineId);
                JsonSchemaKeys.Validate(value, path, JsonSchemaKeys.DelayDefinition, _machineId, _options);
                value = JsonConfig.TryProp(value, "duration", out var duration)
                    ? duration
                    : throw new InvalidOperationException(
                        $"Delay definition '{path}' in machine '{_machineId}' requires a 'duration'.");
                path = $"{path}.duration";
            }

            _configDelays[entry.Name] = ReadDuration(value, path);
        }
    }

    /// <summary>
    /// A literal duration: milliseconds, "500ms"/"1.5s"/ISO-8601, or the name of a delay the
    /// implementations provide. An expression has no evaluator here and is rejected.
    /// </summary>
    private DelayRef<TContext> ReadDuration(JsonElement element, string path)
    {
        JsonConfig.RejectExpression(element, path, _machineId);

        if (JsonConfig.TryDuration(element, out var literal))
        {
            return new DelayRef<TContext>(_ => literal, literal.TotalMilliseconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }

        if (element.ValueKind is JsonValueKind.String && element.GetString() is { } name)
        {
            if (_impls.Delays.TryGetValue(name, out var provided))
            {
                return provided;
            }

            if (_configDelays.TryGetValue(name, out var configured))
            {
                return configured;
            }

            _missing.Add($"delay '{name}' (at {path})");
            return new DelayRef<TContext>(_ => TimeSpan.Zero, name);
        }

        throw new InvalidOperationException(
            $"The duration at {path} in machine '{_machineId}' must be a number of milliseconds, a " +
            "duration string (\"500ms\", \"1.5s\", ISO-8601), or the name of a provided delay.");
    }

    /// <summary>Resolves an `after` key: literal ms/duration, a provided delay, then the config's `delays`.</summary>
    private DelayRef<TContext> ResolveDelay(string key, string path)
    {
        if (JsonConfig.TryDuration(key, out var literal))
        {
            return new DelayRef<TContext>(_ => literal, key);
        }

        if (_impls.Delays.TryGetValue(key, out var provided))
        {
            return new DelayRef<TContext>(provided.Resolve, key);
        }

        if (_configDelays.TryGetValue(key, out var configured))
        {
            return new DelayRef<TContext>(configured.Resolve, key);
        }

        _missing.Add($"delay '{key}' (at {path})");
        return new DelayRef<TContext>(_ => TimeSpan.Zero, key);
    }
}
