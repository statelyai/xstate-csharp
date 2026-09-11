using System.Globalization;
using System.Xml.Linq;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace XState.Scxml;

/// <summary>Accumulates the results of one executable-content block.</summary>
internal sealed class ScxmlExecutionSink(ScxmlDataModel context, Func<string, string>? resolveSrc = null)
{
    public ScxmlDataModel Context { get; set; } = context;

    /// <summary>Resolver for <c>&lt;script src&gt;</c>; the same one <c>&lt;data src&gt;</c> uses.</summary>
    public Func<string, string>? ResolveSrc { get; } = resolveSrc;
    public List<MachineEvent> Raised { get; } = [];
    public List<Effect> Effects { get; } = [];

    /// <summary>Set once an element failed; SCXML §4.1 abandons the rest of the block.</summary>
    public bool Failed { get; private set; }

    public bool Fail(string message, string? sendId = null)
    {
        Raised.Add(ScxmlEvent.ExecutionError(message) with { SendId = sendId });
        Failed = true;
        return false;
    }

    /// <summary>Records a non-fatal error (SCXML §5.9.1: a bad <c>cond</c> is just <c>false</c>).</summary>
    public void Warn(string message) => Raised.Add(ScxmlEvent.ExecutionError(message));

    /// <summary>
    /// SCXML §6.2.4: an event the I/O processor cannot deliver raises <c>error.communication</c>.
    /// Unlike <see cref="Fail"/> this does <em>not</em> abandon the block — delivery failure is not
    /// a failure of the <c>&lt;send&gt;</c> element itself, and the corpus (test521) relies on the
    /// elements after an undeliverable <c>&lt;send&gt;</c> still running.
    /// </summary>
    public bool Undeliverable(string message, string? sendId = null)
    {
        Raised.Add(ScxmlEvent.CommunicationError(message) with { SendId = sendId });
        return true;
    }

    public void Mutated() => Context = Context.Mutated();
}

/// <summary>
/// Compiles SCXML executable content (<c>&lt;onentry&gt;</c>, <c>&lt;onexit&gt;</c> and
/// transition bodies) into core <see cref="ActionDefinition{TContext}"/>s.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="ScriptAction{TContext}"/> per <em>block</em> — an <c>&lt;onentry&gt;</c>,
/// <c>&lt;onexit&gt;</c> or <c>&lt;transition&gt;</c> body — not one per element. SCXML §4.1
/// requires that when an element of executable content fails, the <b>remaining elements of that
/// block are skipped</b>, which a list of independent core actions cannot express: the core runs
/// every action it is given. Compiling the block as a unit also keeps <c>&lt;assign&gt;</c>,
/// <c>&lt;raise&gt;</c> and <c>&lt;send&gt;</c> effects in exact document order.
/// </para>
/// <para>
/// Each block first drains <see cref="ScxmlDataModel.PendingInternalEvents"/> into its own raised
/// events, so platform errors produced by pure contexts (a failed transition <c>cond</c>, a failed
/// <c>&lt;data&gt;</c> initializer) reach the internal queue at the first opportunity rather than
/// only at the end of the macrostep.
/// </para>
/// </remarks>
internal static class ScxmlExecutableContent
{
    private static readonly string[] SupportedSendTypes =
    [
        "scxml",
        "http://www.w3.org/TR/scxml/",
        ScxmlEvent.ScxmlEventProcessor
    ];

    /// <summary>
    /// Compiles the children of an <c>&lt;onentry&gt;</c>-like container into one action, or
    /// <see langword="null"/> when the container is absent or empty.
    /// </summary>
    public static ActionDefinition<ScxmlDataModel>? CompileBlock(
        XElement? container,
        Func<string, string>? resolveSrc = null)
    {
        if (container is null || !container.Elements().Any())
        {
            return null;
        }

        var action = new ScriptAction<ScxmlDataModel>(args =>
        {
            var sink = new ScxmlExecutionSink(args.Context, resolveSrc);
            sink.Context.Bind(args.Event, null);
            sink.Raised.AddRange(sink.Context.DrainPendingInternalEvents());
            ExecuteAll(container.Elements(), sink);
            return new ScriptResult<ScxmlDataModel>(sink.Context, sink.Raised, sink.Effects);
        })
        { Name = "scxml.block" };

        ScxmlMetadata.Record(action, container);
        ScxmlMetadata.MarkBlock(action);
        return action;
    }

    // --- Interpretation ---

    /// <summary>Executes a run of executable-content elements, stopping at the first failure.</summary>
    public static void ExecuteAll(IEnumerable<XElement> elements, ScxmlExecutionSink sink)
    {
        foreach (var element in elements)
        {
            if (sink.Failed || !Execute(element, sink))
            {
                return;
            }
        }
    }

    public static bool Execute(XElement element, ScxmlExecutionSink sink) => element.Name.LocalName switch
    {
        "raise" => ExecuteRaise(element, sink),
        "assign" => ExecuteAssign(element, sink),
        "script" => ExecuteScript(element, sink),
        "log" => ExecuteLog(element, sink),
        "send" => ExecuteSend(element, sink),
        "cancel" => ExecuteCancel(element, sink),
        "if" => ExecuteIf(element, sink),
        "foreach" => ExecuteForeach(element, sink),
        _ => sink.Fail($"Unsupported executable content element <{element.Name.LocalName}>.")
    };

    private static bool ExecuteRaise(XElement element, ScxmlExecutionSink sink)
    {
        sink.Raised.Add(ScxmlEvent.Raised(element.Attribute("event")?.Value ?? string.Empty));
        return true;
    }

    private static bool ExecuteAssign(XElement element, ScxmlExecutionSink sink)
    {
        var location = element.Attribute("location")?.Value;
        if (string.IsNullOrWhiteSpace(location))
        {
            return sink.Fail("<assign> requires a 'location' attribute.");
        }

        var expr = element.Attribute("expr")?.Value ?? InlineText(element);
        if (string.IsNullOrWhiteSpace(expr))
        {
            return sink.Fail($"<assign location=\"{location}\"> has no value expression.");
        }

        if (!sink.Context.TryEvaluateValue(expr, out var value, out var evalError))
        {
            return sink.Fail(evalError!);
        }

        if (!sink.Context.TryAssign(location, value, out var assignError))
        {
            return sink.Fail(assignError!);
        }

        sink.Mutated();
        return true;
    }

    private static bool ExecuteScript(XElement element, ScxmlExecutionSink sink)
    {
        // SCXML §5.8: <script src> names a document to fetch. There is no network here, so it goes
        // through the same resolver <data src>/<invoke src> use; a script that cannot be fetched is
        // an error in this element, which §4.1 turns into error.execution and abandons the block.
        string code;
        if (element.Attribute("src")?.Value is { Length: > 0 } src)
        {
            if (sink.ResolveSrc is null)
            {
                return sink.Fail(
                    $"<script src=\"{src}\"> requires a source resolver (pass one to ScxmlConverter.Parse).");
            }

            try
            {
                code = sink.ResolveSrc(src);
            }
            catch (Exception ex)
            {
                return sink.Fail($"<script src=\"{src}\"> could not be fetched: {ex.Message}");
            }
        }
        else
        {
            code = InlineText(element);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return true;
        }

        if (!sink.Context.TryExecute(code, out var error))
        {
            return sink.Fail(error!);
        }

        sink.Mutated();
        return true;
    }

    private static bool ExecuteLog(XElement element, ScxmlExecutionSink sink)
    {
        var expr = element.Attribute("expr")?.Value;
        object? message = null;

        if (!string.IsNullOrWhiteSpace(expr))
        {
            if (!sink.Context.TryEvaluate(expr, out var value, out var error))
            {
                return sink.Fail(error!);
            }

            message = ScxmlValue.FromJs(value);
        }

        sink.Effects.Add(new ActionEffect(
            "xstate.log",
            new LogParams(message, element.Attribute("label")?.Value),
            () => { }));
        return true;
    }

    private static bool ExecuteCancel(XElement element, ScxmlExecutionSink sink)
    {
        var sendId = element.Attribute("sendid")?.Value;

        if (sendId is null)
        {
            var expr = element.Attribute("sendidexpr")?.Value;
            if (string.IsNullOrWhiteSpace(expr))
            {
                return sink.Fail("<cancel> requires 'sendid' or 'sendidexpr'.");
            }

            if (!sink.Context.TryEvaluate(expr, out var value, out var error))
            {
                return sink.Fail(error!);
            }

            sendId = value.IsUndefined() ? string.Empty : TypeConverter.ToString(value);
        }

        sink.Effects.Add(new CancelEffect(sendId));
        return true;
    }

    private static bool ExecuteIf(XElement element, ScxmlExecutionSink sink)
    {
        var executing = EvaluateCondition(element.Attribute("cond")?.Value, sink);
        var branchTaken = executing;

        foreach (var child in element.Elements())
        {
            var local = child.Name.LocalName;
            if (local is "elseif" or "else")
            {
                if (branchTaken)
                {
                    return true;
                }

                executing = EvaluateCondition(local == "else" ? null : child.Attribute("cond")?.Value, sink);
                branchTaken = executing;
                continue;
            }

            if (executing && !Execute(child, sink))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// SCXML §5.9.1: a missing or blank <c>cond</c> is <see langword="true"/>, and a <c>cond</c>
    /// that cannot be evaluated is <see langword="false"/> plus an <c>error.execution</c> — it does
    /// <em>not</em> abandon the surrounding block.
    /// </summary>
    private static bool EvaluateCondition(string? cond, ScxmlExecutionSink sink)
    {
        if (string.IsNullOrWhiteSpace(cond))
        {
            return true;
        }

        if (!sink.Context.TryEvaluateValue(cond, out var value, out var error))
        {
            sink.Warn(error!);
            return false;
        }

        return TypeConverter.ToBoolean(value);
    }

    private static bool ExecuteForeach(XElement element, ScxmlExecutionSink sink)
    {
        var arrayExpr = element.Attribute("array")?.Value;
        var item = element.Attribute("item")?.Value;
        var index = element.Attribute("index")?.Value;

        if (string.IsNullOrWhiteSpace(arrayExpr))
        {
            return sink.Fail("<foreach> requires an 'array' attribute.");
        }

        if (string.IsNullOrWhiteSpace(item) || !ScxmlDataModel.IsIdentifier(item))
        {
            return sink.Fail($"<foreach> requires a valid 'item' location (got '{item}').");
        }

        if (!sink.Context.TryEvaluateValue(arrayExpr, out var arrayValue, out var evalError))
        {
            return sink.Fail(evalError!);
        }

        if (ScxmlValue.FromJs(arrayValue) is not object[] items)
        {
            return sink.Fail($"<foreach array=\"{arrayExpr}\"> did not evaluate to an array.");
        }

        // SCXML §6.4: `item` (and `index`) are declared if they do not already exist.
        if (!sink.Context.HasGlobal(item))
        {
            sink.Context.Declare(item, JsValue.Undefined);
        }

        if (index is not null && !sink.Context.HasGlobal(index))
        {
            sink.Context.Declare(index, JsValue.Undefined);
        }

        sink.Mutated();

        for (var i = 0; i < items.Length; i++)
        {
            sink.Context.Declare(item, ScxmlValue.ToJs(sink.Context.Engine, items[i]));
            if (index is not null)
            {
                sink.Context.Declare(index, JsValue.FromObject(sink.Context.Engine, i));
            }

            sink.Mutated();

            foreach (var child in element.Elements())
            {
                if (!Execute(child, sink))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // --- <send> ---

    private static bool ExecuteSend(XElement element, ScxmlExecutionSink sink)
    {
        // The send id is resolved first so that every error this <send> can produce carries it
        // (SCXML §5.10: error events triggered by a <send> report its sendid).
        var sendId = element.Attribute("id")?.Value;
        var idLocation = element.Attribute("idlocation")?.Value;
        if (sendId is null && idLocation is not null)
        {
            sendId = "scxml.send." + Guid.NewGuid().ToString("N");
            if (!sink.Context.TryAssign(idLocation, JsValue.FromObject(sink.Context.Engine, sendId), out var idError))
            {
                return sink.Fail(idError!);
            }

            sink.Mutated();
        }

        // type / typeexpr — only the SCXML event processor is supported.
        if (!TryResolve(element, "type", "typeexpr", sink, out var type, sendId))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(type) &&
            !SupportedSendTypes.Contains(type, StringComparer.Ordinal))
        {
            return sink.Fail($"Unsupported <send> type '{type}'.", sendId);
        }

        if (!TryResolve(element, "event", "eventexpr", sink, out var eventName, sendId))
        {
            return false;
        }

        if (string.IsNullOrEmpty(eventName))
        {
            return sink.Fail("<send> requires 'event' or 'eventexpr'.", sendId);
        }

        if (!TryResolve(element, "target", "targetexpr", sink, out var target, sendId))
        {
            return false;
        }

        if (!TryResolve(element, "delay", "delayexpr", sink, out var delayText, sendId))
        {
            return false;
        }

        TimeSpan? delay = null;
        if (!string.IsNullOrWhiteSpace(delayText))
        {
            if (!TryParseDelay(delayText, out var parsed))
            {
                return sink.Fail($"Cannot parse <send> delay '{delayText}'.", sendId);
            }

            delay = parsed;
        }

        if (!TryBuildPayload(element, sink, out var data, allowContent: true, sendId))
        {
            return false;
        }

        var evt = new ScxmlEvent(eventName, data)
        {
            Kind = ScxmlEventKind.External,
            SendId = sendId,
            Origin = $"#_scxml_{sink.Context.SessionId}",
            OriginType = ScxmlEvent.ScxmlEventProcessor
        };

        // Target resolution (SCXML Appendix C.1).
        if (string.IsNullOrEmpty(target))
        {
            // No target: the sender's own *external* queue (SCXML Appendix C.1), which is a
            // send-to-self, not a raise.
            sink.Effects.Add(new SendToEffect(EffectTarget.Self, evt, sendId, delay));
            return true;
        }

        if (target == "#_internal")
        {
            if (delay is null)
            {
                sink.Raised.Add(evt with { Kind = ScxmlEventKind.Internal });
            }
            else
            {
                // A delayed internal send: the event lands on the internal queue when the timer
                // fires, which is exactly what a delayed raise does.
                sink.Effects.Add(new RaiseEffect(evt with { Kind = ScxmlEventKind.Internal }, sendId, delay.Value));
            }

            return true;
        }

        if (target == "#_parent")
        {
            sink.Effects.Add(new SendToEffect(EffectTarget.Parent, evt, sendId, delay));
            return true;
        }

        // #_scxml_<sessionid> is resolved here rather than by the driving runtime: SCXML requires
        // error.communication for an unknown session to reach the internal queue *in document
        // order*, ahead of anything raised later in the same block (test496). A runtime that only
        // discovers the failure when it comes to deliver the event necessarily reports it late.
        if (target.StartsWith("#_scxml_", StringComparison.Ordinal))
        {
            var session = target["#_scxml_".Length..];

            if (!string.Equals(session, sink.Context.SessionId, StringComparison.Ordinal))
            {
                return sink.Undeliverable($"Unable to dispatch event to target '{target}'.", sendId);
            }

            // Own session: Appendix C.1 puts it on this session's *external* queue.
            sink.Effects.Add(new SendToEffect(EffectTarget.Self, evt, sendId, delay));
            return true;
        }

        if (target.StartsWith("#_", StringComparison.Ordinal))
        {
            sink.Effects.Add(new SendToEffect(target[2..], evt with { InvokeId = target[2..] }, sendId, delay));
            return true;
        }

        return sink.Fail($"Unsupported <send> target '{target}'.", sendId);
    }

    /// <summary>
    /// Builds an event/donedata/input payload from <c>namelist</c>, <c>&lt;param&gt;</c> and
    /// (when <paramref name="allowContent"/>) <c>&lt;content&gt;</c>.
    /// </summary>
    public static bool TryBuildPayload(
        XElement element,
        ScxmlExecutionSink sink,
        out object? data,
        bool allowContent = true,
        string? sendId = null)
    {
        data = null;

        var content = allowContent
            ? element.Elements().FirstOrDefault(e => e.Name.LocalName == "content")
            : null;

        if (content is not null)
        {
            var expr = content.Attribute("expr")?.Value;
            if (expr is not null)
            {
                if (!sink.Context.TryEvaluateValue(expr, out var value, out var error))
                {
                    return sink.Fail(error!, sendId);
                }

                data = ScxmlValue.FromJs(value);
            }
            else
            {
                data = ScxmlValue.FromJs(sink.Context.ParseLiteral(InlineText(content)));
            }

            return true;
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        var namelist = element.Attribute("namelist")?.Value;
        if (!string.IsNullOrWhiteSpace(namelist))
        {
            foreach (var name in namelist.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!sink.Context.TryEvaluate(name, out var value, out var error))
                {
                    return sink.Fail(error!, sendId);
                }

                payload[name] = ScxmlValue.FromJs(value);
            }
        }

        foreach (var param in element.Elements().Where(e => e.Name.LocalName == "param"))
        {
            var name = param.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(name))
            {
                return sink.Fail("<param> requires a 'name' attribute.", sendId);
            }

            var expr = param.Attribute("expr")?.Value ?? param.Attribute("location")?.Value;
            if (string.IsNullOrWhiteSpace(expr))
            {
                return sink.Fail($"<param name=\"{name}\"> requires 'expr' or 'location'.", sendId);
            }

            if (!sink.Context.TryEvaluateValue(expr, out var value, out var error))
            {
                return sink.Fail(error!, sendId);
            }

            payload[name] = ScxmlValue.FromJs(value);
        }

        if (payload.Count > 0)
        {
            data = payload;
        }

        return true;
    }

    private static bool TryResolve(
        XElement element,
        string attribute,
        string exprAttribute,
        ScxmlExecutionSink sink,
        out string? value,
        string? sendId = null)
    {
        value = element.Attribute(attribute)?.Value;
        if (value is not null)
        {
            return true;
        }

        var expr = element.Attribute(exprAttribute)?.Value;
        if (string.IsNullOrWhiteSpace(expr))
        {
            return true;
        }

        if (!sink.Context.TryEvaluateValue(expr, out var evaluated, out var error))
        {
            return sink.Fail(error!, sendId);
        }

        value = evaluated.IsUndefined() || evaluated.IsNull()
            ? null
            : TypeConverter.ToString(evaluated);
        return true;
    }

    /// <summary>CSS2 time values: <c>"1s"</c>, <c>"1.5s"</c>, <c>"100ms"</c>; a bare number is milliseconds.</summary>
    public static bool TryParseDelay(string text, out TimeSpan delay)
    {
        delay = default;
        var value = text.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        double factor;
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^2];
            factor = 1;
        }
        else if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^1];
            factor = 1000;
        }
        else
        {
            factor = 1;
        }

        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            number < 0)
        {
            return false;
        }

        delay = TimeSpan.FromMilliseconds(number * factor);
        return true;
    }

    /// <summary>The element's text/CDATA content, trimmed.</summary>
    public static string InlineText(XElement element)
    {
        var text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));
        return text.Trim();
    }
}
