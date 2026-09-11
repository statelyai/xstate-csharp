using System.Xml.Linq;

namespace XState.Scxml;

/// <summary>
/// SCXML ⇄ XState.NET conversion.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Parse(string, Func{string, string})"/> produces a
/// <c>StateMachine&lt;<see cref="ScxmlDataModel"/>&gt;</c>: an ordinary XState machine whose
/// context is a Jint engine holding the ECMAScript datamodel. Everything the pure core already
/// understands — the state tree, transition selection, history, donedata, invocations — is
/// expressed with core primitives; the SCXML-specific parts (expressions, executable content)
/// become <see cref="ScriptAction{TContext}"/>s that run against that engine.
/// </para>
/// <para><b>Supported</b>: <c>&lt;scxml&gt;</c> (name/initial/datamodel/version),
/// <c>&lt;state&gt;</c> (<c>initial</c> attribute and <c>&lt;initial&gt;</c> element),
/// <c>&lt;parallel&gt;</c>, <c>&lt;final&gt;</c> with <c>&lt;donedata&gt;</c>,
/// <c>&lt;history&gt;</c> (shallow/deep, default transition <em>with its executable content</em>),
/// <c>&lt;transition&gt;</c>
/// (event token lists incl. <c>*</c>, <c>cond</c>, multiple targets,
/// <c>type="internal|external"</c>), <c>&lt;onentry&gt;</c>/<c>&lt;onexit&gt;</c>,
/// multiple and deep <c>initial</c> targets with executable content on the <c>&lt;initial&gt;</c>
/// transition (run only on default entry), <c>&lt;datamodel&gt;</c>/<c>&lt;data&gt;</c> (including
/// <c>src</c>, and <c>binding="late"</c>), <c>&lt;script&gt;</c> at the top level and in
/// executable content (including <c>src</c>, fetched through the same resolver), and the executable
/// content <c>&lt;raise&gt;</c>, <c>&lt;assign&gt;</c>, <c>&lt;script&gt;</c>, <c>&lt;log&gt;</c>,
/// <c>&lt;send&gt;</c>, <c>&lt;cancel&gt;</c>, <c>&lt;if&gt;/&lt;elseif&gt;/&lt;else&gt;</c>,
/// <c>&lt;foreach&gt;</c>, plus <c>&lt;invoke&gt;</c> of nested SCXML (<c>src</c>,
/// <c>srcexpr</c>, inline <c>&lt;content&gt;</c>) with <c>autoforward</c>, <c>&lt;finalize&gt;</c>,
/// <c>idlocation</c>, and cancellation of the invocation when its <c>&lt;param&gt;</c>/
/// <c>namelist</c> cannot be evaluated.
/// </para>
/// <para><b>Deferred</b>: non-ECMAScript datamodels (the <c>null</c> datamodel is accepted as a
/// degenerate case of <c>ecmascript</c>), XML-valued <c>&lt;data&gt;</c>/<c>&lt;content&gt;</c>
/// (there is no DOM — such bodies become JSON if they parse, otherwise a space-normalized
/// string), the BasicHTTP event I/O processor (only the SCXML processor is accepted — anything
/// else raises <c>error.execution</c>), and the <c>xmlns:conf</c> conformance-test vocabulary.
/// </para>
/// <para>
/// Conformance against the W3C Implementation Report corpus is tracked in <c>CONFORMANCE.md</c>.
/// </para>
/// </remarks>
public static class ScxmlConverter
{
    /// <summary>The SCXML namespace.</summary>
    public static readonly XNamespace Namespace = "http://www.w3.org/2005/07/scxml";

    /// <summary>Parses an SCXML document.</summary>
    /// <param name="xml">The SCXML source.</param>
    /// <param name="resolveSrc">
    /// Optional resolver for <c>&lt;invoke src&gt;</c>, mapping a URI to SCXML source. Without
    /// it, <c>src</c> invocations throw.
    /// </param>
    public static StateMachine<ScxmlDataModel> Parse(string xml, Func<string, string>? resolveSrc = null) =>
        Parse(XDocument.Parse(xml), resolveSrc);

    /// <inheritdoc cref="Parse(string, Func{string, string})"/>
    public static StateMachine<ScxmlDataModel> Parse(XDocument document, Func<string, string>? resolveSrc = null) =>
        new ScxmlParser(resolveSrc).Parse(document);

    /// <summary>Serializes a machine back to an SCXML document.</summary>
    /// <param name="machine">The machine to write.</param>
    /// <param name="options">
    /// Serialization options. By default a machine holding actions, guards or event descriptors
    /// that did not come from <see cref="Parse(string, Func{string, string})"/> — and so have no
    /// SCXML text — is refused with <see cref="NotSupportedException"/> rather than silently
    /// serialized into a document that behaves differently; set
    /// <see cref="ScxmlSerializationOptions.EmitPlaceholders"/> to emit placeholders instead.
    /// </param>
    /// <exception cref="NotSupportedException">
    /// A part of <paramref name="machine"/> has no SCXML form and placeholders were not requested.
    /// </exception>
    public static XDocument ToScxml(
        StateMachine<ScxmlDataModel> machine,
        ScxmlSerializationOptions? options = null) => ScxmlSerializer.Serialize(machine, options);

    /// <inheritdoc cref="ToScxml(StateMachine{ScxmlDataModel}, ScxmlSerializationOptions?)"/>
    public static string ToScxmlString(
        StateMachine<ScxmlDataModel> machine,
        ScxmlSerializationOptions? options = null) => ToScxml(machine, options).ToString();

    // --- Provenance (populated by Parse) ---

    /// <summary>The SCXML element an action was compiled from, if any.</summary>
    public static XElement? GetSource(ActionDefinition<ScxmlDataModel> action) =>
        ScxmlMetadata.ActionSources.TryGetValue(action, out var element) ? element : null;

    /// <summary>The SCXML element a state node was built from, if any.</summary>
    public static XElement? GetSource(StateNode<ScxmlDataModel> node) =>
        ScxmlMetadata.NodeSources.TryGetValue(node, out var element) ? element : null;

    /// <summary>
    /// The parsed <c>event</c>/<c>cond</c> of a transition. Imported transitions match events in
    /// their guard rather than through an <see cref="EventDescriptor"/>, so this is the only way
    /// to recover the original descriptor tokens.
    /// </summary>
    public static ScxmlTransitionInfo? GetTransitionInfo(TransitionDefinition<ScxmlDataModel> transition) =>
        ScxmlMetadata.Transitions.TryGetValue(transition, out var info) ? info : null;

    /// <summary>
    /// The SCXML text behind an <c>&lt;invoke&gt;</c>: its source element, <c>autoforward</c>,
    /// <c>&lt;finalize&gt;</c> block and <c>idlocation</c>.
    /// </summary>
    /// <remarks>
    /// This is provenance, not configuration — all three are already compiled onto the
    /// <see cref="InvokeDefinition{TContext}"/> and enforced by the core:
    /// <c>autoforward</c> as <see cref="InvokeDefinition{TContext}.AutoForward"/>, and
    /// <c>&lt;finalize&gt;</c> as <see cref="InvokeDefinition{TContext}.OnExternalEvent"/>, the
    /// SCXML §3.13 pre-selection hook that runs the block against the <em>parent's</em> datamodel
    /// before transitions are selected. <c>&lt;finalize&gt;</c> runs only for events of its own
    /// invocation, recognized by <c>_event.invokeid</c>, so the driving runtime must stamp
    /// child-to-parent events with the child's id (as
    /// <c>DeterministicInterpreter.DecorateChildEvent</c> does); an event carrying no invokeid
    /// runs no finalize. Read this metadata when you need the original markup — to re-serialize
    /// it, or to render it.
    /// </remarks>
    public static ScxmlInvokeMetadata? GetInvokeMetadata(InvokeDefinition<ScxmlDataModel> invoke) =>
        ScxmlMetadata.Invokes.TryGetValue(invoke, out var metadata) ? metadata : null;

    /// <summary>Parses an SCXML delay value (<c>"1s"</c>, <c>"100ms"</c>, or bare milliseconds).</summary>
    public static bool TryParseDelay(string text, out TimeSpan delay) =>
        ScxmlExecutableContent.TryParseDelay(text, out delay);
}
