using System.Xml.Linq;
using Jint;
using Jint.Native;
using Jint.Runtime;
using XState.Builder;

namespace XState.Scxml;

/// <summary>Turns an SCXML document into a <see cref="StateMachine{TContext}"/> over <see cref="ScxmlDataModel"/>.</summary>
/// <param name="resolveSrc">Resolver for <c>&lt;invoke src&gt;</c> / <c>&lt;data src&gt;</c>.</param>
/// <param name="invokeChain">
/// The chain of nested <c>&lt;invoke&gt;</c> sources that led to this document. Empty for a
/// top-level parse; see <see cref="ParseNested"/> for why it exists.
/// </param>
internal sealed class ScxmlParser(Func<string, string>? resolveSrc, IReadOnlyList<string>? invokeChain = null)
{
    /// <summary>
    /// How deep <c>&lt;invoke&gt;</c> nesting may go before the parse is rejected. Nested
    /// documents are resolved and parsed eagerly (recursively), so without a cap a cyclic
    /// <c>src</c> graph would overflow the stack — an uncatchable process kill.
    /// </summary>
    private const int MaxInvokeDepth = 64;

    /// <summary>
    /// How deeply states may nest before the parse is rejected. Every pass over the document
    /// (id assignment, the builder tree, metadata attachment) recurses once per level, as does
    /// the core's own resolver, so an arbitrarily deep document would overflow the stack — an
    /// uncatchable process kill. The limit is checked on the first walk, so no later pass ever
    /// sees a document deeper than this.
    /// </summary>
    private const int MaxStateDepth = 1024;

    private readonly IReadOnlyList<string> _invokeChain = invokeChain ?? [];

    private static readonly string[] StateElements = ["state", "parallel", "final", "history"];

    private static readonly string[] SupportedInvokeTypes =
    [
        "scxml",
        "http://www.w3.org/TR/scxml",
        "http://www.w3.org/TR/scxml/",
        "http://www.w3.org/TR/scxml/#SCXMLEventProcessor"
    ];

    private readonly Dictionary<XElement, string> _nodeIds = [];
    private readonly Dictionary<XElement, string> _nodeKeys = [];
    private readonly Dictionary<string, string> _targetIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _nodeElements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScxmlTransitionInfo>> _eventedTransitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScxmlTransitionInfo>> _eventlessTransitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ScxmlInvokeMetadata>> _invokes = new(StringComparer.Ordinal);

    private XElement _rootElement = null!;
    private string _machineId = "(machine)";
    private bool _lateBinding;

    public StateMachine<ScxmlDataModel> Parse(XDocument document)
    {
        _rootElement = document.Root ?? throw new ArgumentException("The SCXML document is empty.", nameof(document));

        if (_rootElement.Name.LocalName != "scxml")
        {
            throw new ArgumentException(
                $"Expected a root <scxml> element but found <{_rootElement.Name.LocalName}>.", nameof(document));
        }

        // "null" is accepted as a degenerate case of "ecmascript": it declares no data and only
        // has to support In() in conditions, which the Jint engine provides anyway.
        var datamodel = _rootElement.Attribute("datamodel")?.Value;
        if (datamodel is { Length: > 0 } and not ("ecmascript" or "null"))
        {
            throw new NotSupportedException(
                $"Only the \"ecmascript\" datamodel is supported (got \"{datamodel}\").");
        }

        _machineId = _rootElement.Attribute("name")?.Value is { Length: > 0 } name ? name : "(machine)";
        _lateBinding = _rootElement.Attribute("binding")?.Value == "late";

        AssignIds(_rootElement, _machineId);

        var builder = Machine.Create<ScxmlDataModel>(_machineId)
            .Context(BuildContextFactory(document))
            // SCXML §3.13: invocations are started at the end of the macrostep, for the states
            // still active then — so <invoke typeexpr/srcexpr/param> reads the datamodel as the
            // entering state's <onentry> left it, and a state entered and exited within one
            // macrostep never invokes at all.
            .DeferInvokes()
            // SCXML §3.12/§6.4.1: a platform error (error.communication from a failed <invoke>,
            // error.execution from bad executable content) that no transition selects is simply
            // discarded — the session keeps running. The core's default is to fault the session
            // on an unhandled ErrorActorEvent, which would kill conforming documents.
            .IgnoreUnhandledErrors();

        // Errors raised while initialising the datamodel have nowhere to go — the context factory
        // runs before the first microstep. This drain, first on the root's entry list, moves them
        // into the internal queue ahead of anything a state's own <onentry> raises.
        builder.Root(root => root.Entry(Synthesized(new ScriptAction<ScxmlDataModel>(args =>
            new ScriptResult<ScxmlDataModel>(args.Context, args.Context.DrainPendingInternalEvents(), null))
        { Name = "scxml.platform.drain" })));

        builder.Root(root => BuildState(_rootElement, root, _machineId, isRoot: true));

        var machine = builder.Build();
        AttachMetadata(machine);
        return machine;
    }

    // --- Pass 1: stable node ids ---

    private void AssignIds(XElement element, string nodeId, int depth = 0)
    {
        if (depth > MaxStateDepth)
        {
            throw new InvalidOperationException(
                $"SCXML state nesting exceeded the limit of {MaxStateDepth} levels at <" +
                $"{element.Name.LocalName}> \"{nodeId}\". Deeply nested documents are rejected " +
                "because every parse pass recurses once per level and would otherwise overflow " +
                "the stack.");
        }

        _nodeIds[element] = nodeId;
        _nodeElements[nodeId] = element;

        var counter = 0;
        foreach (var child in element.Elements())
        {
            var local = child.Name.LocalName;
            if (!StateElements.Contains(local, StringComparer.Ordinal))
            {
                continue;
            }

            var scxmlId = child.Attribute("id")?.Value;
            var key = scxmlId is { Length: > 0 } ? scxmlId : $"{local}{counter}";
            counter++;

            // History nodes cannot carry an explicit id through the core builder, so their node id
            // has to be spelled the way the core spells the alias it registers for an id-less node
            // — "{nearest explicitly-id'd ancestor}.{key}" — and stays '.'-joined. History states
            // never emit done events, so that dot is harmless.
            //
            // An id-less *state*, by contrast, does get its generated id installed as an explicit
            // id, and that id surfaces in event names (done.state.{id}) where '.' is SCXML's token
            // separator: an id-less child of "s1" called "s1.state0" would make the parent's own
            // <transition event="done.state.s1"> token-prefix-match the child's done event. ':'
            // is not a token separator, so it cannot collide.
            var childNodeId = local == "history"
                ? $"{nodeId}.{key}"
                : scxmlId is { Length: > 0 } ? scxmlId : $"{nodeId}:{key}";

            _nodeKeys[child] = key;

            if (scxmlId is { Length: > 0 })
            {
                _targetIds[scxmlId] = childNodeId;
            }

            AssignIds(child, childNodeId, depth + 1);
        }
    }

    /// <summary>An explicit-id target reference (<c>"#id"</c>) for a target named in the document.</summary>
    private string TargetRef(string scxmlId) =>
        "#" + (_targetIds.TryGetValue(scxmlId, out var resolved) ? resolved : scxmlId);

    // --- Pass 2: the builder tree ---

    private void BuildState(XElement element, StateBuilder<ScxmlDataModel> sb, string nodeId, bool isRoot)
    {
        if (!isRoot)
        {
            sb.Id(nodeId);
        }

        // Configuration bookkeeping for In() inside executable content: entered *before* this
        // state's own <onentry> (SCXML adds the state to the configuration first) and left
        // *after* its <onexit> (which still sees itself as active).
        sb.Entry(Synthesized(new ScriptAction<ScxmlDataModel>(args =>
        {
            args.Context.EnterState(nodeId);
            return new ScriptResult<ScxmlDataModel>(args.Context);
        })
        { Name = "scxml.enter" }));

        var initialTargets = SplitTokens(element.Attribute("initial")?.Value);
        XElement? initialContent = null;
        var invokeIndex = 0;
        var documentIndex = 0;

        // binding="late": this state's own <data> elements are assigned when it is entered.
        if (_lateBinding &&
            element.Elements().FirstOrDefault(e => e.Name.LocalName == "datamodel") is { } lateModel &&
            element != _rootElement)
        {
            var lateData = lateModel.Elements().Where(e => e.Name.LocalName == "data").ToList();
            var resolver = resolveSrc;

            sb.Entry(Synthesized(new ScriptAction<ScxmlDataModel>(args =>
            {
                var errors = new List<MachineEvent>();
                foreach (var data in lateData)
                {
                    InitializeData(data, args.Context, errors, resolver);
                }

                return new ScriptResult<ScxmlDataModel>(args.Context.Mutated(), errors, null);
            })
            { Name = "scxml.datamodel.late" }));
        }

        foreach (var child in element.Elements())
        {
            var local = child.Name.LocalName;
            var childId = _nodeIds.TryGetValue(child, out var id) ? id : null;
            var childKey = _nodeKeys.TryGetValue(child, out var key) ? key : null;

            switch (local)
            {
                case "state":
                    sb.State(childKey!, s => BuildState(child, s, childId!, isRoot: false));
                    break;

                case "parallel":
                    sb.Parallel(childKey!, p =>
                    {
                        p.AsParallel();
                        BuildState(child, p, childId!, isRoot: false);
                    });
                    break;

                case "final":
                    sb.Final(childKey!, f =>
                    {
                        BuildState(child, f, childId!, isRoot: false);
                        ApplyDoneData(child, f, childId!);
                    });
                    break;

                case "history":
                {
                    var historyType = child.Attribute("type")?.Value == "deep"
                        ? HistoryType.Deep
                        : HistoryType.Shallow;

                    var defaultTransition = child.Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "transition");
                    var defaultTargets = SplitTokens(defaultTransition?.Attribute("target")?.Value)
                        .Select(TargetRef)
                        .ToArray();

                    // The default transition is *content*, not just a target list: SCXML §3.6
                    // runs it when the history state is entered with nothing recorded, after the
                    // parent's own <initial> content. The core keys it by the history node's
                    // parent and runs it in exactly that order.
                    var defaultContent = ScxmlExecutableContent.CompileBlock(defaultTransition, resolveSrc);

                    if (defaultTargets.Length == 0 && defaultContent is null)
                    {
                        sb.History(childKey!, historyType);
                    }
                    else
                    {
                        sb.History(childKey!, historyType, configure: t =>
                        {
                            if (defaultTargets.Length > 0)
                            {
                                t.Target(defaultTargets);
                            }

                            if (defaultContent is not null)
                            {
                                t.Do(defaultContent);
                            }
                        });
                    }

                    break;
                }

                case "transition":
                    CompileTransition(child, sb, nodeId, documentIndex);
                    break;

                case "onentry":
                    if (ScxmlExecutableContent.CompileBlock(child, resolveSrc) is { } entryBlock)
                    {
                        sb.Entry(entryBlock);
                    }
                    break;

                case "onexit":
                    if (ScxmlExecutableContent.CompileBlock(child, resolveSrc) is { } exitBlock)
                    {
                        sb.Exit(exitBlock);
                    }
                    break;

                case "initial":
                {
                    var transition = child.Elements().FirstOrDefault(e => e.Name.LocalName == "transition");
                    if (transition is null)
                    {
                        break;
                    }

                    initialTargets = SplitTokens(transition.Attribute("target")?.Value);
                    initialContent = transition;
                    break;
                }

                case "invoke":
                    CompileInvoke(child, sb, nodeId, invokeIndex++);
                    break;

                case "datamodel":
                case "donedata":
                case "script":
                    // <datamodel>/<script> are hoisted into the context factory (early binding);
                    // <donedata> is applied by the <final> branch above.
                    break;
            }

            documentIndex++;
        }

        // The initial transition, with its targets and its executable content: the core runs that
        // content only on *default* entry, after the state's own <onentry> and before the entered
        // children's (SCXML §3.3/test412), and takes any number of targets — including deep and
        // cross-region ones named by id — so no transient pseudo-state is needed.
        if (initialTargets.Length > 0)
        {
            var targets = initialTargets.Select(TargetRef).ToArray();
            var content = initialContent is null
                ? null
                : ScxmlExecutableContent.CompileBlock(initialContent, resolveSrc);

            sb.Initial(t =>
            {
                t.Target(targets);
                if (content is not null)
                {
                    t.Do(content);
                }
            });
        }

        sb.Exit(Synthesized(new ScriptAction<ScxmlDataModel>(args =>
        {
            args.Context.ExitState(nodeId);
            return new ScriptResult<ScxmlDataModel>(args.Context);
        })
        { Name = "scxml.exit" }));
    }

    private static ActionDefinition<ScxmlDataModel> Synthesized(ActionDefinition<ScxmlDataModel> action)
    {
        ScxmlMetadata.MarkSynthesized(action);
        return action;
    }

    // --- Transitions ---

    private void CompileTransition(
        XElement element,
        StateBuilder<ScxmlDataModel> sb,
        string nodeId,
        int documentIndex)
    {
        var tokens = SplitTokens(element.Attribute("event")?.Value);
        var condAttribute = element.Attribute("cond")?.Value;
        var cond = string.IsNullOrWhiteSpace(condAttribute) ? null : condAttribute;
        var targetTokens = SplitTokens(element.Attribute("target")?.Value);
        var targets = targetTokens.Select(TargetRef).ToArray();

        // SCXML's default transition type is "external". The core's TransitionDomain override is
        // literal SCXML getTransitionDomain, §3.13 restriction included — "internal" only takes
        // effect when the source is compound and every target is a proper descendant, and falls
        // back to the external domain otherwise — so the domain is simply declared here.
        var isInternal = element.Attribute("type")?.Value == "internal";

        var block = ScxmlExecutableContent.CompileBlock(element, resolveSrc);
        var guard = MakeGuard(tokens, cond);

        void Configure(TransitionBuilder<ScxmlDataModel, MachineEvent> t)
        {
            if (targets.Length > 0)
            {
                t.Target(targets);
                if (isInternal)
                {
                    t.Internal();
                }
                else
                {
                    t.External();
                }
            }

            if (guard is not null)
            {
                t.Guard(guard);
            }

            if (block is not null)
            {
                t.Do(block);
            }
        }

        var info = new ScxmlTransitionInfo
        {
            Source = element,
            EventTokens = tokens,
            Cond = cond,
            DocumentIndex = documentIndex
        };

        if (tokens.Length == 0)
        {
            sb.Always(Configure);
            Bucket(_eventlessTransitions, nodeId).Add(info);
        }
        else
        {
            // Event matching is done inside the guard: the core's ByPattern descriptor cannot
            // express SCXML token-prefix matching from outside the XState assembly, and doing it
            // here additionally preserves SCXML's document-order selection.
            sb.On("*", Configure);
            Bucket(_eventedTransitions, nodeId).Add(info);
        }
    }

    private static Guard<ScxmlDataModel>? MakeGuard(string[] tokens, string? cond)
    {
        if (tokens.Length == 0 && cond is null)
        {
            return null;
        }

        return args =>
        {
            if (tokens.Length > 0)
            {
                var eventName = ScxmlEventNaming.NameOf(args.Event);
                if (!tokens.Any(token => ScxmlEventNaming.Matches(token, eventName)))
                {
                    return false;
                }
            }

            if (cond is null)
            {
                return true;
            }

            args.Context.Bind(args.Event, args.In);

            if (args.Context.TryEvaluateValue(cond, out var value, out var error))
            {
                return TypeConverter.ToBoolean(value);
            }

            // SCXML §5.9: a failed cond evaluates to false and raises error.execution. Guards
            // are pure, so the error is parked for the harness to drain.
            args.Context.PendingInternalEvents.Add(ScxmlEvent.ExecutionError(error));
            return false;
        };
    }

    // --- <final><donedata> ---

    /// <summary>
    /// <c>&lt;donedata&gt;</c> is evaluated by a synthesized <em>entry</em> action on the final
    /// state rather than by the core's output resolver. The core resolves output while it is
    /// already queueing <c>done.state.*</c>, which is too late for the <c>error.execution</c> that
    /// a bad <c>&lt;param&gt;</c> must raise <em>before</em> the done event (SCXML §5.7).
    /// </summary>
    private static void ApplyDoneData(XElement finalElement, StateBuilder<ScxmlDataModel> sb, string nodeId)
    {
        var donedata = finalElement.Elements().FirstOrDefault(e => e.Name.LocalName == "donedata");
        if (donedata is null)
        {
            return;
        }

        sb.Entry(Synthesized(new ScriptAction<ScxmlDataModel>(args =>
        {
            var sink = new ScxmlExecutionSink(args.Context);
            sink.Context.Bind(args.Event, null);

            sink.Context.SetDoneData(
                nodeId,
                ScxmlExecutableContent.TryBuildPayload(donedata, sink, out var data) ? data : null);

            return new ScriptResult<ScxmlDataModel>(sink.Context, sink.Raised, sink.Effects);
        })
        { Name = "scxml.donedata" }));

        sb.Output(args => args.Context.GetDoneData(nodeId));
    }

    /// <summary>
    /// The <c>&lt;param&gt;</c>/<c>namelist</c>/<c>&lt;content&gt;</c> payload of an element.
    /// </summary>
    /// <param name="abortOnFailure">
    /// SCXML §6.4.1: when the evaluation of an <c>&lt;invoke&gt;</c>'s arguments fails, the
    /// processor raises <c>error.execution</c> and <em>does not start the child</em>. The parked
    /// error is what the core delivers; <see cref="SpawnAbortedException"/> is what cancels the
    /// spawn (test554).
    /// </param>
    private static object? EvaluatePayload(
        XElement element,
        ActionArgs<ScxmlDataModel> args,
        bool allowContent,
        bool abortOnFailure = false)
    {
        var sink = new ScxmlExecutionSink(args.Context);
        sink.Context.Bind(args.Event, null);

        if (ScxmlExecutableContent.TryBuildPayload(element, sink, out var data, allowContent))
        {
            return data;
        }

        args.Context.PendingInternalEvents.AddRange(sink.Raised);

        return abortOnFailure
            ? throw new SpawnAbortedException(
                $"<{element.Name.LocalName}> could not evaluate its arguments, so the invocation " +
                "is cancelled (SCXML §6.4.1).")
            : null;
    }

    // --- <invoke> ---

    private void CompileInvoke(
        XElement element,
        StateBuilder<ScxmlDataModel> sb,
        string nodeId,
        int invokeIndex)
    {
        var content = element.Elements().FirstOrDefault(e => e.Name.LocalName == "content");
        var nested = content?.Elements().FirstOrDefault(e => e.Name.LocalName == "scxml");
        var srcExpr = element.Attribute("srcexpr")?.Value;

        // SCXML §6.4.1: a generated invoke id must have the form "stateid.platformid".
        var explicitId = element.Attribute("id")?.Value;
        var effectiveId = explicitId is { Length: > 0 }
            ? explicitId
            : $"{nodeId}.invocation[{invokeIndex}]";

        IActorLogic childLogic;
        if (srcExpr is { Length: > 0 })
        {
            // The document to run is only known once the invocation starts. Nothing recurses at
            // parse time here — the chain is carried so that any eager <invoke src> inside the
            // resolved document keeps counting from this document's depth.
            childLogic = new ScxmlDeferredMachine(
                effectiveId,
                resolveSrc,
                ScxmlInvokeChain.Extend(_invokeChain, $"srcexpr({srcExpr})", MaxInvokeDepth, detectCycle: false));
        }
        else if (nested is not null)
        {
            childLogic = ParseNested(
                _ => new XDocument(new XElement(nested)),
                $"<content><scxml> in {nodeId}",
                detectCycle: false);
        }
        else if (element.Attribute("src")?.Value is { Length: > 0 } src)
        {
            if (resolveSrc is null)
            {
                throw new NotSupportedException(
                    $"<invoke src=\"{src}\"> requires a source resolver (pass one to ScxmlConverter.Parse).");
            }

            childLogic = ParseNested(
                resolver => XDocument.Parse(resolver!(src)),
                src,
                detectCycle: true);
        }
        else
        {
            throw new NotSupportedException("<invoke> requires either 'src' or inline <content><scxml>.");
        }

        // SCXML §6.4: an unsupported <invoke type> is a *runtime* error on the invoking state, not
        // a malformed document — the document stays loadable and the session keeps running. Both
        // the literal `type` and the run-time `typeexpr` therefore report error.execution when the
        // invocation starts, rather than being rejected at parse time.
        var declaredType = element.Attribute("type")?.Value;
        var typeExpr = element.Attribute("typeexpr")?.Value;

        var autoForward = element.Attribute("autoforward")?.Value == "true";
        var finalize = element.Elements().FirstOrDefault(e => e.Name.LocalName == "finalize");

        sb.Invoke(
            childLogic,
            effectiveId,
            args =>
            {
                if (declaredType is { Length: > 0 } &&
                    !SupportedInvokeTypes.Contains(declaredType, StringComparer.Ordinal))
                {
                    args.Context.PendingInternalEvents.Add(ScxmlEvent.ExecutionError(
                        $"Only SCXML <invoke> is supported (got type \"{declaredType}\")."));
                }

                if (typeExpr is { Length: > 0 } &&
                    args.Context.TryEvaluateValue(typeExpr, out var resolvedType, out _) &&
                    !SupportedInvokeTypes.Contains(TypeConverter.ToString(resolvedType), StringComparer.Ordinal))
                {
                    args.Context.PendingInternalEvents.Add(ScxmlEvent.ExecutionError(
                        $"Only SCXML <invoke> is supported (got type \"{TypeConverter.ToString(resolvedType)}\")."));
                }

                var payload = EvaluatePayload(element, args, allowContent: false, abortOnFailure: true);

                if (srcExpr is not { Length: > 0 })
                {
                    return payload;
                }

                args.Context.TryEvaluateValue(srcExpr, out var resolvedSrc, out _);
                return new ScxmlDeferredInput(
                    resolvedSrc.IsUndefined() ? null : TypeConverter.ToString(resolvedSrc),
                    payload);
            },
            autoForward: autoForward,
            onExternalEvent: CompileFinalize(finalize, effectiveId, resolveSrc));

        if (element.Attribute("idlocation")?.Value is { Length: > 0 } idLocation)
        {
            var assign = new ScriptAction<ScxmlDataModel>(args =>
            {
                var sink = new ScxmlExecutionSink(args.Context);
                sink.Context.Bind(args.Event, null);

                if (!sink.Context.TryAssign(
                        idLocation,
                        JsValue.FromObject(sink.Context.Engine, effectiveId),
                        out var error))
                {
                    sink.Fail(error!);
                }
                else
                {
                    sink.Mutated();
                }

                return new ScriptResult<ScxmlDataModel>(sink.Context, sink.Raised, sink.Effects);
            })
            { Name = "scxml.invoke.idlocation" };

            ScxmlMetadata.MarkSynthesized(assign);
            sb.Entry(assign);
        }

        Bucket(_invokes, nodeId).Add(new ScxmlInvokeMetadata
        {
            Source = element,
            AutoForward = autoForward,
            Finalize = finalize,
            IdLocation = element.Attribute("idlocation")?.Value
        });
    }

    /// <summary>
    /// Compiles an <c>&lt;invoke&gt;</c>'s <c>&lt;finalize&gt;</c> into the core's pre-selection
    /// hook (SCXML §3.13 step 2): for every external event delivered while the child is running,
    /// the content runs against the <em>parent's</em> datamodel before transitions are selected,
    /// so a <c>cond</c> already sees what it assigned.
    /// <para>
    /// Only events that came from <em>this</em> invocation qualify — SCXML identifies them by
    /// <c>_event.invokeid</c> (test234: a sibling region's <c>&lt;finalize&gt;</c> must not run) —
    /// so the driving runtime has to stamp child-to-parent events with the child's id, as
    /// <c>DeterministicInterpreter.DecorateChildEvent</c> does. An event carrying no invokeid
    /// runs no finalize at all.
    /// </para>
    /// </summary>
    private static Func<ActionArgs<ScxmlDataModel>, ScriptResult<ScxmlDataModel>?>? CompileFinalize(
        XElement? finalize,
        string invokeId,
        Func<string, string>? resolveSrc)
    {
        if (finalize is null || !finalize.Elements().Any())
        {
            return null;
        }

        return args =>
        {
            if (args.Event is not ScxmlEvent { InvokeId: { } origin } ||
                !string.Equals(origin, invokeId, StringComparison.Ordinal))
            {
                return null;
            }

            var sink = new ScxmlExecutionSink(args.Context, resolveSrc);
            sink.Context.Bind(args.Event, args.In);
            ScxmlExecutableContent.ExecuteAll(finalize.Elements(), sink);

            return new ScriptResult<ScxmlDataModel>(sink.Context, sink.Raised, sink.Effects);
        };
    }

    /// <summary>
    /// Parses a document invoked by this one, one level deeper in the invoke chain. The document
    /// is produced lazily so the cycle/depth guard runs <em>before</em> the source is fetched:
    /// a document that invokes itself (directly or through a ring of <c>src</c>s) would otherwise
    /// recurse until the stack overflows, which no caller can catch.
    /// </summary>
    private StateMachine<ScxmlDataModel> ParseNested(
        Func<Func<string, string>?, XDocument> load,
        string label,
        bool detectCycle)
    {
        var chain = ScxmlInvokeChain.Extend(_invokeChain, label, MaxInvokeDepth, detectCycle);
        return new ScxmlParser(resolveSrc, chain).Parse(load(resolveSrc));
    }

    // --- Datamodel / context factory ---

    /// <summary>Evaluates one <c>&lt;data&gt;</c> element into the datamodel.</summary>
    private static void InitializeData(
        XElement data,
        ScxmlDataModel context,
        List<MachineEvent> errors,
        Func<string, string>? resolver)
    {
        var id = data.Attribute("id")?.Value;
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        if (data.Attribute("src")?.Value is { Length: > 0 } src)
        {
            try
            {
                context.Declare(
                    id,
                    resolver is null
                        ? throw new NotSupportedException(
                            $"<data id=\"{id}\" src=\"{src}\"> requires a source resolver.")
                        : context.ParseLiteral(resolver(src)));
            }
            catch (Exception ex)
            {
                errors.Add(ScxmlEvent.ExecutionError(ex.Message));
                context.Declare(id, JsValue.Undefined);
            }

            return;
        }

        if (data.Attribute("expr")?.Value is { } expr)
        {
            if (context.TryEvaluateValue(expr, out var value, out var error))
            {
                context.Declare(id, value);
            }
            else
            {
                errors.Add(ScxmlEvent.ExecutionError(error));
                context.Declare(id, JsValue.Undefined);
            }

            return;
        }

        // A <data> body is a literal (JSON, else a space-normalized string), not an expression —
        // but an unparenthesized value expression is accepted as a fallback because the corpus
        // (and much SCXML in the wild) relies on it.
        var body = ScxmlExecutableContent.InlineText(data);

        if (string.IsNullOrWhiteSpace(body))
        {
            context.Declare(id, JsValue.Undefined);
        }
        else if (context.TryEvaluateValue(body, out var literal, out _))
        {
            context.Declare(id, literal);
        }
        else
        {
            context.Declare(id, context.ParseLiteral(body));
        }
    }

    /// <summary>
    /// The code a top-level <c>&lt;script&gt;</c> stands for: its body, or the document named by
    /// <c>src</c>, fetched through the same resolver <c>&lt;data src&gt;</c> uses. SCXML §5.8 makes
    /// an unfetchable script a document-level error, so this throws and the parse is rejected.
    /// </summary>
    private string LoadScript(XElement script)
    {
        if (script.Attribute("src")?.Value is not { Length: > 0 } src)
        {
            return ScxmlExecutableContent.InlineText(script);
        }

        if (resolveSrc is null)
        {
            throw new NotSupportedException(
                $"<script src=\"{src}\"> requires a source resolver (pass one to ScxmlConverter.Parse).");
        }

        try
        {
            return resolveSrc(src);
        }
        catch (Exception ex)
        {
            throw new NotSupportedException(
                $"<script src=\"{src}\"> could not be fetched, so the document is rejected: {ex.Message}",
                ex);
        }
    }

    private Func<object?, ScxmlDataModel> BuildContextFactory(XDocument document)
    {
        // binding="early" (the default): every <data> in the document is initialised at startup —
        // but only this document's own, not those of an inline <invoke><content><scxml> child,
        // which owns a separate datamodel and would otherwise clobber same-named variables.
        var dataElements = document
            .Descendants()
            .Where(e => e.Name.LocalName == "data" &&
                        e.Parent?.Name.LocalName == "datamodel" &&
                        e.Ancestors().FirstOrDefault(a => a.Name.LocalName == "scxml") == _rootElement)
            .ToList();

        var declared = dataElements
            .Select(e => e.Attribute("id")?.Value)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.Ordinal)!;

        // binding="late" defers everything below the root <datamodel> to state entry.
        var deferred = _lateBinding
            ? dataElements.Where(e => e.Parent?.Parent != _rootElement).ToHashSet()
            : [];

        // Top-level <script src> is fetched now, at document-load time: SCXML §5.8 requires a
        // document whose script cannot be downloaded to be *rejected*, so a failure here throws
        // out of Parse rather than becoming a runtime error.
        var scripts = _rootElement
            .Elements()
            .Where(e => e.Name.LocalName == "script")
            .Select(LoadScript)
            .Where(code => code.Length > 0)
            .ToList();

        var machineName = _machineId;
        var resolver = resolveSrc;

        return input =>
        {
            var session = new ScxmlSession(machineName);
            var context = new ScxmlDataModel(session, 0);

            foreach (var data in dataElements)
            {
                if (deferred.Contains(data))
                {
                    // binding="late": created now, assigned when its state is entered.
                    if (data.Attribute("id")?.Value is { Length: > 0 } lateId)
                    {
                        context.Declare(lateId, JsValue.Undefined);
                    }

                    continue;
                }

                InitializeData(data, context, session.PendingInternalEvents, resolver);
            }

            foreach (var code in scripts)
            {
                if (!context.TryExecute(code, out var error))
                {
                    session.PendingInternalEvents.Add(ScxmlEvent.ExecutionError(error));
                }
            }

            // <invoke> input (<param>/namelist) overrides the declared data, per SCXML §6.4 —
            // and only that: a name with no matching <data> in this document stays unbound.
            if (input is IReadOnlyDictionary<string, object?> parameters)
            {
                foreach (var (key, value) in parameters)
                {
                    if (declared.Contains(key))
                    {
                        context.Declare(key, ScxmlValue.ToJs(session.Engine, value));
                    }
                }
            }

            return context;
        };
    }

    // --- Metadata wiring (transitions/invokes are created by the core resolver) ---

    private void AttachMetadata(StateMachine<ScxmlDataModel> machine)
    {
        ScxmlMetadata.MachineSources.AddOrUpdate(machine, _rootElement);
        Walk(machine.Root);

        void Walk(StateNode<ScxmlDataModel> node)
        {
            // A history node's core id is the full path from the machine root, while the parser
            // keys it by "{parent id}.{key}" (history states cannot carry an explicit id through
            // the builder). Every other node had its parser id installed as its explicit id.
            var parserId = node.Type is StateNodeType.History && node.Parent is { } historyParent
                ? $"{historyParent.Id}.{node.Key}"
                : node.Id;

            if (_nodeElements.TryGetValue(parserId, out var element))
            {
                ScxmlMetadata.NodeSources.AddOrUpdate(node, element);
            }

            Zip(_eventedTransitions, node.Id, node.Transitions);
            Zip(_eventlessTransitions, node.Id, node.Always);

            if (_invokes.TryGetValue(node.Id, out var invokeMetadata))
            {
                for (var i = 0; i < Math.Min(invokeMetadata.Count, node.Invokes.Count); i++)
                {
                    ScxmlMetadata.Invokes.AddOrUpdate(node.Invokes[i], invokeMetadata[i]);
                }
            }

            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }

        static void Zip(
            Dictionary<string, List<ScxmlTransitionInfo>> source,
            string nodeId,
            IReadOnlyList<TransitionDefinition<ScxmlDataModel>> transitions)
        {
            if (!source.TryGetValue(nodeId, out var infos))
            {
                return;
            }

            for (var i = 0; i < Math.Min(infos.Count, transitions.Count); i++)
            {
                ScxmlMetadata.Transitions.AddOrUpdate(transitions[i], infos[i]);
            }
        }
    }

    // --- Helpers ---

    private static List<T> Bucket<T>(Dictionary<string, List<T>> map, string key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        return list;
    }

    internal static string[] SplitTokens(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// Guards the eager, recursive parse of nested <c>&lt;invoke&gt;</c> documents against cyclic and
/// pathologically deep source graphs, which would otherwise kill the process with an uncatchable
/// <c>StackOverflowException</c>.
/// </summary>
internal static class ScxmlInvokeChain
{
    /// <summary>
    /// Appends one nesting level, throwing a descriptive exception naming the whole chain when
    /// <paramref name="label"/> repeats (a cycle) or the chain exceeds <paramref name="maxDepth"/>.
    /// </summary>
    /// <param name="detectCycle">
    /// Only a resolvable <c>src</c> identifies a document: inline <c>&lt;content&gt;</c> and
    /// <c>srcexpr</c> labels can legitimately repeat, so they are depth-limited but not
    /// cycle-checked.
    /// </param>
    public static IReadOnlyList<string> Extend(
        IReadOnlyList<string> chain,
        string label,
        int maxDepth,
        bool detectCycle)
    {
        var extended = new List<string>(chain.Count + 1);
        extended.AddRange(chain);
        extended.Add(label);

        if (detectCycle && chain.Contains(label, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"<invoke> source cycle detected: {Describe(extended)}. A document cannot invoke " +
                "itself, directly or transitively, because invoked documents are parsed eagerly.");
        }

        if (extended.Count > maxDepth)
        {
            throw new InvalidOperationException(
                $"<invoke> nesting exceeded the limit of {maxDepth} documents: {Describe(extended)}.");
        }

        return extended;
    }

    private static string Describe(IReadOnlyList<string> chain) => string.Join(" -> ", chain);
}
