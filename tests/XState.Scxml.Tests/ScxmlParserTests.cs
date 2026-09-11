using System.Xml.Linq;
using XState.Builder;
using XState.Scxml;
using Xunit;

namespace XState.Scxml.Tests;

public class ScxmlParserTests
{
    private const string Ns = "xmlns=\"http://www.w3.org/2005/07/scxml\"";

    private static string Document(string body, string attributes = "") =>
        $"<scxml {Ns} version=\"1.0\" datamodel=\"ecmascript\" {attributes}>{body}</scxml>";

    private static StateMachine<ScxmlDataModel> Parse(string body, string attributes = "") =>
        ScxmlConverter.Parse(Document(body, attributes));

    private static ScxmlEvent Event(string name) => new(name);

    // --- Structure ---

    [Fact]
    public void ParsesMinimalMachine()
    {
        var machine = Parse(
            """
            <state id="s0"><transition event="go" target="s1"/></state>
            <state id="s1"/>
            """,
            "initial=\"s0\"");

        Assert.Equal("(machine)", machine.Id);
        Assert.Equal(["s0", "s1"], machine.Root.Children.Select(c => c.Key));

        var initial = machine.GetInitialState().State;
        Assert.True(initial.Matches("#s0"));

        var next = machine.Transition(initial, Event("go")).State;
        Assert.True(next.Matches("#s1"));
    }

    [Fact]
    public void ParsesNameAndDefaultsInitialToFirstChild()
    {
        var machine = Parse("<state id=\"a\"/><state id=\"b\"/>", "name=\"lights\"");

        Assert.Equal("lights", machine.Id);
        Assert.True(machine.GetInitialState().State.Matches("#a"));
    }

    [Fact]
    public void ParsesHierarchyWithInitialAttributeAndInitialElement()
    {
        var machine = Parse(
            """
            <state id="outer" initial="inner2">
              <state id="inner1"/>
              <state id="inner2">
                <initial><transition target="leaf2"/></initial>
                <state id="leaf1"/>
                <state id="leaf2"/>
              </state>
            </state>
            """);

        var initial = machine.GetInitialState().State;

        Assert.True(initial.Matches("#outer"));
        Assert.True(initial.Matches("#inner2"));
        Assert.True(initial.Matches("#leaf2"));
        Assert.False(initial.Matches("#leaf1"));
    }

    [Fact]
    public void RootInitialElementContentRunsExactlyOnce()
    {
        // An <initial> aiming past its own children is an ordinary deep initial transition, and
        // its executable content rides on that transition — run once, and only on default entry.
        var grandchild = Parse(
            """
            <initial><transition target="leaf"><log label="init" expr="'once'"/></transition></initial>
            <state id="outer"><state id="leaf"/><state id="other"/></state>
            """);

        var state = grandchild.GetInitialState();
        Assert.True(state.State.Matches("#leaf"));
        Assert.Single(LogParamsOf(state.Effects), l => l.Label == "init");

        var directChild = Parse(
            """
            <initial><transition target="outer"><log label="init" expr="'once'"/></transition></initial>
            <state id="outer"/>
            """);

        var direct = directChild.GetInitialState();
        Assert.True(direct.State.Matches("#outer"));
        Assert.Single(LogParamsOf(direct.Effects), l => l.Label == "init");
    }

    [Fact]
    public void InitialElementContentRunsOnlyOnDefaultEntry()
    {
        // SCXML §3.3: the <initial> transition's content is executed when the state is entered by
        // default. A transition that names a descendant outright enters the state without taking
        // its initial transition, so the content must not run.
        var machine = Parse(
            """
            <state id="b">
              <transition event="deep" target="a2"/>
              <transition event="shallow" target="a"/>
            </state>
            <state id="a">
              <initial><transition target="a1"><log label="init" expr="'default'"/></transition></initial>
              <state id="a1"/>
              <state id="a2"/>
            </state>
            """,
            "initial=\"b\"");

        var start = machine.GetInitialState().State;

        var deep = machine.Transition(start, Event("deep"));
        Assert.True(deep.State.Matches("#a2"));
        Assert.DoesNotContain(LogParamsOf(deep.Effects), l => l.Label == "init");

        var shallow = machine.Transition(start, Event("shallow"));
        Assert.True(shallow.State.Matches("#a1"));
        Assert.Single(LogParamsOf(shallow.Effects), l => l.Label == "init");
    }

    [Fact]
    public void HistoryDefaultTransitionContentRunsOnlyWhenTheDefaultIsTaken()
    {
        // SCXML §3.6: a history state's default transition is executable content, run only when
        // the state is entered with nothing recorded.
        var machine = Parse(
            """
            <state id="away">
              <transition event="into" target="s0"/>
              <transition event="back" target="h"/>
            </state>
            <state id="s0" initial="s01">
              <history id="h"><transition target="s02"><log label="hist" expr="'default'"/></transition></history>
              <transition event="out" target="away"/>
              <state id="s01"><transition event="next" target="s02"/></state>
              <state id="s02"/>
            </state>
            """,
            "initial=\"away\"");

        var away = machine.GetInitialState().State;

        // Nothing recorded: the default target is entered and its content runs.
        var fresh = machine.Transition(away, Event("back"));
        Assert.True(fresh.State.Matches("#s02"));
        Assert.Single(LogParamsOf(fresh.Effects), l => l.Label == "hist");

        // With s01 recorded, the default is not taken and its content must not run.
        var left = machine.Transition(machine.Transition(away, Event("into")).State, Event("out"));
        var replayed = machine.Transition(left.State, Event("back"));

        Assert.True(replayed.State.Matches("#s01"));
        Assert.DoesNotContain(LogParamsOf(replayed.Effects), l => l.Label == "hist");
    }

    /// <summary>The <c>xstate.log</c> action effects of a transition, as their parameters.</summary>
    private static IEnumerable<LogParams> LogParamsOf(IEnumerable<Effect> effects) =>
        effects.OfType<ActionEffect>()
            .Where(a => a.Type == "xstate.log")
            .Select(a => (LogParams)a.Params!);

    [Fact]
    public void ParsesParallelRegions()
    {
        var machine = Parse(
            """
            <parallel id="p">
              <state id="a" initial="a1"><state id="a1"/><state id="a2"/></state>
              <state id="b" initial="b1"><state id="b1"/><state id="b2"/></state>
            </parallel>
            """);

        Assert.Equal(StateNodeType.Parallel, machine.GetNodeById("p").Type);

        var initial = machine.GetInitialState().State;
        Assert.True(initial.Matches("#a1"));
        Assert.True(initial.Matches("#b1"));
    }

    [Fact]
    public void ParsesHistoryWithDefaultTransition()
    {
        var machine = Parse(
            """
            <state id="s0" initial="s01">
              <history id="hist" type="shallow"><transition target="s02"/></history>
              <state id="s01"><transition event="next" target="s02"/></state>
              <state id="s02"><transition event="out" target="other"/></state>
            </state>
            <state id="other"><transition event="back" target="hist"/></state>
            """);

        var history = machine.GetNodeById("s0.hist");
        Assert.Equal(StateNodeType.History, history.Type);
        Assert.Equal(HistoryType.Shallow, history.HistoryType);
        Assert.Equal("s02", Assert.Single(history.HistoryDefault!.Targets).Id);

        var state = machine.GetInitialState().State;
        state = machine.Transition(state, Event("next")).State;
        Assert.True(state.Matches("#s02"));

        state = machine.Transition(state, Event("out")).State;
        Assert.True(state.Matches("#other"));

        // History replays s02, not the initial s01.
        state = machine.Transition(state, Event("back")).State;
        Assert.True(state.Matches("#s02"));
    }

    [Fact]
    public void GeneratedIdsOfIdLessStatesCannotCollideWithEventTokenPrefixes()
    {
        // The inner compound has no id, so the parser generates one from its parent's. A '.' in
        // that generated id would make its done event ("done.state.s1<sep>state0") token-prefix-
        // match the parent's own <transition event="done.state.s1">, firing it a level too early.
        var machine = Parse(
            """
            <state id="s1">
              <transition event="done.state.s1" target="fail"/>
              <transition event="done.state" target="pass"/>
              <state>
                <state id="inner"><transition event="go" target="innerFinal"/></state>
                <final id="innerFinal"/>
              </state>
            </state>
            <state id="pass"/>
            <state id="fail"/>
            """,
            "initial=\"s1\"");

        var state = machine.GetInitialState().State;
        Assert.True(state.Matches("#inner"));

        // Completing the *inner* compound must not look like s1 completing.
        var next = machine.Transition(state, Event("go")).State;

        Assert.True(next.Matches("#pass"));
        Assert.False(next.Matches("#fail"));
    }

    [Fact]
    public void PathologicallyDeepNestingIsRejectedInsteadOfOverflowingTheStack()
    {
        // Every parse pass (id assignment, the builder tree, metadata) recurses once per level,
        // as does the core resolver, so an unbounded document would kill the process outright.
        var error = Assert.Throws<InvalidOperationException>(() => Parse(Nested(2000)));

        Assert.Contains("nesting exceeded the limit", error.Message, StringComparison.Ordinal);

        // Well inside the cap: still parses and runs.
        Assert.True(Parse(Nested(500)).GetInitialState().State.Matches("#deep"));
    }

    /// <summary>A chain of <paramref name="depth"/> id-less states around a <c>#deep</c> leaf.</summary>
    private static string Nested(int depth) =>
        string.Concat(Enumerable.Repeat("<state>", depth)) +
        "<state id=\"deep\"/>" +
        string.Concat(Enumerable.Repeat("</state>", depth));

    // --- Guards ---

    [Fact]
    public void EvaluatesTransitionCondAgainstTheDatamodel()
    {
        var machine = Parse(
            """
            <datamodel><data id="Var1" expr="3"/></datamodel>
            <state id="s0">
              <transition event="go" cond="Var1 &gt; 5" target="high"/>
              <transition event="go" cond="Var1 &gt; 1" target="mid"/>
              <transition event="go" target="low"/>
            </state>
            <state id="high"/><state id="mid"/><state id="low"/>
            """);

        var state = machine.GetInitialState().State;
        Assert.Equal(3d, state.Context.GetValue("Var1"));

        Assert.True(machine.Transition(state, Event("go")).State.Matches("#mid"));
    }

    [Fact]
    public void SupportsTheInPredicate()
    {
        var machine = Parse(
            """
            <parallel id="p">
              <state id="a" initial="a1">
                <state id="a1"><transition event="go" cond="In('b2')" target="a2"/></state>
                <state id="a2"/>
              </state>
              <state id="b" initial="b1">
                <state id="b1"><transition event="flip" target="b2"/></state>
                <state id="b2"/>
              </state>
            </parallel>
            """);

        var state = machine.GetInitialState().State;

        // b2 is not active yet, so the guard blocks the transition.
        Assert.True(machine.Transition(state, Event("go")).State.Matches("#a1"));

        state = machine.Transition(state, Event("flip")).State;
        Assert.True(state.Matches("#b2"));
        Assert.True(machine.Transition(state, Event("go")).State.Matches("#a2"));
    }

    [Fact]
    public void MatchesEventDescriptorsByTokenPrefix()
    {
        var machine = Parse(
            """
            <state id="s0">
              <transition event="error" target="caught"/>
              <transition event="a.b other" target="multi"/>
              <transition event="*" target="wildcard"/>
            </state>
            <state id="caught"/><state id="multi"/><state id="wildcard"/>
            """);

        var initial = machine.GetInitialState().State;

        Assert.True(machine.Transition(initial, Event("error.execution")).State.Matches("#caught"));
        Assert.True(machine.Transition(initial, Event("a.b.c")).State.Matches("#multi"));
        Assert.True(machine.Transition(initial, Event("other")).State.Matches("#multi"));
        Assert.True(machine.Transition(initial, Event("zzz")).State.Matches("#wildcard"));
    }

    [Fact]
    public void MapsTransitionTypeToTheTransitionDomain()
    {
        var machine = Parse(
            """
            <state id="p" initial="c1">
              <transition event="int" type="internal" target="c1"/>
              <transition event="ext" type="external" target="c1"/>
              <transition event="def" target="c1"/>
              <transition event="none"/>
              <state id="c1"/>
            </state>
            """);

        var transitions = machine.GetNodeById("p").Transitions;

        // The declared SCXML type becomes the core's explicit transition domain, which is literal
        // SCXML getTransitionDomain — §3.13 eligibility included.
        Assert.Equal(TransitionDomain.Internal, transitions[0].Domain);
        Assert.Equal(TransitionDomain.External, transitions[1].Domain);

        // SCXML's default transition type is "external".
        Assert.Equal(TransitionDomain.External, transitions[2].Domain);

        // A targetless transition has no domain at all: nothing is exited or entered.
        Assert.Null(transitions[3].Domain);
    }

    // --- Executable content ---

    [Fact]
    public void RaisedEventsSettleWithinASingleTransition()
    {
        var machine = Parse(
            """
            <state id="s0">
              <onentry><raise event="foo"/><raise event="bar"/></onentry>
              <transition event="foo" target="s1"/>
              <transition event="*" target="fail"/>
            </state>
            <state id="s1">
              <transition event="bar" target="pass"/>
              <transition event="*" target="fail"/>
            </state>
            <final id="pass"/>
            <state id="fail"/>
            """,
            "initial=\"s0\"");

        var result = machine.GetInitialState();

        Assert.True(result.State.Matches("#pass"));
        Assert.Equal(SnapshotStatus.Done, result.State.Status);
    }

    [Fact]
    public void IfElseIfElseExecutesExactlyOneBranch()
    {
        var machine = Parse(
            """
            <datamodel><data id="Var1" expr="0"/></datamodel>
            <state id="s0">
              <onentry>
                <if cond="false">
                  <raise event="foo"/><assign location="Var1" expr="1"/>
                <elseif cond="true"/>
                  <raise event="bar"/><assign location="Var1" expr="2"/>
                <else/>
                  <raise event="baz"/><assign location="Var1" expr="3"/>
                </if>
              </onentry>
              <transition event="bar" target="pass"/>
              <transition event="*" target="fail"/>
            </state>
            <state id="pass"/><state id="fail"/>
            """,
            "initial=\"s0\"");

        var result = machine.GetInitialState();

        Assert.True(result.State.Matches("#pass"));
        Assert.Equal(2d, result.State.Context.GetValue("Var1"));
    }

    [Fact]
    public void ForeachIteratesAndDeclaresItsVariables()
    {
        var machine = Parse(
            """
            <datamodel>
              <data id="Total" expr="0"/>
              <data id="Items">[1,2,3,4]</data>
            </datamodel>
            <state id="s0">
              <onentry>
                <foreach item="Item" index="Idx" array="Items">
                  <assign location="Total" expr="Total + Item"/>
                </foreach>
              </onentry>
            </state>
            """);

        var state = machine.GetInitialState().State;

        Assert.Equal(10d, state.Context.GetValue("Total"));
        Assert.Equal(4d, state.Context.GetValue("Item"));
        Assert.Equal(3d, state.Context.GetValue("Idx"));
    }

    [Fact]
    public void SendProducesSendToEffectsWithParsedDelays()
    {
        var machine = Parse(
            """
            <datamodel><data id="Var1" expr="'from-expr'"/></datamodel>
            <state id="s0">
              <onentry>
                <send event="a" delay="1s"/>
                <send event="b" delay="100ms"/>
                <send event="c" delay="500"/>
                <send eventexpr="Var1"/>
                <send event="internal" target="#_internal"/>
                <send event="up" target="#_parent"/>
                <send event="withData" id="sid"><param name="x" expr="7"/></send>
              </onentry>
              <transition event="internal" target="s1"/>
            </state>
            <state id="s1"/>
            """);

        var sends = machine.GetInitialState().Effects.OfType<SendToEffect>().ToList();

        Assert.Equal(TimeSpan.FromSeconds(1), sends[0].Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(100), sends[1].Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(500), sends[2].Delay);

        Assert.Null(sends[3].Delay);
        // A targetless <send> is a send to the sender's own *external* queue.
        Assert.Equal(EffectTarget.Self, sends[3].Target);
        Assert.Equal("from-expr", sends[3].Event.Type);

        // target="#_internal" becomes a raise, so it never reaches the effect list.
        Assert.DoesNotContain(sends, s => s.Event.Type == "internal");

        var parent = Assert.Single(sends, s => s.Target == "#_parent");
        Assert.Equal("up", parent.Event.Type);

        var withData = Assert.Single(sends, s => s.Id == "sid");
        var payload = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            ((ScxmlEvent)withData.Event).Data);
        Assert.Equal(7d, payload["x"]);

        // The internal send drove the machine to s1 without an external round trip.
        Assert.True(machine.GetInitialState().State.Matches("#s1"));
    }

    [Fact]
    public void CancelProducesACancelEffect()
    {
        var machine = Parse(
            """
            <datamodel><data id="Var1" expr="'dynamic'"/></datamodel>
            <state id="s0">
              <onentry><cancel sendid="static"/><cancel sendidexpr="Var1"/></onentry>
            </state>
            """);

        var cancels = machine.GetInitialState().Effects.OfType<CancelEffect>().Select(c => c.SendId).ToList();

        Assert.Equal(["static", "dynamic"], cancels);
    }

    [Fact]
    public void UnsupportedSendTargetAndTypeRaiseExecutionErrors()
    {
        var machine = Parse(
            """
            <state id="s0">
              <onentry><send target="baz" event="event2"/></onentry>
              <transition event="error.execution" target="pass"/>
              <transition event="*" target="fail"/>
            </state>
            <state id="pass"/><state id="fail"/>
            """,
            "initial=\"s0\"");

        Assert.True(machine.GetInitialState().State.Matches("#pass"));

        var badType = Parse(
            """
            <state id="s0">
              <onentry><send type="27" event="event1"/></onentry>
              <transition event="error.execution" target="pass"/>
              <transition event="*" target="fail"/>
            </state>
            <state id="pass"/><state id="fail"/>
            """,
            "initial=\"s0\"");

        Assert.True(badType.GetInitialState().State.Matches("#pass"));
    }

    [Fact]
    public void FailedCondEvaluatesFalseAndParksAnExecutionError()
    {
        var machine = Parse(
            """
            <state id="s0">
              <transition event="go" cond="Nope.missing" target="bad"/>
              <transition event="go" target="good"/>
            </state>
            <state id="bad"/><state id="good"/>
            """);

        var state = machine.GetInitialState().State;
        Assert.Empty(state.Context.PendingInternalEvents);

        var next = machine.Transition(state, Event("go")).State;
        Assert.True(next.Matches("#good"));

        // The harness contract: drain after every Transition.
        var drained = next.Context.DrainPendingInternalEvents();
        var error = Assert.IsType<ScxmlEvent>(Assert.Single(drained));
        Assert.Equal("error.execution", error.Name);
        Assert.Equal(ScxmlEventKind.Platform, error.Kind);
        Assert.Empty(next.Context.PendingInternalEvents);
    }

    // --- donedata / invoke ---

    [Fact]
    public void DonedataBecomesTheMachineOutput()
    {
        var machine = Parse(
            """
            <datamodel><data id="Var1" expr="6"/></datamodel>
            <state id="s0"><transition event="go" target="done"/></state>
            <final id="done">
              <donedata>
                <param name="answer" expr="Var1 * 7"/>
                <param name="label" expr="'ok'"/>
              </donedata>
            </final>
            """);

        var state = machine.GetInitialState().State;
        var result = machine.Transition(state, Event("go")).State;

        Assert.Equal(SnapshotStatus.Done, result.Status);
        var output = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Output);
        Assert.Equal(42d, output["answer"]);
        Assert.Equal("ok", output["label"]);
    }

    [Fact]
    public void InlineInvokeContentParsesToAChildMachine()
    {
        var machine = Parse(
            """
            <state id="s0">
              <invoke type="scxml" autoforward="true">
                <content>
                  <scxml initial="sub0" version="1.0" datamodel="ecmascript">
                    <state id="sub0"><transition target="subFinal"/></state>
                    <final id="subFinal"/>
                  </scxml>
                </content>
                <finalize><assign location="Var1" expr="1"/></finalize>
                <param name="seed" expr="9"/>
              </invoke>
            </state>
            """);

        var invoke = Assert.Single(machine.GetNodeById("s0").Invokes);
        Assert.Equal("s0.invocation[0]", invoke.Id);

        var child = Assert.IsType<StateMachine<ScxmlDataModel>>(invoke.Logic);
        var childInitial = (State<ScxmlDataModel>)child.GetInitialState().State;
        Assert.Equal(SnapshotStatus.Done, childInitial.Status);

        var metadata = ScxmlConverter.GetInvokeMetadata(invoke);
        Assert.NotNull(metadata);
        Assert.True(metadata!.AutoForward);
        Assert.NotNull(metadata.Finalize);

        var spawn = Assert.Single(machine.GetInitialState().Effects.OfType<SpawnEffect>());
        Assert.Equal("s0.invocation[0]", spawn.Id);
        var input = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(spawn.Input);
        Assert.Equal(9d, input["seed"]);
    }

    // --- Serialization ---

    [Fact]
    public void SerializesAMultiTargetInitialAsIdrefsWithNoSynthesizedStates()
    {
        // A deep, cross-region initial is an ordinary initial transition — no transient state is
        // synthesized for it, so none may appear in the output, and `initial` is SCXML IDREFS.
        var machine = Parse(
            """
            <state id="s1">
              <parallel id="p">
                <state id="ra" initial="ra1"><state id="ra1"/><state id="ra2"/></state>
                <state id="rb" initial="rb1"><state id="rb1"/><state id="rb2"/></state>
              </parallel>
            </state>
            """,
            "initial=\"ra2 rb2\"");

        var document = ScxmlConverter.ToScxml(machine);
        var root = document.Root!;

        Assert.Equal("ra2 rb2", root.Attribute("initial")?.Value);
        Assert.DoesNotContain(
            document.Descendants(),
            e => e.Attribute("id")?.Value.Contains("(initial)", StringComparison.Ordinal) == true);

        var reparsed = ScxmlConverter.Parse(document);
        var state = reparsed.GetInitialState().State;
        Assert.True(state.Matches("#ra2"));
        Assert.True(state.Matches("#rb2"));
    }

    [Fact]
    public void SerializesAHistoryDefaultTransitionWithItsContent()
    {
        var machine = Parse(
            """
            <state id="s0" initial="s01">
              <history id="h"><transition target="s02"><raise event="defaulted"/></transition></history>
              <state id="s01"/>
              <state id="s02"/>
            </state>
            """);

        var history = ScxmlConverter.ToScxml(machine)
            .Descendants()
            .Single(e => e.Name.LocalName == "history");

        var transition = history.Elements().Single(e => e.Name.LocalName == "transition");
        Assert.Equal("s02", transition.Attribute("target")?.Value);
        Assert.Equal("raise", transition.Elements().Single().Name.LocalName);
    }

    [Fact]
    public void SerializesStructureBackToScxml()
    {
        var machine = Parse(
            """
            <state id="s0" initial="s01">
              <onentry><raise event="hi"/></onentry>
              <transition event="go" type="internal" target="s01"/>
              <state id="s01"/>
              <state id="s02"/>
            </state>
            <final id="done"/>
            """);

        var document = ScxmlConverter.ToScxml(machine);
        var root = document.Root!;

        Assert.Equal("scxml", root.Name.LocalName);
        Assert.Equal("http://www.w3.org/2005/07/scxml", root.Name.NamespaceName);
        Assert.Equal("s0", root.Attribute("initial")?.Value);

        var s0 = root.Elements().First(e => e.Attribute("id")?.Value == "s0");
        Assert.Equal("s01", s0.Attribute("initial")?.Value);

        var transition = s0.Elements().Single(e => e.Name.LocalName == "transition");
        Assert.Equal("go", transition.Attribute("event")?.Value);
        Assert.Equal("internal", transition.Attribute("type")?.Value);
        Assert.Equal("s01", transition.Attribute("target")?.Value);

        var onentry = s0.Elements().Single(e => e.Name.LocalName == "onentry");
        Assert.Equal("raise", onentry.Elements().Single().Name.LocalName);

        Assert.Contains(root.Elements(), e => e.Name.LocalName == "final");
    }

    [Fact]
    public void RoundTripsBindingAndStateLocalDatamodel()
    {
        // A state-local <datamodel> is read by the context factory / a synthesized entry action,
        // neither of which the core can hand back — so it has to be recovered from the node's
        // source element, and the binding it obeys has to survive with it.
        var machine = Parse(
            """
            <state id="s0">
              <datamodel><data id="local" expr="7"/></datamodel>
              <onentry><log label="local" expr="local"/></onentry>
            </state>
            """,
            "binding=\"late\" initial=\"s0\"");

        var document = ScxmlConverter.ToScxml(machine);

        Assert.Equal("late", document.Root!.Attribute("binding")?.Value);

        var s0 = document.Root.Elements().Single(e => e.Attribute("id")?.Value == "s0");
        var data = Assert.Single(
            s0.Elements().Single(e => e.Name.LocalName == "datamodel").Elements());

        Assert.Equal("local", data.Attribute("id")?.Value);
        Assert.Equal("7", data.Attribute("expr")?.Value);

        // And the emitted document still behaves: local is assigned on entry to s0.
        var reparsed = ScxmlConverter.Parse(document);
        var logged = Assert.Single(LogParamsOf(reparsed.GetInitialState().Effects), l => l.Label == "local");

        Assert.Equal(7d, logged.Message);
    }

    [Theory]
    [InlineData("test144.scxml")]
    [InlineData("test147.scxml")]
    [InlineData("test355.scxml")]
    [InlineData("test388.scxml")]
    public void RoundTripsVendoredW3CFilesStructurally(string file)
    {
        var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "w3c", file));

        var first = ScxmlConverter.Parse(xml);
        var second = ScxmlConverter.Parse(ScxmlConverter.ToScxml(first));

        Assert.Equal(Signature(first.Root), Signature(second.Root));
    }

    [Theory]
    [InlineData("1s", 1000)]
    [InlineData("1.5s", 1500)]
    [InlineData("100ms", 100)]
    [InlineData("500", 500)]
    public void ParsesCss2Delays(string text, double milliseconds)
    {
        Assert.True(ScxmlConverter.TryParseDelay(text, out var delay));
        Assert.Equal(milliseconds, delay.TotalMilliseconds);
        Assert.False(ScxmlConverter.TryParseDelay("soon", out _));
    }

    // --- <script src> ---

    [Fact]
    public void TopLevelScriptSrcIsFetchedThroughTheSameResolverAsDataSrc()
    {
        var machine = ScxmlConverter.Parse(
            Document(
                "<datamodel><data id=\"Var1\" expr=\"0\"/></datamodel>" +
                "<script src=\"boot.js\"/>" +
                "<state id=\"s0\"/>",
                "initial=\"s0\""),
            src => src == "boot.js" ? "Var1 = 42;" : throw new FileNotFoundException(src));

        Assert.Equal(42d, machine.GetInitialState().State.Context.GetValue("Var1"));
    }

    [Fact]
    public void AnUnfetchableTopLevelScriptSrcRejectsTheDocument()
    {
        // SCXML §5.8: the script is fetched when the document is loaded, and a document whose
        // script cannot be fetched is rejected — it must not run with the script silently skipped.
        var body = Document("<script src=\"missing.js\"/><state id=\"s0\"/>", "initial=\"s0\"");

        var error = Assert.Throws<NotSupportedException>(
            () => ScxmlConverter.Parse(body, _ => throw new FileNotFoundException("missing.js")));

        Assert.Contains("missing.js", error.Message, StringComparison.Ordinal);
        Assert.Contains("rejected", error.Message, StringComparison.Ordinal);

        // Without a resolver there is no way to fetch it at all — also a rejection, not a no-op.
        Assert.Throws<NotSupportedException>(() => ScxmlConverter.Parse(body));
    }

    [Fact]
    public void ExecutableScriptSrcIsFetchedAndFailsTheBlockWhenItCannotBe()
    {
        var machine = ScxmlConverter.Parse(
            Document(
                """
                <datamodel><data id="Var1" expr="0"/></datamodel>
                <state id="s0">
                  <onentry>
                    <script src="good.js"/>
                    <script src="missing.js"/>
                    <assign location="Var1" expr="99"/>
                  </onentry>
                  <transition event="error.execution" target="pass"/>
                  <transition event="*" target="fail"/>
                </state>
                <final id="pass"/><state id="fail"/>
                """,
                "initial=\"s0\""),
            src => src == "good.js" ? "Var1 = 7;" : throw new FileNotFoundException(src));

        var state = machine.GetInitialState().State;

        Assert.True(state.Matches("#pass"));

        // The fetched script ran; the element after the failed one did not (SCXML §4.1).
        Assert.Equal(7d, state.Context.GetValue("Var1"));
    }

    // --- binding="late" (IRP test307, which the harness cannot score) ---

    [Fact]
    public void LateBoundAccessBeforeDeclarationBehavesLikeAMissingSubstructure()
    {
        // test307 is a *manual* IRP test: it has no #pass state, and the verdict is whether reading
        // a not-yet-declared late-bound variable produces the same observable result as reading a
        // non-existent substructure of a declared one. Both must yield undefined with no error —
        // which is exactly what this asserts, so the manual comparison is machine-checked here
        // even though the conformance harness has nothing to score.
        var machine = ScxmlConverter.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "w3c", "test307.scxml")));

        var result = machine.GetInitialState();
        var logs = result.Effects.OfType<ActionEffect>()
            .Where(a => a.Type == "xstate.log")
            .Select(a => (LogParams)a.Params!)
            .ToList();

        Assert.True(result.State.Matches("#final"));

        // s0: Var1 is declared but unassigned until s1 is entered.
        Assert.Null(Assert.Single(logs, l => l.Label!.StartsWith("entering s0", StringComparison.Ordinal)).Message);
        Assert.Contains(logs, l => l.Label == "no error in s0");

        // s1: Var1 is 1, so Var1.bar is a missing substructure — same result, still no error.
        Assert.Null(Assert.Single(logs, l => l.Label!.StartsWith("entering s1", StringComparison.Ordinal)).Message);
        Assert.Contains(logs, l => l.Label == "No error in s1");

        Assert.DoesNotContain(logs, l => l.Label!.StartsWith("error in state", StringComparison.Ordinal));
    }

    // --- Serializing a machine the importer did not produce ---

    /// <summary>A machine over <see cref="ScxmlDataModel"/> built with the plain core builder.</summary>
    private static StateMachine<ScxmlDataModel> HandBuilt(Action<StateBuilder<ScxmlDataModel>> configure)
    {
        // The datamodel handle is only constructible by the importer, so it is borrowed from a
        // trivial parsed document; everything above it is ordinary core builder output with no
        // SCXML provenance attached.
        var context = Parse("<state id=\"s0\"/>").GetInitialState().State.Context;

        return Machine.Create<ScxmlDataModel>("handbuilt")
            .Context(_ => context)
            .Root(root => configure(root))
            .Build();
    }

    [Fact]
    public void SerializingAnActionWithNoScxmlFormIsRefused()
    {
        var machine = HandBuilt(root => root.State("s0", s => s.Entry(
            new ScriptAction<ScxmlDataModel>(args => new ScriptResult<ScxmlDataModel>(args.Context))
            { Name = "doSomething" })));

        var error = Assert.Throws<NotSupportedException>(() => ScxmlConverter.ToScxml(machine));
        Assert.Contains("doSomething", error.Message, StringComparison.Ordinal);

        // Opting in restores the placeholder document.
        var document = ScxmlConverter.ToScxml(machine, new ScxmlSerializationOptions { EmitPlaceholders = true });
        var onentry = document.Root!.Descendants().Single(e => e.Name.LocalName == "onentry");

        Assert.Equal("doSomething", onentry.Elements().Single().Attribute("label")?.Value);
    }

    [Fact]
    public void SerializingAGuardWithNoScxmlFormIsRefused()
    {
        // A guard is an opaque delegate: emitting the transition without a `cond` would turn a
        // conditional transition into an unconditional one, which is worse than refusing.
        var machine = HandBuilt(root => root
            .State("s0", s => s.On("go", t => t.Target("s1").Guard(_ => false)))
            .State("s1"));

        var error = Assert.Throws<NotSupportedException>(() => ScxmlConverter.ToScxml(machine));

        Assert.Contains("guard", error.Message, StringComparison.Ordinal);
        Assert.Contains("handbuilt.s0", error.Message, StringComparison.Ordinal);

        var document = ScxmlConverter.ToScxml(machine, new ScxmlSerializationOptions { EmitPlaceholders = true });
        var transition = document.Root!.Descendants().Single(e => e.Name.LocalName == "transition");

        Assert.Equal("go", transition.Attribute("event")?.Value);
        Assert.Null(transition.Attribute("cond"));
    }

    private static string Signature(StateNode<ScxmlDataModel> node)
    {
        var transitions = node.Transitions.Concat(node.Always).Select(t =>
        {
            var info = ScxmlConverter.GetTransitionInfo(t);
            var events = info is null ? "" : string.Join('|', info.EventTokens);
            var targets = string.Join('|', t.Targets.Select(x => x.Id));
            return $"[{events}>{targets}:{t.Reenter}]";
        });

        var children = node.Children.Select(Signature);

        return $"({node.Id}:{node.Type}:{node.HistoryType}" +
               $" T{string.Join(',', transitions)}" +
               $" C{string.Join(',', children)})";
    }
}
