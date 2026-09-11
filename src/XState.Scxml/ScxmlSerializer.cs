using System.Xml.Linq;

namespace XState.Scxml;

/// <summary>Options for <see cref="ScxmlConverter.ToScxml(StateMachine{ScxmlDataModel}, ScxmlSerializationOptions?)"/>.</summary>
public sealed record ScxmlSerializationOptions
{
    /// <summary>
    /// What to do with machine parts SCXML cannot express — an action, guard or event descriptor
    /// that was not produced by <see cref="ScxmlConverter.Parse(string, Func{string, string})"/>
    /// and is therefore an opaque delegate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="false"/> (the default): serialization throws
    /// <see cref="NotSupportedException"/> naming the state and the part. This is the safe default
    /// because the alternatives all *silently change the machine's behaviour* — a dropped guard
    /// turns a conditional transition into an unconditional one, and a dropped event descriptor
    /// turns an evented transition into an eventless one.
    /// </para>
    /// <para>
    /// <see langword="true"/>: emit a <c>&lt;log&gt;</c> placeholder where the action was and omit
    /// the guard/event, producing a structurally faithful but behaviourally different document —
    /// useful for diagrams and diffing, never for execution.
    /// </para>
    /// </remarks>
    public bool EmitPlaceholders { get; init; }
}

/// <summary>
/// Renders a <see cref="StateMachine{TContext}"/> over <see cref="ScxmlDataModel"/> back to SCXML.
/// </summary>
/// <remarks>
/// The state tree, transitions, invocations and donedata are reconstructed structurally. Actions
/// are opaque delegates in the core, so executable content is recovered from the source elements
/// the parser recorded. A machine that was <em>not</em> produced by
/// <see cref="ScxmlConverter.Parse(string, Func{string, string})"/> may hold actions, guards and
/// event descriptors that are opaque delegates with no SCXML form; serializing one throws unless
/// <see cref="ScxmlSerializationOptions.EmitPlaceholders"/> is set.
/// </remarks>
internal static class ScxmlSerializer
{
    public static XDocument Serialize(
        StateMachine<ScxmlDataModel> machine,
        ScxmlSerializationOptions? options = null)
    {
        var placeholders = options?.EmitPlaceholders ?? false;

        var ns = ScxmlConverter.Namespace;
        var root = new XElement(ns + "scxml",
            new XAttribute("version", "1.0"),
            new XAttribute("datamodel", "ecmascript"));

        if (machine.Id != "(machine)")
        {
            root.Add(new XAttribute("name", machine.Id));
        }

        ScxmlMetadata.MachineSources.TryGetValue(machine, out var source);

        if (InitialElementOf(source) is { } rootInitial)
        {
            root.Add(Rename(rootInitial, ns));
        }
        else if (InitialTargetOf(machine.Root) is { } initial)
        {
            root.Add(new XAttribute("initial", initial));
        }

        if (source is not null)
        {
            // binding="late" changes when state-local <data> is assigned, so it has to survive the
            // round-trip alongside the <datamodel> elements it governs.
            if (source.Attribute("binding")?.Value is { Length: > 0 } binding)
            {
                root.Add(new XAttribute("binding", binding));
            }

            foreach (var datamodel in source.Elements().Where(e => e.Name.LocalName == "datamodel"))
            {
                root.Add(Rename(datamodel, ns));
            }

            foreach (var script in source.Elements().Where(e => e.Name.LocalName == "script"))
            {
                root.Add(Rename(script, ns));
            }
        }

        WriteBody(machine.Root, root, ns, placeholders);

        return new XDocument(root);
    }

    private static void WriteBody(
        StateNode<ScxmlDataModel> node,
        XElement target,
        XNamespace ns,
        bool placeholders)
    {
        WriteActions(node.Entry, "onentry", target, ns, placeholders);
        WriteActions(node.Exit, "onexit", target, ns, placeholders);

        foreach (var transition in Ordered(node))
        {
            target.Add(WriteTransition(transition, ns, placeholders));
        }

        foreach (var invoke in node.Invokes)
        {
            target.Add(ScxmlMetadata.Invokes.TryGetValue(invoke, out var metadata)
                ? Rename(metadata.Source, ns)
                : new XElement(ns + "invoke", new XAttribute("type", "scxml"), new XAttribute("id", invoke.Id)));
        }

        if (node.Type is StateNodeType.Final && ScxmlMetadata.NodeSources.TryGetValue(node, out var finalSource))
        {
            var donedata = finalSource.Elements().FirstOrDefault(e => e.Name.LocalName == "donedata");
            if (donedata is not null)
            {
                target.Add(Rename(donedata, ns));
            }
        }

        foreach (var child in node.Children)
        {
            target.Add(WriteNode(child, ns, placeholders));
        }
    }

    private static XElement WriteNode(StateNode<ScxmlDataModel> node, XNamespace ns, bool placeholders)
    {
        var element = new XElement(ns + ElementNameOf(node.Type));
        element.Add(new XAttribute("id", DocumentIdOf(node)));

        if (node.Type is StateNodeType.History)
        {
            element.Add(new XAttribute("type", node.HistoryType is HistoryType.Deep ? "deep" : "shallow"));

            // The default transition can carry executable content, which is an opaque delegate on
            // the transition; the source element is the only faithful form, exactly as it is for
            // an <initial> element below.
            if (ScxmlMetadata.NodeSources.TryGetValue(node, out var historySource) &&
                historySource.Elements().FirstOrDefault(e => e.Name.LocalName == "transition") is { } historyElement)
            {
                element.Add(Rename(historyElement, ns));
            }
            else if (node.HistoryDefault is { Targets.Count: > 0 } historyDefault)
            {
                element.Add(new XElement(ns + "transition",
                    new XAttribute("target", string.Join(' ', historyDefault.Targets.Select(DocumentIdOf)))));
            }

            return element;
        }

        // An <initial> element can carry executable content, which rides on the initial transition
        // as an opaque delegate with no written form of its own. The source element is therefore
        // the only faithful form; an `initial` attribute is written only when there was no
        // <initial> element (test412 raises an event from one, and loses it if this collapses to
        // the attribute).
        if (InitialElementOf(ScxmlMetadata.NodeSources.TryGetValue(node, out var nodeSource) ? nodeSource : null) is { } initialElement)
        {
            element.Add(Rename(initialElement, ns));
        }
        else if (InitialTargetOf(node) is { } initial)
        {
            element.Add(new XAttribute("initial", initial));
        }

        // A state-local <datamodel> is not reachable from any action or transition — it is read
        // by the context factory (early binding) or by a synthesized entry action (late binding),
        // both opaque to the core — so it is recovered from the node's source element. The root's
        // own <datamodel> is written by Serialize; this only ever runs for descendants.
        if (ScxmlMetadata.NodeSources.TryGetValue(node, out var source))
        {
            foreach (var datamodel in source.Elements().Where(e => e.Name.LocalName == "datamodel"))
            {
                element.Add(Rename(datamodel, ns));
            }
        }

        WriteBody(node, element, ns, placeholders);
        return element;
    }

    private static XElement WriteTransition(
        TransitionDefinition<ScxmlDataModel> transition,
        XNamespace ns,
        bool placeholders)
    {
        ScxmlMetadata.Transitions.TryGetValue(transition, out var info);

        var element = new XElement(ns + "transition");

        // Imported transitions match events *inside* their guard (SCXML token-prefix matching and
        // document-order selection cannot be expressed by an EventDescriptor), so the recorded
        // ScxmlTransitionInfo is the authority on both `event` and `cond`. Without it there is no
        // SCXML text for either: a Guard is an opaque delegate, and only a ByPattern descriptor has
        // a written form. Dropping them silently would change the machine — a guarded transition
        // would become unconditional and an evented one eventless — so it is refused by default.
        if (info is null && !placeholders)
        {
            if (transition.Guard is not null)
            {
                throw Unrepresentable(
                    transition,
                    "its guard is a delegate with no `cond` expression to write");
            }

            if (transition.Event is not null and not EventDescriptor.ByPattern)
            {
                throw Unrepresentable(
                    transition,
                    $"its {transition.Event.GetType().Name} event descriptor has no `event` attribute form");
            }
        }

        var events = info is not null
            ? string.Join(' ', info.EventTokens)
            : transition.Event is EventDescriptor.ByPattern pattern
                ? pattern.Pattern
                : null;

        if (!string.IsNullOrEmpty(events))
        {
            element.Add(new XAttribute("event", events));
        }

        if (info?.Cond is { } cond)
        {
            element.Add(new XAttribute("cond", cond));
        }

        if (transition.Targets.Count > 0)
        {
            element.Add(new XAttribute("target", string.Join(' ', transition.Targets.Select(DocumentIdOf))));

            // Imported transitions declare their SCXML domain outright (§3.13 eligibility is the
            // core's business), so the domain is what `type` is read back from.
            if (transition.Domain is TransitionDomain.Internal)
            {
                element.Add(new XAttribute("type", "internal"));
            }
        }

        foreach (var action in transition.Actions.Where(a => !ScxmlMetadata.IsSynthesized(a)))
        {
            element.Add(WriteActionContent(action, ns, placeholders));
        }

        return element;
    }

    /// <summary>The exception raised for a machine part that has no SCXML form.</summary>
    private static NotSupportedException Unrepresentable(
        TransitionDefinition<ScxmlDataModel> transition,
        string what) =>
        new($"The transition from '{transition.Source.Id}' cannot be serialized to SCXML because " +
            $"{what}. Only machines produced by ScxmlConverter.Parse carry the provenance needed " +
            "to write it back; pass ScxmlSerializationOptions { EmitPlaceholders = true } to emit " +
            "a structurally faithful document instead.");

    private static void WriteActions(
        IReadOnlyList<ActionDefinition<ScxmlDataModel>> actions,
        string containerName,
        XElement target,
        XNamespace ns,
        bool placeholders)
    {
        var emitted = actions.Where(a => !ScxmlMetadata.IsSynthesized(a)).ToList();
        if (emitted.Count == 0)
        {
            return;
        }

        // One container per action, not one for all of them: the core builder flattens a state's
        // <onentry>/<onexit> elements into a single action list, but SCXML §4.1 makes each of them
        // a separate block — an error only abandons the block it occurred in (test376/test378), so
        // merging them here would change what the emitted document does.
        foreach (var action in emitted)
        {
            var container = new XElement(ns + containerName);
            container.Add(WriteActionContent(action, ns, placeholders));
            target.Add(container);
        }
    }

    /// <summary>
    /// The executable content one action stands for: a block action's source is its container, so
    /// its children are emitted; any other action maps to the single element it came from.
    /// </summary>
    private static IEnumerable<XElement> WriteActionContent(
        ActionDefinition<ScxmlDataModel> action,
        XNamespace ns,
        bool placeholders)
    {
        if (!ScxmlMetadata.ActionSources.TryGetValue(action, out var source))
        {
            if (!placeholders)
            {
                throw new NotSupportedException(
                    $"The action '{action.Name ?? action.GetType().Name}' cannot be serialized to " +
                    "SCXML: it is a delegate with no source element, so there is no executable " +
                    "content to write. Only machines produced by ScxmlConverter.Parse carry that " +
                    "provenance; pass ScxmlSerializationOptions { EmitPlaceholders = true } to emit " +
                    "a <log> placeholder instead.");
            }

            return
            [
                new XElement(ns + "log",
                    new XAttribute("label", action.Name ?? action.GetType().Name),
                    new XAttribute("expr", $"'<{action.Name ?? action.GetType().Name}>'"))
            ];
        }

        return ScxmlMetadata.IsBlock(action)
            ? [.. source.Elements().Select(e => Rename(e, ns))]
            : [Rename(source, ns)];
    }

    /// <summary>Evented and eventless transitions, restored to their original document order.</summary>
    private static IEnumerable<TransitionDefinition<ScxmlDataModel>> Ordered(StateNode<ScxmlDataModel> node) =>
        node.Transitions
            .Concat(node.Always)
            .Select((t, fallback) => (Transition: t, Order:
                ScxmlMetadata.Transitions.TryGetValue(t, out var info) ? info.DocumentIndex : fallback))
            .OrderBy(pair => pair.Order)
            .Select(pair => pair.Transition);

    /// <summary>The <c>&lt;initial&gt;</c> child of a state's source element, if it had one.</summary>
    private static XElement? InitialElementOf(XElement? source) =>
        source?.Elements().FirstOrDefault(e => e.Name.LocalName == "initial");

    /// <summary>
    /// The <c>initial</c> attribute for a state: SCXML IDREFS, so every target of a multi-target
    /// initial transition is written, not just the first.
    /// </summary>
    private static string? InitialTargetOf(StateNode<ScxmlDataModel> node) =>
        node.Type is StateNodeType.Compound && node.InitialTransition is { Targets.Count: > 0 } initial
            ? string.Join(' ', initial.Targets.Select(DocumentIdOf))
            : null;

    /// <summary>
    /// History nodes cannot carry an explicit id through the core builder, so their node id is
    /// <c>{parent}.{key}</c>; the document form is just the key.
    /// </summary>
    private static string DocumentIdOf(StateNode<ScxmlDataModel> node) =>
        node.Type is StateNodeType.History ? node.Key : node.Id;

    private static string ElementNameOf(StateNodeType type) => type switch
    {
        StateNodeType.Parallel => "parallel",
        StateNodeType.Final => "final",
        StateNodeType.History => "history",
        _ => "state"
    };

    /// <summary>Deep-clones an element into the SCXML namespace (source docs may be namespace-free).</summary>
    private static XElement Rename(XElement element, XNamespace ns)
    {
        var clone = new XElement(ns + element.Name.LocalName);

        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
        {
            clone.Add(new XAttribute(attribute.Name.LocalName, attribute.Value));
        }

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child:
                    clone.Add(Rename(child, ns));
                    break;
                case XText text:
                    clone.Add(new XText(text.Value));
                    break;
            }
        }

        return clone;
    }
}
