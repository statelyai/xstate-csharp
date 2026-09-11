using XState.TestKit;
using Xunit;

namespace XState.Scxml.Tests;

/// <summary>
/// The W3C SCXML 1.0 Implementation Report (IRP) conformance suite, run against the vendored
/// <c>w3c/</c> corpus.
/// </summary>
/// <remarks>
/// <para>
/// Every IRP test is a self-checking machine: it ends in a top-level <c>&lt;final id="pass"/&gt;</c>
/// or <c>&lt;final id="fail"/&gt;</c>. The harness therefore only has to run the document to
/// completion on the deterministic virtual-time interpreter and look at where it stopped.
/// </para>
/// <para>
/// <b><see cref="KnownFailing"/> is an exact list, not a floor.</b> A test that is not listed must
/// pass, and a test that <em>is</em> listed must fail — an unexpected pass fails the suite so the
/// list cannot silently rot.
/// </para>
/// </remarks>
public class W3cConformanceTests
{
    /// <summary>Virtual-time budget per test; IRP timeouts are all well under a minute.</summary>
    private static readonly TimeSpan VirtualTimeCap = TimeSpan.FromSeconds(120);

    private static string W3cDirectory => Path.Combine(AppContext.BaseDirectory, "w3c");

    // ------------------------------------------------------------------
    // Excluded: not executed at all (manual/optional IRP tests).
    // ------------------------------------------------------------------

    /// <summary>
    /// IRP tests the report itself marks as manual: the property under test is only visible in the
    /// log, so reaching the document's success state does not establish it. Some of these documents
    /// do have a reachable <c>&lt;final id="fail"/&gt;</c> — it catches gross failures only, and
    /// none of them has a <c>#pass</c> state for this harness to score.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ExcludedManual =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["test178"] = "Manual: the document has no #pass state — success is <final id=\"final\">, and the property under test (that a <send> carries both same-named <param> pairs) is only observable in the logged _event.raw, whose format the SCXML I/O processor does not specify.",
            ["test230"] = "Manual: no #pass state, and the verdict is whether the parent's and the child's logged event fields match for an autoforwarded event — a human comparison. Autoforward itself is implemented and covered by test229.",
            ["test250"] = "Manual: tester must read the cancelled child's log; the parent cannot observe the child after cancellation.",
            ["test307"] = "Manual: no #pass or #fail state — the document reaches <final id=\"final\"> either way, and the verdict is whether the two logged values match. ScxmlParserTests.LateBoundAccessBeforeDeclarationBehavesLikeAMissingSubstructure makes that comparison instead.",
            ["test415"] = "Manual: tester must confirm that an event raised in a top-level final state is never processed.",
        };

    // ------------------------------------------------------------------
    // Known failing: must fail. Grouped by bucket; see CONFORMANCE.md.
    // ------------------------------------------------------------------

    public static readonly IReadOnlyDictionary<string, string> KnownFailing =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // --- Basic HTTP Event I/O Processor (optional in the IRP; not implemented) ---
            ["test201"] = "basichttp: requires the Basic HTTP Event I/O Processor, which XState.Scxml does not implement (only the SCXML processor).",
            ["test509"] = "basichttp: POST delivery to an accessURI.",
            ["test510"] = "basichttp: inbound HTTP messages must land in the external queue.",
            ["test518"] = "basichttp: namelist values encoded as POST parameters.",
            ["test519"] = "basichttp: <param> values encoded as POST parameters.",
            ["test520"] = "basichttp: <content> sent as the message body.",
            ["test522"] = "basichttp: _ioprocessors['basichttp'].location used as a send target.",
            ["test531"] = "basichttp: the _scxmleventname parameter names the generated event.",
            ["test532"] = "basichttp: the HTTP method names the event when _scxmleventname is absent.",
            ["test534"] = "basichttp: <send event> is transmitted as the _scxmleventname parameter.",
            ["test567"] = "basichttp: message content other than _scxmleventname populates _event.data.",
            ["test577"] = "basichttp: a targetless basichttp <send> must raise error.communication; an unsupported <send type> correctly raises error.execution here instead.",

            // --- XML values in the ECMAScript datamodel (§B.2 DOM) ---
            ["test530"] = "XML datamodel: <invoke><content expr> resolving to an SCXML document held in a variable requires DOM-valued data.",
            ["test557"] = "XML datamodel: an XML <data> body/src must become a DOM node exposing getElementsByTagName; Jint has no DOM.",
            ["test561"] = "XML datamodel: XML <send><content> must arrive as a DOM node in _event.data.",

            // --- Scored as a failure by this harness' convention ---
            ["test301"] = "The IRP expects the document to be *rejected*, and it is: its <script src> cannot be fetched, so ScxmlConverter.Parse throws. The document has no #pass state to reach, so this harness can only score that rejection as a failure.",
        };

    // ------------------------------------------------------------------
    // Theory
    // ------------------------------------------------------------------

    /// <summary>All runnable IRP tests: <c>w3c/test*.scxml</c> minus invoked sub-documents and manual tests.</summary>
    public static TheoryData<string> Cases
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var id in TestIds())
            {
                data.Add(id);
            }

            return data;
        }
    }

    internal static IEnumerable<string> TestIds() =>
        Directory
            .EnumerateFiles(W3cDirectory, "test*.scxml")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => name!)
            // *sub*.scxml are documents invoked *by* a test, not tests themselves.
            .Where(name => !name.Contains("sub", StringComparison.Ordinal))
            .Where(name => !ExcludedManual.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(Cases))]
    public void ConformsToTheW3CImplementationReport(string testId)
    {
        var outcome = Run(testId);

        if (KnownFailing.TryGetValue(testId, out var reason))
        {
            Assert.False(
                outcome.Passed,
                $"{testId} now passes — remove it from KnownFailing (recorded reason: {reason}).");
            return;
        }

        Assert.True(outcome.Passed, $"{testId}: {outcome.Detail}");
    }

    /// <summary>
    /// Every executed IRP document, serialized back to SCXML with
    /// <see cref="ScxmlConverter.ToScxml(StateMachine{ScxmlDataModel}, ScxmlSerializationOptions?)"/>
    /// and re-parsed, must reach the same verdict as the document itself.
    /// </summary>
    /// <remarks>
    /// This is the only end-to-end check on the serializer: a structural comparison would miss the
    /// parts that decide behaviour (executable content recovered from source elements, the
    /// <c>event</c>/<c>cond</c> text of transitions whose matching lives in a guard, <c>binding</c>,
    /// state-local <c>&lt;datamodel&gt;</c>, <c>&lt;donedata&gt;</c>). Documents the direct run
    /// cannot even load — today only <c>test301</c>, whose <c>&lt;script src&gt;</c> is
    /// unfetchable — have nothing to serialize, so the round trip is satisfied by rejecting them
    /// the same way. External files referenced by <c>&lt;invoke src&gt;</c>/<c>&lt;data src&gt;</c>
    /// need no special handling: the <c>src</c> attribute survives serialization and is resolved
    /// again through the same <see cref="ResolveSrc"/>.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Cases))]
    public void SurvivesAnScxmlRoundTrip(string testId)
    {
        var direct = Run(testId);
        var roundTripped = Run(testId, roundTrip: true);

        Assert.True(
            direct.Passed == roundTripped.Passed,
            $"{testId}: direct run {Verdict(direct)}, round-tripped run {Verdict(roundTripped)}.");

        // A document the serializer turns unparseable must not hide behind a case that already
        // fails at run time: the two runs must fail at the same stage.
        Assert.True(
            direct.Detail.StartsWith("parse threw", StringComparison.Ordinal) ==
            roundTripped.Detail.StartsWith("parse threw", StringComparison.Ordinal),
            $"{testId}: direct run {Verdict(direct)}, round-tripped run {Verdict(roundTripped)}.");
    }

    private static string Verdict(Outcome outcome) =>
        (outcome.Passed ? "passed" : "failed") + $" ({outcome.Detail})";

    // ------------------------------------------------------------------
    // Runner
    // ------------------------------------------------------------------

    /// <summary>The result of running one IRP document.</summary>
    public sealed record Outcome(bool Passed, string Detail);

    /// <param name="testId">The IRP document to run.</param>
    /// <param name="roundTrip">
    /// Serialize the parsed machine back to SCXML and re-parse it before running, so that the
    /// verdict is produced by the emitted document rather than the original.
    /// </param>
    public static Outcome Run(string testId, bool roundTrip = false)
    {
        var path = Path.Combine(W3cDirectory, testId + ".scxml");

        StateMachine<ScxmlDataModel> machine;
        try
        {
            machine = ScxmlConverter.Parse(File.ReadAllText(path), ResolveSrc);

            if (roundTrip)
            {
                machine = ScxmlConverter.Parse(ScxmlConverter.ToScxml(machine), ResolveSrc);
            }
        }
        catch (Exception ex)
        {
            return new Outcome(false, $"parse threw {ex.GetType().Name}: {ex.Message}");
        }

        var interpreter = new DeterministicInterpreter(machine)
        {
            // The ScxmlDataModel contract: drain parked platform events after every step.
            PostTransitionEvents = DrainScxmlEvents,

            // <send target="#_scxml_<sessionid>"> addresses a session by its SCXML _sessionid.
            ActorAddress = snapshot =>
                snapshot is State<ScxmlDataModel> state ? state.Context.SessionId : null,

            // SCXML §5.10: events an invoked child sends up carry the invokeid it was started with.
            DecorateChildEvent = (evt, childId) =>
                evt is ScxmlEvent scxml ? scxml with { InvokeId = childId } : evt,

            // SCXML §6.2: a send that cannot be delivered raises error.communication on the sender.
            UndeliverableEvent = send => ScxmlEvent.CommunicationError(
                $"Cannot deliver '{send.Event.Type}' to '{send.Target}'.") with
            {
                SendId = send.Id
            }
        };

        try
        {
            interpreter.Start();
            interpreter.RunToCompletion(VirtualTimeCap);
        }
        catch (Exception ex)
        {
            return new Outcome(false, $"run threw {ex.GetType().Name}: {ex.Message}{Describe(interpreter)}");
        }

        var snapshot = (State<ScxmlDataModel>)interpreter.RootSnapshot;

        if (snapshot.Status is SnapshotStatus.Error)
        {
            return new Outcome(false, $"machine errored: {snapshot.Error?.Message}{Describe(interpreter)}");
        }

        if (snapshot.Configuration.Contains("pass"))
        {
            return new Outcome(true, "reached #pass");
        }

        if (snapshot.Configuration.Contains("fail"))
        {
            return new Outcome(false, $"reached #fail{Describe(interpreter)}");
        }

        return new Outcome(
            false,
            $"never reached #pass (status {snapshot.Status}, active: " +
            $"{string.Join(", ", snapshot.ActiveLeafStates)}){Describe(interpreter)}");
    }

    private static IEnumerable<MachineEvent> DrainScxmlEvents(ISnapshot snapshot) =>
        snapshot is State<ScxmlDataModel> state
            ? state.Context.DrainPendingInternalEvents()
            : [];

    /// <summary>Resolves <c>&lt;invoke src&gt;</c> / <c>&lt;data src&gt;</c> against the corpus directory.</summary>
    private static string ResolveSrc(string src)
    {
        var relative = src.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? src["file:".Length..]
            : src;

        relative = relative.TrimStart('/');

        var resolved = Path.IsPathRooted(relative) ? relative : Path.Combine(W3cDirectory, relative);
        return File.ReadAllText(resolved);
    }

    private static string Describe(DeterministicInterpreter interpreter)
    {
        var parts = new List<string>();

        if (interpreter.Log.Count > 0)
        {
            parts.Add("log: " + string.Join(
                " | ",
                interpreter.Log.Take(10).Select(e => $"{e.Label}={e.Message}")));
        }

        if (interpreter.Diagnostics.Count > 0)
        {
            parts.Add("diagnostics: " + string.Join(" | ", interpreter.Diagnostics.Take(5)));
        }

        return parts.Count == 0 ? string.Empty : "; " + string.Join("; ", parts);
    }
}
