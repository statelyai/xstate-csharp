using XState.TestKit;
using Xunit;

namespace XState.Scxml.Tests;

/// <summary>
/// <c>&lt;invoke&gt;</c> behaviour that only shows up across documents or across actors:
/// the eager parse of nested documents, per-actor <c>srcexpr</c> resolution, and SCXML's
/// end-of-macrostep invocation order.
/// </summary>
public class ScxmlInvocationTests
{
    private const string Ns = "xmlns=\"http://www.w3.org/2005/07/scxml\"";

    private static string Document(string body, string attributes = "") =>
        $"<scxml {Ns} version=\"1.0\" datamodel=\"ecmascript\" {attributes}>{body}</scxml>";

    private static StateMachine<ScxmlDataModel> Parse(string body, string attributes = "") =>
        ScxmlConverter.Parse(Document(body, attributes));

    private static ScxmlEvent Event(string name) => new(name);

    // --- Eager nested-document parse: cycles and depth ---

    [Fact]
    public void SelfReferencingInvokeSrcIsRejectedInsteadOfOverflowingTheStack()
    {
        var document = Document("<state id=\"s0\"><invoke src=\"loop.scxml\"/></state>", "initial=\"s0\"");

        // The resolver hands back the very document that asked for it. Nested documents are parsed
        // eagerly, so without the guard this recurses until the process dies on a StackOverflow.
        var error = Assert.Throws<InvalidOperationException>(
            () => ScxmlConverter.Parse(document, _ => document));

        Assert.Contains("cycle", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("loop.scxml -> loop.scxml", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnboundedInvokeSrcNestingIsCappedByDepth()
    {
        // Every document names a *different* source, so there is no repeated src to spot as a
        // cycle — only the depth cap terminates the parse.
        var depth = 0;

        var error = Assert.Throws<InvalidOperationException>(() => ScxmlConverter.Parse(
            Document("<state id=\"s0\"><invoke src=\"doc0.scxml\"/></state>", "initial=\"s0\""),
            _ => Document($"<state id=\"s0\"><invoke src=\"doc{++depth}.scxml\"/></state>", "initial=\"s0\"")));

        Assert.Contains("nesting exceeded", error.Message, StringComparison.Ordinal);
        Assert.Contains("doc0.scxml", error.Message, StringComparison.Ordinal);
    }

    // --- Unhandled platform errors ---

    [Fact]
    public void AnUnhandledInvocationErrorLeavesTheSessionRunning()
    {
        // SCXML §3.12/§6.4.1: a platform error nobody has a transition for is discarded. The core
        // otherwise faults a session on an unhandled ErrorActorEvent, which would kill any
        // conforming document whose invocation fails.
        var machine = ScxmlConverter.Parse(
            Document(
                """
                <state id="s0">
                  <invoke id="child" srcexpr="'missing.scxml'"/>
                  <transition event="go" target="s1"/>
                </state>
                <state id="s1"/>
                """,
                "initial=\"s0\""),
            _ => throw new FileNotFoundException("no such document"));

        var interpreter = Interpreter(machine, input: null);
        interpreter.Start();
        interpreter.RunUntilQuiescent();

        var afterFailure = Assert.IsType<State<ScxmlDataModel>>(interpreter.RootSnapshot);
        Assert.Equal(SnapshotStatus.Active, afterFailure.Status);
        Assert.True(afterFailure.Matches("#s0"));
        Assert.False(interpreter.IsRunning("child"));

        // The session is still live: a later event is still processed.
        interpreter.Send(Event("go"));
        interpreter.RunUntilQuiescent();

        var afterEvent = Assert.IsType<State<ScxmlDataModel>>(interpreter.RootSnapshot);
        Assert.Equal(SnapshotStatus.Active, afterEvent.Status);
        Assert.True(afterEvent.Matches("#s1"));
    }

    // --- <invoke srcexpr>: one parsed parent, many actors ---

    [Fact]
    public void ConcurrentActorsOfOneParentResolveAndStepTheirOwnSrcexprDocuments()
    {
        // Two documents with disjoint state ids: stepping one's snapshot against the other's
        // machine cannot silently "work", it fails to find the node.
        var children = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a.scxml"] = Document(
                "<state id=\"a0\"><transition event=\"go\" target=\"a1\"/></state><state id=\"a1\"/>",
                "initial=\"a0\""),
            ["b.scxml"] = Document(
                "<state id=\"b0\"><transition event=\"go\" target=\"b1\"/></state><state id=\"b1\"/>",
                "initial=\"b0\"")
        };

        // One parsed parent, shared by both actors — the machine is immutable and reusable.
        var parent = ScxmlConverter.Parse(
            Document(
                """
                <datamodel><data id="Doc" expr="'a.scxml'"/></datamodel>
                <state id="s0"><invoke id="child" srcexpr="Doc"/></state>
                """,
                "initial=\"s0\""),
            src => children[src]);

        var first = Interpreter(parent, input: null);
        var second = Interpreter(parent, new Dictionary<string, object?> { ["Doc"] = "b.scxml" });

        first.Start();
        first.RunUntilQuiescent();
        second.Start();
        second.RunUntilQuiescent();

        Assert.True(Child(first).Matches("a0"));
        Assert.True(Child(second).Matches("b0"));

        // The first actor steps *after* the second one started: a resolved machine cached on the
        // shared <invoke> wrapper would step this snapshot against b.scxml and lose the child.
        first.SendTo("child", Event("go"));
        first.RunUntilQuiescent();

        Assert.True(first.IsRunning("child"));
        Assert.True(Child(first).Matches("a1"));
        Assert.True(Child(second).Matches("b0"));

        second.SendTo("child", Event("go"));
        second.RunUntilQuiescent();

        Assert.True(Child(second).Matches("b1"));
        Assert.Empty(first.Diagnostics);
        Assert.Empty(second.Diagnostics);
    }

    private static DeterministicInterpreter Interpreter(
        StateMachine<ScxmlDataModel> machine,
        object? input) =>
        new(machine, input)
        {
            PostTransitionEvents = snapshot => snapshot is State<ScxmlDataModel> state
                ? state.Context.DrainPendingInternalEvents()
                : []
        };

    private static State<ScxmlDataModel> Child(DeterministicInterpreter interpreter) =>
        Assert.IsType<State<ScxmlDataModel>>(interpreter.SnapshotOf("child"));

    // --- SCXML invocation order (StateMachine.DeferInvokes) ---

    [Fact]
    public void InvocationsAreStartedAfterTheEnteringStatesOnentry()
    {
        var machine = Parse(
            """
            <datamodel><data id="Var1" expr="1"/></datamodel>
            <state id="s0">
              <onentry><assign location="Var1" expr="2"/></onentry>
              <invoke type="scxml">
                <content>
                  <scxml initial="sub0" version="1.0" datamodel="ecmascript"><final id="sub0"/></scxml>
                </content>
                <param name="seed" expr="Var1"/>
              </invoke>
            </state>
            """,
            "initial=\"s0\"");

        var spawn = Assert.Single(machine.GetInitialState().Effects.OfType<SpawnEffect>());
        var input = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(spawn.Input);

        // 1 if the invocation were started before the entry actions, as xstate v6 does.
        Assert.Equal(2d, input["seed"]);
    }

    [Fact]
    public void AnUnsupportedInvokeTypeIsReportedWhenTheInvocationStarts()
    {
        // SCXML §6.4: an unsupported <invoke type> is a run-time error on the invoking state, not a
        // malformed document — the document still loads and the session keeps running, exactly as
        // for the `typeexpr` form whose value is only known then.
        var machine = Parse(
            """
            <state id="s0">
              <invoke type="http://example.com/other">
                <content>
                  <scxml initial="sub0" version="1.0" datamodel="ecmascript"><final id="sub0"/></scxml>
                </content>
              </invoke>
            </state>
            """,
            "initial=\"s0\"");

        var result = machine.GetInitialState();

        Assert.True(result.State.Matches("#s0"));

        var error = Assert.Single(result.State.Context.PendingInternalEvents.OfType<ScxmlEvent>());

        Assert.Equal("error.execution", error.Name);
        Assert.Contains("http://example.com/other", error.Data as string ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedParamEvaluationCancelsTheInvocation()
    {
        // SCXML §6.4.1 (test554): when the evaluation of <invoke>'s arguments fails, the processor
        // raises error.execution and does *not* start the child.
        var machine = Parse(
            """
            <state id="s0">
              <invoke id="child" type="scxml" namelist="&#34;foo">
                <content>
                  <scxml initial="sub0" version="1.0" datamodel="ecmascript"><final id="sub0"/></scxml>
                </content>
              </invoke>
            </state>
            """,
            "initial=\"s0\"");

        var result = machine.GetInitialState();

        Assert.Empty(result.Effects.OfType<SpawnEffect>());

        var error = Assert.Single(result.State.Context.PendingInternalEvents.OfType<ScxmlEvent>());
        Assert.Equal("error.execution", error.Name);
    }

    [Fact]
    public void AutoforwardCopiesExternalEventsToTheChild()
    {
        var machine = Parse(
            """
            <state id="s0">
              <invoke id="child" type="scxml" autoforward="true">
                <content>
                  <scxml initial="sub0" version="1.0" datamodel="ecmascript"><state id="sub0"/></scxml>
                </content>
              </invoke>
              <transition event="go" target="s1"/>
            </state>
            <state id="s1"/>
            """,
            "initial=\"s0\"");

        var started = machine.GetInitialState();
        Assert.Single(started.Effects.OfType<SpawnEffect>());

        var forwarded = machine.Transition(started.State, Event("go"));

        // The event reaches the child as well as being handled by the parent.
        var send = Assert.Single(forwarded.Effects.OfType<SendToEffect>());
        Assert.Equal("child", send.Target);
        Assert.Equal("go", send.Event.Type);
        Assert.True(forwarded.State.Matches("#s1"));

        // Internal xstate.* bookkeeping is never forwarded.
        Assert.Empty(machine.Transition(started.State, new DoneStateEvent("nobody", null)).Effects
            .OfType<SendToEffect>());
    }

    [Fact]
    public void FinalizeRunsAgainstTheParentDatamodelBeforeTransitionsAreSelected()
    {
        // SCXML §3.13 (test233): <finalize> must have assigned Var1 by the time the transition's
        // cond is evaluated. Only the invocation the event came from runs its finalize (test234),
        // which is what _event.invokeid identifies.
        var machine = Parse(
            """
            <datamodel><data id="Var1" expr="1"/></datamodel>
            <state id="s0">
              <invoke id="child" type="scxml">
                <content>
                  <scxml initial="sub0" version="1.0" datamodel="ecmascript"><state id="sub0"/></scxml>
                </content>
                <finalize><assign location="Var1" expr="_event.data.aParam"/></finalize>
              </invoke>
              <transition event="childToParent" cond="Var1==2" target="pass"/>
              <transition event="childToParent" target="fail"/>
            </state>
            <state id="pass"/>
            <state id="fail"/>
            """,
            "initial=\"s0\"");

        var payload = new Dictionary<string, object?> { ["aParam"] = 2 };

        var fromChild = machine.Transition(
            machine.GetInitialState().State,
            new ScxmlEvent("childToParent", payload) { InvokeId = "child" });

        Assert.True(fromChild.State.Matches("#pass"));

        // An event that did not come from this invocation runs no finalize, so the cond still
        // sees Var1 == 1. (A fresh initial state, because the Jint datamodel behind a snapshot is
        // the same engine either way.)
        var fromElsewhere = machine.Transition(
            machine.GetInitialState().State,
            new ScxmlEvent("childToParent", payload));

        Assert.True(fromElsewhere.State.Matches("#fail"));
    }

    [Fact]
    public void AStateEnteredAndExitedWithinOneMacrostepNeverInvokes()
    {
        var machine = Parse(
            """
            <state id="s0">
              <transition target="s1"/>
              <invoke type="scxml">
                <content>
                  <scxml initial="sub0" version="1.0" datamodel="ecmascript"><final id="sub0"/></scxml>
                </content>
              </invoke>
            </state>
            <state id="s1"/>
            """,
            "initial=\"s0\"");

        var result = machine.GetInitialState();

        Assert.True(result.State.Matches("#s1"));
        Assert.Empty(result.Effects.OfType<SpawnEffect>());

        // Nothing started, so nothing is stopped either.
        Assert.Empty(result.Effects.OfType<StopChildEffect>());
    }
}
