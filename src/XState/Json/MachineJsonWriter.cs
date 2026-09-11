using System.Text.Json;
using System.Text.Json.Nodes;

namespace XState.Json;

/// <summary>
/// Walks a built machine back into the v6 JSON config shape (xstate v6 <c>machineConfigToJSON</c>,
/// <c>serialize.ts</c>). Structure, names and static values survive; a delegate — an inline guard
/// or action, an input/output resolver, a choice function — has no portable representation and
/// becomes a <c>{ "@code": "&lt;opaque&gt;" }</c> marker, the same marker v6 writes for functions.
/// A machine imported from JSON never comes here: it exports its own source config.
/// </summary>
internal static class MachineJsonWriter
{
    internal static JsonElement Write<TContext>(StateMachine<TContext> machine) =>
        JsonSerializer.SerializeToElement(Node(machine.Root, machine));

    private static JsonObject Node<TContext>(StateNode<TContext> node, StateMachine<TContext>? machine)
    {
        var json = new JsonObject();

        if (machine is not null)
        {
            json["id"] = machine.Id;

            if (machine.Version is { } version)
            {
                json["version"] = version;
            }

            if (machine.InternalEvents.Count > 0)
            {
                json["internalEvents"] = new JsonArray([.. machine.InternalEvents.Select(e => (JsonNode)e!)]);
            }
        }
        else if (node.Parent is { } parent && node.Id != $"{parent.Path}.{node.Key}")
        {
            // The id only needs writing when it was set explicitly; otherwise it is the key path.
            json["id"] = node.Id;
        }

        switch (node.Type)
        {
            case StateNodeType.Parallel:
                json["type"] = "parallel";
                break;
            case StateNodeType.Final:
                json["type"] = "final";
                break;
            case StateNodeType.History:
                json["type"] = "history";
                json["history"] = node.HistoryType is HistoryType.Deep ? "deep" : "shallow";
                if (node.HistoryDefault is { Targets.Count: > 0 } historyDefault)
                {
                    json["target"] = Reference(historyDefault.Targets[0]);
                }

                break;
            case StateNodeType.Choice:
                json["type"] = "choice";
                json["choice"] = Opaque();
                break;
        }

        if (node.Type is StateNodeType.Compound && node.InitialTransition is { } initial)
        {
            json["initial"] = Initial(node, initial);
        }

        if (node.Entry.Count > 0)
        {
            json["entry"] = List(node.Entry.Select(Action));
        }

        if (node.Exit.Count > 0)
        {
            json["exit"] = List(node.Exit.Select(Action));
        }

        // Transitions synthesized from `after`, `timeout` and the invokes also live in
        // `node.Transitions`; they are written under their own keys, never duplicated into `on`
        // (v6 json.test.ts "should not double-serialize invoke transitions").
        var written = new HashSet<TransitionDefinition<TContext>>(
            node.After.Select(a => a.Transition)
                .Concat(node.OnTimeout)
                .Concat(node.Invokes.SelectMany(i => i.OnDone.Concat(i.OnError).Concat(i.OnTimeout).Concat(i.OnSnapshot))));

        var on = new Dictionary<string, List<JsonNode>>(StringComparer.Ordinal);
        var onDone = new List<JsonNode>();

        foreach (var transition in node.Transitions)
        {
            if (written.Contains(transition))
            {
                continue;
            }

            switch (transition.Event)
            {
                case EventDescriptor.ByPattern pattern:
                    Add(on, pattern.Pattern, Transition(transition));
                    break;
                case EventDescriptor.ByClrType clr:
                    // A CLR-typed transition matches by event type name once it is JSON again.
                    Add(on, clr.EventType.Name, Transition(transition));
                    break;
                default:
                    // The only remaining synthesized descriptor is this state's own done event.
                    onDone.Add(Transition(transition));
                    break;
            }
        }

        if (on.Count > 0)
        {
            var map = new JsonObject();
            foreach (var (descriptor, transitions) in on)
            {
                map[descriptor] = List(transitions);
            }

            json["on"] = map;
        }

        if (onDone.Count > 0)
        {
            json["onDone"] = List(onDone);
        }

        if (node.Always.Count > 0)
        {
            json["always"] = List(node.Always.Select(Transition));
        }

        if (node.After.Count > 0)
        {
            var after = new Dictionary<string, List<JsonNode>>(StringComparer.Ordinal);
            foreach (var (delay, transition) in node.After)
            {
                Add(after, delay.Name ?? "0", Transition(transition));
            }

            var map = new JsonObject();
            foreach (var (key, transitions) in after)
            {
                map[key] = List(transitions);
            }

            json["after"] = map;
        }

        if (node.Timeout is { } timeout)
        {
            json["timeout"] = Duration(timeout);
        }

        if (node.OnTimeout.Count > 0)
        {
            json["onTimeout"] = List(node.OnTimeout.Select(Transition));
        }

        if (node.Invokes.Count > 0)
        {
            var invokes = node.Invokes.Select(Invoke).OfType<JsonNode>().ToList();
            if (invokes.Count > 0)
            {
                json["invoke"] = List(invokes);
            }
        }

        if (node.Tags.Count > 0)
        {
            json["tags"] = new JsonArray([.. node.Tags.Select(t => (JsonNode)t!)]);
        }

        if (Value(node.Meta) is { } meta)
        {
            json["meta"] = meta;
        }

        if (node.Description is { } description)
        {
            json["description"] = description;
        }

        if (node.Output is not null)
        {
            json["output"] = Opaque();
        }

        if (node.Children.Count > 0)
        {
            var states = new JsonObject();
            foreach (var child in node.Children)
            {
                states[child.Key] = Node(child, null);
            }

            json["states"] = states;
        }

        return json;
    }

    private static JsonNode Initial<TContext>(
        StateNode<TContext> node,
        TransitionDefinition<TContext> initial)
    {
        var targets = initial.Targets
            .Select(t => t.Parent == node ? t.Key : $"#{t.Id}")
            .ToList();

        JsonNode target = targets.Count == 1 ? targets[0] : new JsonArray([.. targets.Select(t => (JsonNode)t!)]);

        if (initial.InputResolver is null && initial.Actions.Count == 0)
        {
            return target;
        }

        var json = new JsonObject { ["target"] = target };
        if (initial.InputResolver is not null)
        {
            json["input"] = Opaque();
        }

        return json;
    }

    private static JsonNode? Invoke<TContext>(InvokeDefinition<TContext> invoke)
    {
        // v6 omits an invoke whose `src` is not a name: inline logic is not portable.
        if (invoke.Src is null)
        {
            return null;
        }

        var json = new JsonObject { ["src"] = invoke.Src, ["id"] = invoke.Id };

        if (invoke.InputResolver is not null)
        {
            json["input"] = Opaque();
        }

        if (invoke.OnDone.Count > 0)
        {
            json["onDone"] = List(invoke.OnDone.Select(Transition));
        }

        if (invoke.OnError.Count > 0)
        {
            json["onError"] = List(invoke.OnError.Select(Transition));
        }

        if (invoke.OnSnapshot.Count > 0)
        {
            json["onSnapshot"] = List(invoke.OnSnapshot.Select(Transition));
        }

        if (invoke.Timeout is { } timeout)
        {
            json["timeout"] = Duration(timeout);
            json["onTimeout"] = List(invoke.OnTimeout.Select(Transition));
        }

        return json;
    }

    private static JsonNode Transition<TContext>(TransitionDefinition<TContext> transition)
    {
        var json = new JsonObject();

        if (transition.Targets.Count == 1)
        {
            json["target"] = Reference(transition.Targets[0]);
        }
        else if (transition.Targets.Count > 1)
        {
            json["target"] = new JsonArray([.. transition.Targets.Select(t => (JsonNode)Reference(t)!)]);
        }

        if (transition.GuardDefinition is { Name: { } guardName } definition)
        {
            var guard = new JsonObject { ["type"] = guardName };
            if (Value(definition.Params) is { } @params)
            {
                guard["params"] = @params;
            }

            json["guard"] = guard;
        }
        else if (transition.Guard is not null)
        {
            json["guard"] = Opaque();
        }

        if (transition.Actions.Count > 0)
        {
            json["actions"] = List(transition.Actions.Select(Action));
        }

        if (transition.Reenter)
        {
            json["reenter"] = true;
        }

        if (transition.InputResolver is not null)
        {
            json["input"] = Opaque();
        }

        if (Value(transition.Meta) is { } meta)
        {
            json["meta"] = meta;
        }

        if (transition.Description is { } description)
        {
            json["description"] = description;
        }

        // A transition that is nothing but a target is written in the shorthand form.
        return json.Count == 1 && json.ContainsKey("target") ? json["target"]!.DeepClone() : json;
    }

    private static JsonNode Action<TContext>(ActionDefinition<TContext> action)
    {
        if (action.Name is not { } name)
        {
            return Opaque();
        }

        var json = new JsonObject { ["type"] = name };
        if (Value(action.Params) is { } @params)
        {
            json["params"] = @params;
        }

        return json;
    }

    private static JsonNode Duration<TContext>(DelayRef<TContext> delay)
    {
        if (delay.Name is not { } name)
        {
            return Opaque();
        }

        return JsonConfig.TryDuration(name, out var literal)
            ? JsonValue.Create(literal.TotalMilliseconds)
            : JsonValue.Create(name)!;
    }

    private static string Reference<TContext>(StateNode<TContext> node) => $"#{node.Id}";

    /// <summary>A value that has no portable representation, marked the way v6 marks functions.</summary>
    private static JsonObject Opaque() => new() { ["@code"] = "<opaque>", ["@lang"] = "csharp" };

    /// <summary>JSON for a runtime value (params, meta): JSON as-is, anything else serialized.</summary>
    private static JsonNode? Value(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonElement element:
                return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? null
                    : JsonSerializer.SerializeToNode(element);
            case string text:
                return JsonValue.Create(text);
        }

        try
        {
            return JsonSerializer.SerializeToNode(value, value.GetType());
        }
        catch (NotSupportedException)
        {
            return Opaque();
        }
        catch (JsonException)
        {
            return Opaque();
        }
    }

    private static void Add(Dictionary<string, List<JsonNode>> map, string key, JsonNode value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(value);
    }

    private static JsonNode List(IEnumerable<JsonNode> nodes)
    {
        var items = nodes.ToList();
        return items.Count == 1 ? items[0] : new JsonArray([.. items]);
    }
}
