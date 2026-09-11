using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace XState.Scxml;

/// <summary>What the parser recorded about one <c>&lt;transition&gt;</c>.</summary>
public sealed class ScxmlTransitionInfo
{
    /// <summary>The source element (used by the serializer to round-trip executable content).</summary>
    public required XElement Source { get; init; }

    /// <summary>
    /// The whitespace-separated tokens of the <c>event</c> attribute. Empty for an eventless
    /// (<c>always</c>) transition.
    /// </summary>
    public required IReadOnlyList<string> EventTokens { get; init; }

    /// <summary>The <c>cond</c> attribute, or <see langword="null"/> when absent/blank.</summary>
    public string? Cond { get; init; }

    /// <summary>Position of the element among its siblings, for stable re-serialization.</summary>
    public int DocumentIndex { get; init; }
}

/// <summary>SCXML <c>&lt;invoke&gt;</c> details that the core <see cref="InvokeDefinition{TContext}"/> cannot hold.</summary>
public sealed class ScxmlInvokeMetadata
{
    public required XElement Source { get; init; }

    /// <summary>
    /// <c>autoforward="true"</c>, also compiled onto
    /// <see cref="InvokeDefinition{TContext}.AutoForward"/>, which is what enforces it.
    /// </summary>
    public bool AutoForward { get; init; }

    /// <summary>
    /// The <c>&lt;finalize&gt;</c> block, also compiled onto
    /// <see cref="InvokeDefinition{TContext}.OnExternalEvent"/>, which is what runs it — see
    /// <see cref="ScxmlConverter.GetInvokeMetadata"/>.
    /// </summary>
    public XElement? Finalize { get; init; }

    /// <summary>The <c>idlocation</c> attribute, if any.</summary>
    public string? IdLocation { get; init; }
}

/// <summary>
/// Side tables linking the objects the core builder produced back to the SCXML elements they
/// came from. The core switches on concrete action/transition types and has no room for
/// provenance, so it is kept here instead; entries are weak and disappear with the machine.
/// </summary>
internal static class ScxmlMetadata
{
    public static readonly ConditionalWeakTable<ActionDefinition<ScxmlDataModel>, XElement> ActionSources = new();
    public static readonly ConditionalWeakTable<ActionDefinition<ScxmlDataModel>, object> SynthesizedActions = new();

    /// <summary>
    /// Actions compiled from a whole block: their recorded source is the <em>container</em>
    /// (<c>&lt;onentry&gt;</c>, <c>&lt;onexit&gt;</c>, <c>&lt;transition&gt;</c>), so the serializer
    /// must emit the container's children rather than the container itself.
    /// </summary>
    public static readonly ConditionalWeakTable<ActionDefinition<ScxmlDataModel>, object> BlockActions = new();
    public static readonly ConditionalWeakTable<TransitionDefinition<ScxmlDataModel>, ScxmlTransitionInfo> Transitions = new();
    public static readonly ConditionalWeakTable<InvokeDefinition<ScxmlDataModel>, ScxmlInvokeMetadata> Invokes = new();
    public static readonly ConditionalWeakTable<StateNode<ScxmlDataModel>, XElement> NodeSources = new();
    public static readonly ConditionalWeakTable<StateMachine<ScxmlDataModel>, XElement> MachineSources = new();

    public static void Record(ActionDefinition<ScxmlDataModel> action, XElement source) =>
        ActionSources.AddOrUpdate(action, source);

    public static void MarkSynthesized(ActionDefinition<ScxmlDataModel> action) =>
        SynthesizedActions.AddOrUpdate(action, new object());

    public static bool IsSynthesized(ActionDefinition<ScxmlDataModel> action) =>
        SynthesizedActions.TryGetValue(action, out _);

    public static void MarkBlock(ActionDefinition<ScxmlDataModel> action) =>
        BlockActions.AddOrUpdate(action, new object());

    public static bool IsBlock(ActionDefinition<ScxmlDataModel> action) =>
        BlockActions.TryGetValue(action, out _);
}
