using System.Xml.Linq;

namespace XState.Scxml;

/// <summary>Input for a <see cref="ScxmlDeferredMachine"/>: the resolved <c>srcexpr</c> plus the real input.</summary>
internal sealed record ScxmlDeferredInput(string? Source, object? Input);

/// <summary>
/// The logic behind <c>&lt;invoke srcexpr&gt;</c>: the document to run is named by an expression
/// that SCXML requires to be evaluated when the invocation starts, not when the parent is parsed.
/// </summary>
/// <remarks>
/// <para>
/// This instance is embedded in the parent <see cref="StateMachine{TContext}"/>, which is
/// immutable and shared by every actor started from it — so it holds <em>no</em> resolved-machine
/// state. Each <see cref="IActorLogic.GetInitialSnapshot"/> resolves and parses afresh (two actors
/// may resolve the same <c>&lt;invoke&gt;</c> to different documents), and each subsequent step
/// is delegated to the machine its own snapshot came from.
/// </para>
/// </remarks>
/// <param name="id">The invoke id, used only for diagnostics.</param>
/// <param name="resolveSrc">Resolver mapping the evaluated <c>srcexpr</c> to SCXML source.</param>
/// <param name="invokeChain">Nesting chain carried from the parent parse; see <see cref="ScxmlInvokeChain"/>.</param>
internal sealed class ScxmlDeferredMachine(
    string id,
    Func<string, string>? resolveSrc,
    IReadOnlyList<string>? invokeChain = null) : IMachineLogic
{
    public string Id => id;

    /// <summary>The document is only known once <c>srcexpr</c> is evaluated, so the version is too.</summary>
    public string? Version => null;

    /// <summary>SCXML names invoked documents by URI, not through an actor-source registry.</summary>
    public IReadOnlyDictionary<string, IActorLogic> Sources { get; } =
        new Dictionary<string, IActorLogic>(StringComparer.Ordinal);

    /// <summary>Persists through the machine the snapshot came from (see <see cref="Transition"/>).</summary>
    public XState.Persistence.PersistedSnapshot Persist(ISnapshot state, XState.Persistence.PersistOptions? options = null)
    {
        if (state is not State<ScxmlDataModel> { Machine: { } machine })
        {
            throw new InvalidOperationException(
                $"Invocation '{id}' was asked to persist a snapshot that did not come from a parsed SCXML document.");
        }

        return ((IMachineLogic)machine).Persist(state, options);
    }

    /// <summary>
    /// An <c>srcexpr</c> invocation has no document until the expression is evaluated, so there is
    /// nothing to restore against here: restore the persisted child through the parsed document
    /// (<c>ScxmlConverter.Parse</c>) that <see cref="XState.Persistence.PersistedSnapshot.Machine"/> names.
    /// </summary>
    public XState.Persistence.IRestoreResult Restore(
        XState.Persistence.PersistedSnapshot snapshot,
        Func<XState.Persistence.PersistedSnapshot, string?, XState.Persistence.PersistedSnapshot>? migrate = null,
        Func<object?, object?>? contextConverter = null,
        string? rootAddress = null) =>
        throw new NotSupportedException(
            $"Invocation '{id}' is resolved from <invoke srcexpr>; restore its snapshot through the parsed document it names.");

    public (ISnapshot State, IReadOnlyList<Effect> Effects) GetInitialSnapshot(object? input)
    {
        var deferred = input as ScxmlDeferredInput;

        if (deferred?.Source is not { Length: > 0 } source)
        {
            throw new InvalidOperationException(
                $"<invoke srcexpr> for '{id}' did not evaluate to a document location.");
        }

        if (resolveSrc is null)
        {
            throw new NotSupportedException(
                $"<invoke srcexpr> for '{id}' requires a source resolver (pass one to ScxmlConverter.Parse).");
        }

        var resolved = new ScxmlParser(resolveSrc, invokeChain).Parse(XDocument.Parse(resolveSrc(source)));
        return ((IActorLogic)resolved).GetInitialSnapshot(deferred.Input);
    }

    /// <summary>
    /// Steps the snapshot against the machine that produced it. Reading the machine back off the
    /// snapshot — rather than off a field here — is what keeps concurrent actors of the same
    /// parent, whose <c>srcexpr</c> resolved to different documents, from stepping each other's
    /// state.
    /// </summary>
    public (ISnapshot State, IReadOnlyList<Effect> Effects) Transition(ISnapshot state, MachineEvent evt)
    {
        if (state is not State<ScxmlDataModel> { Machine: { } machine })
        {
            throw new InvalidOperationException(
                $"Invocation '{id}' was stepped with a snapshot that did not come from a parsed SCXML document.");
        }

        return ((IActorLogic)machine).Transition(state, evt);
    }
}
