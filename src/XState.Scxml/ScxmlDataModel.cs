using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;

namespace XState.Scxml;

/// <summary>
/// The shared, mutable half of an SCXML "ecmascript" datamodel: one Jint engine plus the
/// per-session bookkeeping that outlives any individual snapshot.
/// </summary>
internal sealed class ScxmlSession
{
    public Engine Engine { get; }
    public string SessionId { get; }
    public string Name { get; }
    public List<MachineEvent> PendingInternalEvents { get; } = [];

    /// <summary>Backing store for the JS <c>In()</c> predicate; rebound before every evaluation.</summary>
    public Func<string, bool> InPredicate { get; set; } = _ => false;

    /// <summary>
    /// The state ids currently entered, maintained by synthesized entry/exit actions so that
    /// <c>In()</c> also works inside executable content (the core only hands a configuration
    /// predicate to guards).
    /// </summary>
    public HashSet<string> ActiveStates { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The event currently published as <c>_event</c>. SCXML requires <c>_event</c> to keep its
    /// identity for the whole time an event is being processed, so it is only rebuilt when the
    /// event actually changes.
    /// </summary>
    public MachineEvent? CurrentEvent { get; set; }

    /// <summary>Payloads produced by <c>&lt;donedata&gt;</c>, keyed by final-state node id.</summary>
    public Dictionary<string, object?> DoneData { get; } = new(StringComparer.Ordinal);

    public ScxmlSession(string name, string? sessionId = null)
    {
        Name = name;
        SessionId = sessionId ?? Guid.NewGuid().ToString("N");
        Engine = new Engine();

        Engine.SetValue("In", new Func<string, bool>(id => InPredicate(id)));
        Engine.SetValue("_sessionid", SessionId);
        Engine.SetValue("_name", Name);

        // _event is deliberately *not* declared: SCXML §5.10 leaves it unbound until the first
        // event is processed, and tests check that referencing it before then is an error.

        // Stub I/O processor table (only the SCXML event processor is supported).
        var location = $"#_scxml_{SessionId}";
        Engine.SetValue("__scxmlLocation", location);
        Engine.SetValue("_ioprocessors", Engine.Evaluate(
            "({'" + ScxmlEvent.ScxmlEventProcessor + "':{location:__scxmlLocation}," +
            "scxml:{location:__scxmlLocation}})"));
    }
}

/// <summary>
/// Machine context for SCXML-imported machines: a handle onto a Jint <see cref="Engine"/> that
/// holds the ECMAScript datamodel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deviation from core purity.</b> Everything else in XState.NET treats the context as an
/// immutable value. SCXML datamodels are imperative by definition (<c>&lt;assign&gt;</c>,
/// <c>&lt;script&gt;</c> and <c>&lt;foreach&gt;</c> mutate variables in place), so the engine is
/// shared and mutated in place. What <em>is</em> immutable is this handle: every mutation
/// produces a <b>new</b> <see cref="ScxmlDataModel"/> with an incremented <see cref="Version"/>,
/// and equality is defined over <see cref="Version"/> only. That matters because the macrostep
/// re-selects eventless transitions only when the context "changed" — with a reference-equal
/// mutable engine every datamodel change would look like a no-op and eventless transitions
/// would stop being re-evaluated.
/// </para>
/// <para>
/// Practical consequence: an older <see cref="State{TContext}"/> snapshot does <b>not</b> give
/// you the old datamodel values; time travel and snapshot persistence are not supported for
/// SCXML machines.
/// </para>
/// <para>
/// <b><see cref="PendingInternalEvents"/> contract.</b> Guards are pure in the core — they
/// cannot raise events — but SCXML requires <c>error.execution</c> to be raised when a
/// <c>cond</c> evaluation fails (the guard then evaluates to <c>false</c>). Such errors, and
/// errors from action resolvers that have no channel of their own (<c>&lt;log&gt;</c>), are
/// appended here instead. The driving interpreter/harness must drain this list after every
/// <see cref="StateMachine{TContext}.Transition"/> / <see cref="StateMachine{TContext}.GetInitialState"/>
/// call and feed the drained events back into the machine before any external event.
/// Executable content does not use this channel: <c>ScriptAction</c> can raise directly, and
/// does.
/// </para>
/// </remarks>
public sealed class ScxmlDataModel : IEquatable<ScxmlDataModel>
{
    private readonly ScxmlSession _session;

    internal ScxmlDataModel(ScxmlSession session, int version)
    {
        _session = session;
        Version = version;
    }

    /// <summary>Monotonic mutation counter. Equality is defined over this value only.</summary>
    public int Version { get; }

    /// <summary>The live Jint engine holding the datamodel. Shared across all versions.</summary>
    public Engine Engine => _session.Engine;

    /// <summary>SCXML <c>_sessionid</c>.</summary>
    public string SessionId => _session.SessionId;

    /// <summary>SCXML <c>_name</c> (the <c>&lt;scxml name&gt;</c> attribute).</summary>
    public string Name => _session.Name;

    /// <summary>
    /// Platform events (typically <c>error.execution</c>) produced from contexts that cannot
    /// raise directly — see the type-level remarks for the draining contract.
    /// </summary>
    public List<MachineEvent> PendingInternalEvents => _session.PendingInternalEvents;

    /// <summary>Drains <see cref="PendingInternalEvents"/> and returns what was drained.</summary>
    public IReadOnlyList<MachineEvent> DrainPendingInternalEvents()
    {
        if (_session.PendingInternalEvents.Count == 0)
        {
            return [];
        }

        var drained = _session.PendingInternalEvents.ToArray();
        _session.PendingInternalEvents.Clear();
        return drained;
    }

    /// <summary>A new handle onto the same engine with the version bumped.</summary>
    internal ScxmlDataModel Mutated() => new(_session, Version + 1);

    // --- Evaluation ---

    /// <summary>Binds <c>_event</c> and <c>In()</c> for the next evaluation.</summary>
    internal void Bind(MachineEvent? evt, Func<string, bool>? inPredicate)
    {
        // Guards receive the core's configuration predicate; executable content has no such
        // channel, so it falls back to the entry/exit-maintained set.
        var active = _session.ActiveStates;
        _session.InPredicate = inPredicate ?? (id => active.Contains(id.StartsWith('#') ? id[1..] : id));

        // Neither @xstate.init nor xstate.timer is an SCXML event, so neither binds _event: a
        // timer firing is the host reporting elapsed time, and the event it releases arrives
        // separately (raised onto the internal queue, or delivered as its own external event).
        if (evt is not null and not InitEvent and not TimerEvent)
        {
            SetCurrentEvent(evt);
        }
    }

    internal void EnterState(string id) => _session.ActiveStates.Add(id);

    internal void ExitState(string id) => _session.ActiveStates.Remove(id);

    internal void SetDoneData(string key, object? value) => _session.DoneData[key] = value;

    internal object? GetDoneData(string key) =>
        _session.DoneData.TryGetValue(key, out var value) ? value : null;

    /// <summary>Publishes an event as SCXML <c>_event</c>.</summary>
    internal void SetCurrentEvent(MachineEvent evt)
    {
        // _event must stay identical (===) for as long as the same event is being processed.
        if (ReferenceEquals(_session.CurrentEvent, evt))
        {
            return;
        }

        _session.CurrentEvent = evt;

        var scxml = evt as ScxmlEvent;
        var engine = _session.Engine;

        engine.SetValue("__scxmlEvtName", ScxmlEventNaming.NameOf(evt));
        engine.SetValue("__scxmlEvtType", KindString(ScxmlEventNaming.KindOf(evt)));
        engine.SetValue("__scxmlEvtSendId", Optional(engine, scxml?.SendId));
        engine.SetValue("__scxmlEvtOrigin", Optional(engine, scxml?.Origin));
        engine.SetValue("__scxmlEvtOriginType", Optional(
            engine,
            scxml?.OriginType ?? (ScxmlEventNaming.KindOf(evt) is ScxmlEventKind.External
                ? ScxmlEvent.ScxmlEventProcessor
                : null)));
        engine.SetValue("__scxmlEvtInvokeId", Optional(engine, scxml?.InvokeId ?? InvokeIdOf(evt)));
        engine.SetValue("__scxmlEvtData", ScxmlValue.ToJs(engine, ScxmlEventNaming.DataOf(evt)));

        engine.SetValue("_event", engine.Evaluate(
            "({name:__scxmlEvtName,type:__scxmlEvtType,sendid:__scxmlEvtSendId," +
            "origin:__scxmlEvtOrigin,origintype:__scxmlEvtOriginType," +
            "invokeid:__scxmlEvtInvokeId,data:__scxmlEvtData})"));
    }

    /// <summary>SCXML §5.10: <c>done.invoke</c>/<c>error.*</c> events name the invocation they came from.</summary>
    private static string? InvokeIdOf(MachineEvent evt) => evt switch
    {
        DoneActorEvent e => e.ActorId,
        ErrorActorEvent e => e.ActorId,
        _ => null
    };

    private static string KindString(ScxmlEventKind kind) => kind switch
    {
        ScxmlEventKind.Internal => "internal",
        ScxmlEventKind.Platform => "platform",
        _ => "external"
    };

    private static JsValue Optional(Engine engine, string? value) =>
        value is null ? JsValue.Undefined : JsValue.FromObject(engine, value);

    /// <summary>Evaluates an expression, returning <see langword="false"/> on failure.</summary>
    internal bool TryEvaluate(string expression, out JsValue value, out string? error)
    {
        try
        {
            value = _session.Engine.Evaluate(expression);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            value = JsValue.Undefined;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Evaluates a <em>value</em> expression (an <c>expr</c> attribute). The expression is
    /// parenthesized so that object literals parse as values rather than blocks; a trailing
    /// <c>;</c> is dropped first, because <c>(new Foo();)</c> is a syntax error.
    /// </summary>
    internal bool TryEvaluateValue(string expression, out JsValue value, out string? error) =>
        TryEvaluate(Parenthesize(expression), out value, out error);

    internal static string Parenthesize(string expression) =>
        "(" + expression.Trim().TrimEnd(';', ' ', '\t', '\r', '\n') + ")";

    /// <summary>Runs a statement block (<c>&lt;script&gt;</c>), returning <see langword="false"/> on failure.</summary>
    internal bool TryExecute(string code, out string? error)
    {
        try
        {
            _session.Engine.Execute(code);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// SCXML <c>&lt;assign&gt;</c>. Assigning to an undeclared top-level variable is an error
    /// (SCXML requires the location to already exist in the datamodel).
    /// </summary>
    internal bool TryAssign(string location, JsValue value, out string? error)
    {
        try
        {
            // SCXML §5.10: the system variables are read-only for the whole session.
            if (SystemVariables.Contains(RootIdentifier(location), StringComparer.Ordinal))
            {
                error = $"Cannot assign to the system variable '{RootIdentifier(location)}'.";
                return false;
            }

            if (IsIdentifier(location) && !HasGlobal(location))
            {
                error = $"Cannot assign to undeclared location '{location}'.";
                return false;
            }

            _session.Engine.SetValue("__scxmlAssigned", value);
            _session.Engine.Evaluate($"{location} = __scxmlAssigned;");
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Declares (or overwrites) a top-level datamodel variable.</summary>
    internal void Declare(string id, JsValue value) => _session.Engine.SetValue(id, value);

    /// <summary>
    /// SCXML ECMAScript datamodel §B.2: the literal body of <c>&lt;data&gt;</c> / <c>&lt;content&gt;</c>
    /// (or of a <c>src</c> document) is JSON when it parses as JSON, and otherwise a
    /// space-normalized string. XML bodies would become a DOM node — not supported.
    /// </summary>
    internal JsValue ParseLiteral(string text)
    {
        var trimmed = text.Trim();

        if (trimmed.Length > 0)
        {
            _session.Engine.SetValue("__scxmlLiteral", trimmed);
            if (TryEvaluate("JSON.parse(__scxmlLiteral)", out var json, out _))
            {
                return json;
            }
        }

        return JsValue.FromObject(_session.Engine, NormalizeSpace(text));
    }

    /// <summary>XML space normalization: collapse runs of whitespace, trim the ends.</summary>
    internal static string NormalizeSpace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal bool HasGlobal(string id)
    {
        _session.Engine.SetValue("__scxmlProbe", id);
        return TypeConverter.ToBoolean(
            _session.Engine.Evaluate("Object.prototype.hasOwnProperty.call(globalThis, __scxmlProbe)"));
    }

    /// <summary>Reads a datamodel variable as a CLR value (for tests and host code).</summary>
    public object? GetValue(string id) => ScxmlValue.FromJs(_session.Engine.GetValue(id));

    /// <summary>Evaluates an expression and returns the result as a CLR value.</summary>
    public object? Evaluate(string expression) => ScxmlValue.FromJs(_session.Engine.Evaluate(expression));

    internal static bool IsIdentifier(string location) =>
        location.Length > 0 &&
        (char.IsLetter(location[0]) || location[0] is '_' or '$') &&
        location.All(c => char.IsLetterOrDigit(c) || c is '_' or '$');

    /// <summary>SCXML §5.10 system variables — bound for the session and never assignable.</summary>
    private static readonly string[] SystemVariables = ["_event", "_sessionid", "_name", "_ioprocessors", "_x"];

    /// <summary>The leading identifier of a location expression (<c>"a.b[0]"</c> → <c>"a"</c>).</summary>
    private static string RootIdentifier(string location)
    {
        var trimmed = location.Trim();
        var end = 0;
        while (end < trimmed.Length && (char.IsLetterOrDigit(trimmed[end]) || trimmed[end] is '_' or '$'))
        {
            end++;
        }

        return trimmed[..end];
    }

    // --- Equality over Version only (see remarks) ---

    public bool Equals(ScxmlDataModel? other) => other is not null && Version == other.Version;

    public override bool Equals(object? obj) => Equals(obj as ScxmlDataModel);

    public override int GetHashCode() => Version;

    public override string ToString() => $"ScxmlDataModel(v{Version}, session {SessionId})";
}

/// <summary>Conversions between CLR payloads and Jint values.</summary>
internal static class ScxmlValue
{
    public static JsValue ToJs(Engine engine, object? value)
    {
        switch (value)
        {
            case null:
                return JsValue.Undefined;
            case JsValue js:
                return js;
            case IReadOnlyDictionary<string, object?> dictionary:
            {
                var obj = (ObjectInstance)engine.Evaluate("({})");
                foreach (var (key, item) in dictionary)
                {
                    obj.FastSetDataProperty(key, ToJs(engine, item));
                }
                return obj;
            }
            default:
                return JsValue.FromObject(engine, value);
        }
    }

    public static object? FromJs(JsValue value) => value.IsUndefined() ? null : value.ToObject();
}
