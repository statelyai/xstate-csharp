using XState.TestKit;
using Xunit;

namespace XState.Tests;

/// <summary>
/// Semantics ported from the xstate v6 test suite
/// (<c>packages/core/test/{actions,parallel,history,final,after,invoke}.test.ts</c>).
/// Each test names the case it mirrors; the expected action logs are the TS expectations verbatim.
/// </summary>
public class PortedSemanticsTests
{
    private static MachineEvent Ev(string type) => new NamedEvent(type);

    private static DeterministicInterpreter Interpret(IMachineLogic logic, object? input = null) =>
        DeterministicInterpreter.Start(logic, input);

    // ======================================================================
    // A. Entry / exit action ordering (actions.test.ts)
    // ======================================================================

    [Fact] // 'should return the entry actions of an initial state (deep)'
    public void Entry_actions_of_an_initial_state_deep()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("(machine)")
            .Root(r => r.Entry(_ => tracked.Add("enter: __root__")))
            .Initial("a")
            .State("a", s => s
                .Entry(_ => tracked.Add("enter: a"))
                .Initial("a1")
                .State("a1", c => c
                    .Entry(_ => tracked.Add("enter: a.a1"))
                    .On("NEXT", t => t.Target("a2")))
                .State("a2")
                .On("CHANGE", t => t.Target("#(machine).b")))
            .State("b")
            .Build();

        Interpret(machine);

        Assert.Equal(["enter: __root__", "enter: a", "enter: a.a1"], tracked);
    }

    [Fact] // 'should return the entry actions of an initial state (parallel)'
    public void Entry_actions_of_an_initial_state_parallel()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("(machine)")
            .Root(r => r
                .AsParallel()
                .Entry(_ => tracked.Add("enter: __root__")))
            .State("a", s => s
                .Entry(_ => tracked.Add("enter: a"))
                .Initial("a1")
                .State("a1", c => c.Entry(_ => tracked.Add("enter: a.a1"))))
            .State("b", s => s
                .Entry(_ => tracked.Add("enter: b"))
                .Initial("b1")
                .State("b1", c => c.Entry(_ => tracked.Add("enter: b.b1"))))
            .Build();

        Interpret(machine);

        Assert.Equal(
            ["enter: __root__", "enter: a", "enter: a.a1", "enter: b", "enter: b.b1"],
            tracked);
    }

    [Fact] // 'should return the entry and exit actions of a deep transition'
    public void Entry_and_exit_actions_of_a_deep_transition()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("deep-transition")
            .Initial("green")
            .State("green", s => s
                .Entry(_ => tracked.Add("enter: green"))
                .Exit(_ => tracked.Add("exit: green"))
                .On("TIMER", t => t.Target("yellow")))
            .State("yellow", s => s
                .Entry(_ => tracked.Add("enter: yellow"))
                .Exit(_ => tracked.Add("exit: yellow"))
                .Initial("speed_up")
                .State("speed_up", c => c
                    .Entry(_ => tracked.Add("enter: yellow.speed_up"))
                    .Exit(_ => tracked.Add("exit: yellow.speed_up"))))
            .Build();

        var actor = Interpret(machine);
        tracked.Clear();
        actor.Send(Ev("TIMER"));

        Assert.Equal(["exit: green", "enter: yellow", "enter: yellow.speed_up"], tracked);
    }

    [Fact] // 'should exit and enter the state for reentering self-transitions (deep)'
    public void Reentering_self_transition_exits_and_enters_deeply()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("restart")
            .Initial("green")
            .State("green", s => s
                .Entry(_ => tracked.Add("enter: green"))
                .Exit(_ => tracked.Add("exit: green"))
                .On("RESTART", t => t.Target("green").Reenter())
                .Initial("walk")
                .State("walk", c => c
                    .Entry(_ => tracked.Add("enter: green.walk"))
                    .Exit(_ => tracked.Add("exit: green.walk")))
                .State("wait", c => c
                    .Entry(_ => tracked.Add("enter: green.wait"))
                    .Exit(_ => tracked.Add("exit: green.wait"))))
            .Build();

        var actor = Interpret(machine);
        tracked.Clear();
        actor.Send(Ev("RESTART"));

        Assert.Equal(
            ["exit: green.walk", "exit: green", "enter: green", "enter: green.walk"],
            tracked);
    }

    [Fact] // 'should return actions for parallel machines'
    public void Parallel_machine_exits_in_reverse_document_order_and_enters_in_document_order()
    {
        var actual = new List<string>();

        var machine = Machine.Create("parallel-actions")
            .Root(r => r.AsParallel())
            .State("a", s => s
                .Entry(_ => actual.Add("enter_a"))
                .Exit(_ => actual.Add("exit_a"))
                .Initial("a1")
                .State("a1", c => c
                    .Entry(_ => actual.Add("enter_a1"))
                    .Exit(_ => actual.Add("exit_a1"))
                    .On("CHANGE", t => t
                        .Do(_ => actual.Add("do_a2"))
                        .Do(_ => actual.Add("another_do_a2"))
                        .Target("a2")))
                .State("a2", c => c
                    .Entry(_ => actual.Add("enter_a2"))
                    .Exit(_ => actual.Add("exit_a2"))))
            .State("b", s => s
                .Entry(_ => actual.Add("enter_b"))
                .Exit(_ => actual.Add("exit_b"))
                .Initial("b1")
                .State("b1", c => c
                    .Entry(_ => actual.Add("enter_b1"))
                    .Exit(_ => actual.Add("exit_b1"))
                    .On("CHANGE", t => t
                        .Do(_ => actual.Add("do_b2"))
                        .Target("b2")))
                .State("b2", c => c
                    .Entry(_ => actual.Add("enter_b2"))
                    .Exit(_ => actual.Add("exit_b2"))))
            .Build();

        var actor = Interpret(machine);
        actual.Clear();
        actor.Send(Ev("CHANGE"));

        Assert.Equal(
            ["exit_b1", "exit_a1", "do_a2", "another_do_a2", "do_b2", "enter_a2", "enter_b2"],
            actual);
    }

    [Fact] // 'should return nested actions in the correct (child to parent) order'
    public void Nested_actions_run_child_to_parent_on_exit_and_parent_to_child_on_entry()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("nested-order")
            .Initial("a")
            .State("a", s => s
                .Entry(_ => tracked.Add("enter: a"))
                .Exit(_ => tracked.Add("exit: a"))
                .Initial("a1")
                .State("a1", c => c
                    .Entry(_ => tracked.Add("enter: a.a1"))
                    .Exit(_ => tracked.Add("exit: a.a1")))
                .On("CHANGE", t => t.Target("#nested-order.b")))
            .State("b", s => s
                .Entry(_ => tracked.Add("enter: b"))
                .Exit(_ => tracked.Add("exit: b"))
                .Initial("b1")
                .State("b1", c => c
                    .Entry(_ => tracked.Add("enter: b.b1"))
                    .Exit(_ => tracked.Add("exit: b.b1"))))
            .Build();

        var actor = Interpret(machine);
        tracked.Clear();
        actor.Send(Ev("CHANGE"));

        Assert.Equal(["exit: a.a1", "exit: a", "enter: b", "enter: b.b1"], tracked);
    }

    [Fact] // 'should exit children of parallel state nodes'
    public void Exiting_a_parallel_state_exits_all_its_regions_deepest_first()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("exit-parallel")
            .Initial("B")
            .State("A", s => s
                .Entry(_ => tracked.Add("enter: A"))
                .Exit(_ => tracked.Add("exit: A"))
                .On("to-B", t => t.Target("B")))
            .Parallel("B", p => p
                .Entry(_ => tracked.Add("enter: B"))
                .Exit(_ => tracked.Add("exit: B"))
                .On("to-A", t => t.Target("A"))
                .State("C", r => r
                    .Entry(_ => tracked.Add("enter: B.C"))
                    .Exit(_ => tracked.Add("exit: B.C"))
                    .Initial("C1")
                    .State("C1", c => c
                        .Entry(_ => tracked.Add("enter: B.C.C1"))
                        .Exit(_ => tracked.Add("exit: B.C.C1"))))
                .State("D", r => r
                    .Entry(_ => tracked.Add("enter: B.D"))
                    .Exit(_ => tracked.Add("exit: B.D"))
                    .Initial("D1")
                    .State("D1", c => c
                        .Entry(_ => tracked.Add("enter: B.D.D1"))
                        .Exit(_ => tracked.Add("exit: B.D.D1")))))
            .Build();

        var actor = Interpret(machine);
        tracked.Clear();
        actor.Send(Ev("to-A"));

        Assert.Equal(
            ["exit: B.D.D1", "exit: B.D", "exit: B.C.C1", "exit: B.C", "exit: B", "enter: A"],
            tracked);
    }

    [Fact] // "should reenter targeted ancestor (as it's a descendant of the transition domain)"
    public void Targeting_an_ancestor_by_id_reenters_it()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("ancestor-target")
            .Initial("loaded")
            .State("loaded", s => s
                .Id("loaded")
                .Entry(_ => tracked.Add("enter: loaded"))
                .Exit(_ => tracked.Add("exit: loaded"))
                .Initial("idle")
                .State("idle", c => c
                    .Entry(_ => tracked.Add("enter: loaded.idle"))
                    .Exit(_ => tracked.Add("exit: loaded.idle"))
                    .On("UPDATE", t => t.Target("#loaded"))))
            .Build();

        var actor = Interpret(machine);
        tracked.Clear();
        actor.Send(Ev("UPDATE"));

        Assert.Equal(
            ["exit: loaded.idle", "exit: loaded", "enter: loaded", "enter: loaded.idle"],
            tracked);
    }

    [Fact] // 'should exit current node and reenter target node when target is ancestor of current'
    public void Transition_to_an_ancestor_from_a_grandchild_reenters_the_ancestor()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("reenter-ancestor")
            .Initial("A")
            .State("A", s => s
                .Id("ancestor")
                .Entry(_ => tracked.Add("enter: A"))
                .Exit(_ => tracked.Add("exit: A"))
                .Initial("A1")
                .State("A1", c => c
                    .Entry(_ => tracked.Add("enter: A.A1"))
                    .Exit(_ => tracked.Add("exit: A.A1"))
                    .On("NEXT", t => t.Target("A2")))
                .State("A2", c => c
                    .Entry(_ => tracked.Add("enter: A.A2"))
                    .Exit(_ => tracked.Add("exit: A.A2"))
                    .Initial("A2_child")
                    .State("A2_child", g => g
                        .Entry(_ => tracked.Add("enter: A.A2.A2_child"))
                        .Exit(_ => tracked.Add("exit: A.A2.A2_child"))
                        .On("NEXT", t => t.Target("#ancestor")))))
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("NEXT"));
        tracked.Clear();
        actor.Send(Ev("NEXT"));

        Assert.Equal(
            ["exit: A.A2.A2_child", "exit: A.A2", "exit: A", "enter: A", "enter: A.A1"],
            tracked);
    }

    [Fact] // 'should enter all descendents when target is a descendent of the source when using an reentering transition'
    public void Reentering_transition_to_a_descendant_enters_all_descendants()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("reenter-descendant")
            .Initial("A")
            .State("A", s => s
                .Entry(_ => tracked.Add("enter: A"))
                .Exit(_ => tracked.Add("exit: A"))
                .Initial("A1")
                .On("NEXT", t => t.Target(".A2").Reenter())
                .State("A1", c => c
                    .Entry(_ => tracked.Add("enter: A.A1"))
                    .Exit(_ => tracked.Add("exit: A.A1")))
                .State("A2", c => c
                    .Entry(_ => tracked.Add("enter: A.A2"))
                    .Exit(_ => tracked.Add("exit: A.A2"))
                    .Initial("A2a")
                    .State("A2a", g => g
                        .Entry(_ => tracked.Add("enter: A.A2.A2a"))
                        .Exit(_ => tracked.Add("exit: A.A2.A2a")))))
            .Build();

        var actor = Interpret(machine);
        tracked.Clear();
        actor.Send(Ev("NEXT"));

        Assert.Equal(
            ["exit: A.A1", "exit: A", "enter: A", "enter: A.A2", "enter: A.A2.A2a"],
            tracked);
    }

    [Fact] // 'should exit deep descendant during a default self-transition'
    public void Default_self_transition_exits_descendants_but_not_the_source()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("self-default")
            .Initial("a")
            .State("a", s => s
                .Entry(_ => tracked.Add("enter: a"))
                .Exit(_ => tracked.Add("exit: a"))
                .On("EV", t => t.Target("a"))
                .Initial("a1")
                .State("a1", c => c
                    .Entry(_ => tracked.Add("enter: a.a1"))
                    .Exit(_ => tracked.Add("exit: a.a1"))
                    .Initial("a11")
                    .State("a11", g => g
                        .Entry(_ => tracked.Add("enter: a.a1.a11"))
                        .Exit(_ => tracked.Add("exit: a.a1.a11")))))
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("EV"));
        tracked.Clear();
        actor.Send(Ev("EV"));

        Assert.Equal(
            ["exit: a.a1.a11", "exit: a.a1", "enter: a.a1", "enter: a.a1.a11"],
            tracked);
    }

    [Fact] // 'should exit deep descendant during a reentering self-transition'
    public void Reentering_self_transition_also_exits_and_reenters_the_source()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("self-reenter")
            .Initial("a")
            .State("a", s => s
                .Entry(_ => tracked.Add("enter: a"))
                .Exit(_ => tracked.Add("exit: a"))
                .On("EV", t => t.Target("a").Reenter())
                .Initial("a1")
                .State("a1", c => c
                    .Entry(_ => tracked.Add("enter: a.a1"))
                    .Exit(_ => tracked.Add("exit: a.a1"))
                    .Initial("a11")
                    .State("a11", g => g
                        .Entry(_ => tracked.Add("enter: a.a1.a11"))
                        .Exit(_ => tracked.Add("exit: a.a1.a11")))))
            .Build();

        var actor = Interpret(machine);
        tracked.Clear();
        actor.Send(Ev("EV"));

        Assert.Equal(
            [
                "exit: a.a1.a11", "exit: a.a1", "exit: a",
                "enter: a", "enter: a.a1", "enter: a.a1.a11"
            ],
            tracked);
    }

    [Fact] // 'should not enter exited state when targeting its ancestor and when its former descendant gets selected through initial state'
    public void Targeting_an_ancestor_reenters_via_the_initial_state_not_the_exited_one()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("ancestor-initial")
            .Initial("a")
            .State("a", s => s
                .Id("parent")
                .Entry(_ => tracked.Add("enter: a"))
                .Exit(_ => tracked.Add("exit: a"))
                .Initial("a1")
                .State("a1", c => c
                    .Entry(_ => tracked.Add("enter: a.a1"))
                    .Exit(_ => tracked.Add("exit: a.a1"))
                    .On("EV", t => t.Target("a2")))
                .State("a2", c => c
                    .Entry(_ => tracked.Add("enter: a.a2"))
                    .Exit(_ => tracked.Add("exit: a.a2"))
                    .On("EV", t => t.Target("#parent"))))
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("EV"));
        tracked.Clear();
        actor.Send(Ev("EV"));

        Assert.Equal(["exit: a.a2", "exit: a", "enter: a", "enter: a.a1"], tracked);
    }

    [Fact] // 'should return entry action defined on parallel state'
    public void Entering_a_parallel_state_runs_its_own_entry_action_first()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("enter-parallel")
            .Initial("start")
            .State("start", s => s
                .Entry(_ => tracked.Add("enter: start"))
                .Exit(_ => tracked.Add("exit: start"))
                .On("ENTER_PARALLEL", t => t.Target("p1")))
            .Parallel("p1", p => p
                .Entry(_ => tracked.Add("enter: p1"))
                .Exit(_ => tracked.Add("exit: p1"))
                .State("nested", r => r
                    .Entry(_ => tracked.Add("enter: p1.nested"))
                    .Exit(_ => tracked.Add("exit: p1.nested"))
                    .Initial("inner")
                    .State("inner", c => c
                        .Entry(_ => tracked.Add("enter: p1.nested.inner"))
                        .Exit(_ => tracked.Add("exit: p1.nested.inner")))))
            .Build();

        var actor = Interpret(machine);
        tracked.Clear();
        actor.Send(Ev("ENTER_PARALLEL"));

        Assert.Equal(
            ["exit: start", "enter: p1", "enter: p1.nested", "enter: p1.nested.inner"],
            tracked);
    }

    [Fact] // 'should reenter parallel region when a parallel state gets reentered while targeting another region'
    public void Reentering_a_parallel_state_reenters_every_region()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("reenter-regions")
            .Initial("ready")
            .Parallel("ready", p => p
                .Entry(_ => tracked.Add("enter: ready"))
                .Exit(_ => tracked.Add("exit: ready"))
                .On("FOO", t => t.Target("#cameraOff").Reenter())
                .State("devicesInfo", r => r
                    .Entry(_ => tracked.Add("enter: ready.devicesInfo"))
                    .Exit(_ => tracked.Add("exit: ready.devicesInfo")))
                .State("camera", r => r
                    .Entry(_ => tracked.Add("enter: ready.camera"))
                    .Exit(_ => tracked.Add("exit: ready.camera"))
                    .Initial("on")
                    .State("on", c => c
                        .Entry(_ => tracked.Add("enter: ready.camera.on"))
                        .Exit(_ => tracked.Add("exit: ready.camera.on")))
                    .State("off", c => c
                        .Id("cameraOff")
                        .Entry(_ => tracked.Add("enter: ready.camera.off"))
                        .Exit(_ => tracked.Add("exit: ready.camera.off")))))
            .Build();

        var actor = Interpret(machine);
        tracked.Clear();
        actor.Send(Ev("FOO"));

        Assert.Equal(
            [
                "exit: ready.camera.on", "exit: ready.camera", "exit: ready.devicesInfo",
                "exit: ready", "enter: ready", "enter: ready.devicesInfo",
                "enter: ready.camera", "enter: ready.camera.off"
            ],
            tracked);
    }

    [Fact] // 'should call transition actions in document order for same-level parallel regions'
    public void Transition_actions_run_in_document_order_across_parallel_regions()
    {
        var actual = new List<string>();

        var machine = Machine.Create("doc-order")
            .Root(r => r.AsParallel())
            .State("a", s => s.On("FOO", t => t.Do(_ => actual.Add("a"))))
            .State("b", s => s.On("FOO", t => t.Do(_ => actual.Add("b"))))
            .Build();

        Interpret(machine).Send(Ev("FOO"));

        Assert.Equal(["a", "b"], actual);
    }

    [Fact] // 'should call transition actions in document order for states at different levels of parallel regions'
    public void Transition_actions_run_in_document_order_across_levels()
    {
        var actual = new List<string>();

        var machine = Machine.Create("doc-order-levels")
            .Root(r => r.AsParallel())
            .State("a", s => s
                .Initial("a1")
                .State("a1", c => c.On("FOO", t => t.Do(_ => actual.Add("a1")))))
            .State("b", s => s.On("FOO", t => t.Do(_ => actual.Add("b"))))
            .Build();

        Interpret(machine).Send(Ev("FOO"));

        Assert.Equal(["a1", "b"], actual);
    }

    // ======================================================================
    // B. Parallel states (parallel.test.ts)
    // ======================================================================

    [Fact] // 'source parallel region should not be exited when a transition within it targets another parallel region (parallel root)'
    public void A_transition_into_another_region_does_not_exit_the_source_region()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("regions")
            .Root(r => r.AsParallel())
            .State("Operation", s => s
                .Entry(_ => tracked.Add("enter: Operation"))
                .Exit(_ => tracked.Add("exit: Operation"))
                .Initial("Waiting")
                .State("Waiting", c => c
                    .Entry(_ => tracked.Add("enter: Operation.Waiting"))
                    .Exit(_ => tracked.Add("exit: Operation.Waiting"))
                    .On("TOGGLE_MODE", t => t.Target("#Demo")))
                .State("Fetching"))
            .State("Mode", s => s
                .Initial("Normal")
                .State("Normal", c => c
                    .Entry(_ => tracked.Add("enter: Mode.Normal"))
                    .Exit(_ => tracked.Add("exit: Mode.Normal")))
                .State("Demo", c => c
                    .Id("Demo")
                    .Entry(_ => tracked.Add("enter: Mode.Demo"))
                    .Exit(_ => tracked.Add("exit: Mode.Demo"))))
            .Build();

        var actor = Interpret(machine);
        tracked.Clear();
        actor.Send(Ev("TOGGLE_MODE"));

        Assert.Equal(["exit: Mode.Normal", "enter: Mode.Demo"], tracked);
    }

    [Fact] // 'targetless transition on a parallel state should not enter nor exit any states'
    public void Targetless_transition_on_a_parallel_state_enters_and_exits_nothing()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("test")
            .Root(r => r
                .AsParallel()
                .On("MY_EVENT", t => t.Do(_ => tracked.Add("action"))))
            .State("first", s => s
                .Entry(_ => tracked.Add("enter: first"))
                .Exit(_ => tracked.Add("exit: first"))
                .Initial("disabled")
                .State("disabled", c => c
                    .Entry(_ => tracked.Add("enter: first.disabled"))
                    .Exit(_ => tracked.Add("exit: first.disabled")))
                .State("enabled"))
            .State("second", s => s
                .Entry(_ => tracked.Add("enter: second"))
                .Exit(_ => tracked.Add("exit: second")))
            .Build();

        var actor = Interpret(machine);
        tracked.Clear();
        actor.Send(Ev("MY_EVENT"));

        Assert.Equal(["action"], tracked);
    }

    [Fact] // 'regions should be able to transition to orthogonal regions'
    public void A_transition_can_target_multiple_regions_at_once()
    {
        var machine = Machine.Create("orthogonal")
            .Root(r => r.AsParallel())
            .State("Pages", s => s
                .Initial("About")
                .State("About", c => c.Id("About"))
                .State("Dashboard", c => c.Id("Dashboard")))
            .State("Menu", s => s
                .Initial("Closed")
                .State("Closed", c => c
                    .Id("Closed")
                    .On("toggle", t => t.Target("#Opened")))
                .State("Opened", c => c
                    .Id("Opened")
                    .On("toggle", t => t.Target("#Closed"))
                    .On("go to dashboard", t => t.Target("#Dashboard", "#Opened"))))
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("toggle"));
        actor.Send(Ev("go to dashboard"));

        // Both states carry explicit ids, so they are matched by id rather than by path.
        var state = (State<Unit>)actor.RootSnapshot;
        Assert.True(state.Matches("#Opened"));
        Assert.True(state.Matches("#Dashboard"));
    }

    [Fact] // 'should raise a "xstate.done.state.*" event when all child states reach final state'
    public void Parallel_state_is_done_when_every_region_reaches_a_final_state()
    {
        var machine = Machine.Create("test")
            .Initial("p")
            .Parallel("p", p => p
                .On("xstate.done.state.test.p", t => t.Target("#test.success"))
                .State("a", r => r
                    .Initial("idle")
                    .State("idle", c => c.On("FINISH", t => t.Target("finished")))
                    .Final("finished"))
                .State("b", r => r
                    .Initial("idle")
                    .State("idle", c => c.On("FINISH", t => t.Target("finished")))
                    .Final("finished"))
                .State("c", r => r
                    .Initial("idle")
                    .State("idle", c => c.On("FINISH", t => t.Target("finished")))
                    .Final("finished")))
            .Final("success")
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("FINISH"));

        Assert.Equal(SnapshotStatus.Done, actor.RootSnapshot.Status);
    }

    [Fact] // 'should raise a "xstate.done.state.*" event when a pseudostate of a history type is directly on a parallel state'
    public void A_history_pseudostate_does_not_count_as_a_parallel_region_for_done_state()
    {
        var machine = Machine.Create("hist-done")
            .Initial("parallelSteps")
            .Parallel("parallelSteps", p => p
                .History("hist")
                .On("xstate.done.state.hist-done.parallelSteps", t => t.Target("#hist-done.finished"))
                .State("one", r => r
                    .Initial("wait_one")
                    .State("wait_one", c => c.On("finish_one", t => t.Target("done")))
                    .Final("done"))
                .State("two", r => r
                    .Initial("wait_two")
                    .State("wait_two", c => c.On("finish_two", t => t.Target("done")))
                    .Final("done")))
            .State("finished")
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("finish_one"));
        actor.Send(Ev("finish_two"));

        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("finished"));
    }

    [Fact] // 'should aggregate region outputs into a keyed object'
    public void Done_state_aggregates_region_outputs_into_a_keyed_object()
    {
        object? output = null;

        var machine = Machine.Create("aggregate")
            .Initial("processing")
            .Parallel("processing", p => p
                .On("xstate.done.state.aggregate.processing", t => t
                    .Do(a => output = ((DoneStateEvent)a.Event).Output)
                    .Target("#aggregate.success"))
                .State("upload", r => r
                    .Initial("pending")
                    .State("pending", c => c.On("UPLOADED", t => t.Target("done")))
                    .Final("done", c => c.Output(_ => "/file.png")))
                .State("validate", r => r
                    .Initial("checking")
                    .State("checking", c => c.On("VALID", t => t.Target("done")))
                    .Final("done", c => c.Output(_ => true))))
            .Final("success")
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("UPLOADED"));
        actor.Send(Ev("VALID"));

        var aggregated = Assert.IsType<Dictionary<string, object?>>(output);
        Assert.Equal("/file.png", aggregated["upload"]);
        Assert.Equal(true, aggregated["validate"]);
        Assert.Equal(SnapshotStatus.Done, actor.RootSnapshot.Status);
    }

    [Fact] // 'should include undefined for regions without output'
    public void Done_state_aggregation_includes_regions_without_output()
    {
        object? output = null;

        var machine = Machine.Create("partial")
            .Initial("p")
            .Parallel("p", p => p
                .On("xstate.done.state.partial.p", t => t
                    .Do(a => output = ((DoneStateEvent)a.Event).Output)
                    .Target("#partial.success"))
                .State("withOutput", r => r
                    .Initial("a")
                    .State("a", c => c.On("GO", t => t.Target("done")))
                    .Final("done", c => c.Output(_ => 42)))
                .State("withoutOutput", r => r
                    .Initial("a")
                    .State("a", c => c.On("GO", t => t.Target("done")))
                    .Final("done")))
            .Final("success")
            .Build();

        Interpret(machine).Send(Ev("GO"));

        var aggregated = Assert.IsType<Dictionary<string, object?>>(output);
        Assert.Equal(42, aggregated["withOutput"]);
        Assert.True(aggregated.ContainsKey("withoutOutput"));
        Assert.Null(aggregated["withoutOutput"]);
    }

    [Fact] // 'should aggregate nested parallel outputs'
    public void Nested_parallel_outputs_are_aggregated_recursively()
    {
        object? output = null;

        var machine = Machine.Create("nested-agg")
            .Initial("outer")
            .Parallel("outer", p => p
                .On("xstate.done.state.nested-agg.outer", t => t
                    .Do(a => output = ((DoneStateEvent)a.Event).Output)
                    .Target("#nested-agg.success"))
                .State("branch1", r => r
                    .Initial("active")
                    .State("active", c => c.On("DONE", t => t.Target("done")))
                    .Final("done", c => c.Output(_ => "branch1")))
                .Parallel("branch2", r => r
                    .State("inner1", i => i
                        .Initial("active")
                        .State("active", c => c.On("DONE", t => t.Target("done")))
                        .Final("done", c => c.Output(_ => "inner1")))
                    .State("inner2", i => i
                        .Initial("active")
                        .State("active", c => c.On("DONE", t => t.Target("done")))
                        .Final("done", c => c.Output(_ => "inner2")))))
            .Final("success")
            .Build();

        Interpret(machine).Send(Ev("DONE"));

        var aggregated = Assert.IsType<Dictionary<string, object?>>(output);
        Assert.Equal("branch1", aggregated["branch1"]);
        var inner = Assert.IsType<Dictionary<string, object?>>(aggregated["branch2"]);
        Assert.Equal("inner1", inner["inner1"]);
        Assert.Equal("inner2", inner["inner2"]);
    }

    [Fact] // 'should provide aggregated output for root parallel machine'
    public void A_root_parallel_machine_resolves_its_output_from_the_aggregated_regions()
    {
        var machine = Machine.Create("root-parallel")
            .Root(r => r
                .AsParallel()
                .Output(a => ((DoneStateEvent)a.Event).Output))
            .State("a", s => s
                .Initial("active")
                .State("active", c => c.On("DONE", t => t.Target("final")))
                .Final("final", c => c.Output(_ => "from a")))
            .State("b", s => s
                .Initial("active")
                .State("active", c => c.On("DONE", t => t.Target("final")))
                .Final("final", c => c.Output(_ => "from b")))
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("DONE"));

        Assert.Equal(SnapshotStatus.Done, actor.RootSnapshot.Status);
        var aggregated = Assert.IsType<Dictionary<string, object?>>(actor.RootSnapshot.Output);
        Assert.Equal("from a", aggregated["a"]);
        Assert.Equal("from b", aggregated["b"]);
    }

    // ======================================================================
    // C. History (history.test.ts)
    // ======================================================================

    [Fact] // 'should go to the most recently visited state (explicit shallow history type)'
    public void Shallow_history_returns_to_the_most_recently_visited_child()
    {
        var machine = Machine.Create("shallow")
            .Initial("on")
            .State("on", s => s
                .Initial("first")
                .State("first", c => c.On("SWITCH", t => t.Target("second")))
                .State("second")
                .History("hist")
                .On("POWER", t => t.Target("#shallow.off")))
            .State("off", s => s.On("POWER", t => t.Target("on.hist")))
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("SWITCH"));
        actor.Send(Ev("POWER"));
        actor.Send(Ev("POWER"));

        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("on.second"));
    }

    [Fact] // 'should go to the shallow history'
    public void Shallow_history_restores_only_the_immediate_child_and_uses_initial_below_it()
    {
        var machine = Machine.Create("shallow-deep")
            .Initial("on")
            .State("off", s => s.On("POWER", t => t.Target("on.history")))
            .State("on", s => s
                .Initial("first")
                .State("first", c => c.On("SWITCH", t => t.Target("second")))
                .State("second", c => c
                    .Initial("A")
                    .State("A", g => g.On("INNER", t => t.Target("B")))
                    .State("B", g => g
                        .Initial("P")
                        .State("P")
                        .State("Q")))
                .History("history")
                .On("POWER", t => t.Target("#shallow-deep.off")))
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("SWITCH"));
        actor.Send(Ev("INNER"));
        actor.Send(Ev("POWER"));
        actor.Send(Ev("POWER"));

        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("on.second.A"));
    }

    [Fact] // 'should go to the deep history (explicit)'
    public void Deep_history_restores_the_whole_nested_configuration()
    {
        var machine = Machine.Create("deep-hist")
            .Initial("on")
            .State("off", s => s.On("POWER", t => t.Target("on.history")))
            .State("on", s => s
                .Initial("first")
                .State("first", c => c.On("SWITCH", t => t.Target("second")))
                .State("second", c => c
                    .Initial("A")
                    .State("A", g => g.On("INNER", t => t.Target("B")))
                    .State("B", g => g
                        .Initial("P")
                        .State("P", h => h.On("INNER", t => t.Target("Q")))
                        .State("Q")))
                .History("history", HistoryType.Deep)
                .On("POWER", t => t.Target("#deep-hist.off")))
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("SWITCH"));
        actor.Send(Ev("INNER"));
        actor.Send(Ev("POWER"));
        actor.Send(Ev("POWER"));
        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("on.second.B.P"));

        // 'should go to the deepest history'
        var deepest = Interpret(machine);
        deepest.Send(Ev("SWITCH"));
        deepest.Send(Ev("INNER"));
        deepest.Send(Ev("INNER"));
        deepest.Send(Ev("POWER"));
        deepest.Send(Ev("POWER"));
        Assert.True(((State<Unit>)deepest.RootSnapshot).Matches("on.second.B.Q"));
    }

    [Fact] // 'should go to the configured default target when a history state is the initial state of the machine'
    public void A_history_state_as_the_initial_state_resolves_to_its_default_target()
    {
        // The history node is the machine's initial state and targets `bar` by default.
        var machine = Machine.Create("hist-initial")
            .Initial("foo")
            .State("bar")
            .Root(r => r.History("foo", HistoryType.Shallow, defaultTarget: "bar"))
            .Build();

        Assert.True(((State<Unit>)Interpret(machine).RootSnapshot).Matches("bar"));
    }

    [Fact] // 'should ignore parallel state history'
    public void History_directly_on_a_parallel_state_restores_regions_but_not_their_descendants()
    {
        var machine = Machine.Create("par-hist")
            .Initial("off")
            .State("off", s => s
                .On("SWITCH", t => t.Target("on"))
                .On("POWER", t => t.Target("on.hist")))
            .Parallel("on", p => p
                .History("hist")
                .On("POWER", t => t.Target("#par-hist.off"))
                .State("A", r => r
                    .Initial("B")
                    .State("B", c => c.On("INNER_A", t => t.Target("C")))
                    .State("C", c => c
                        .Initial("D")
                        .State("D")
                        .State("E"))
                    .History("hist"))
                .State("K", r => r
                    .Initial("L")
                    .State("L")
                    .State("M")
                    .History("hist")))
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("SWITCH"));
        actor.Send(Ev("INNER_A"));
        actor.Send(Ev("POWER"));
        actor.Send(Ev("POWER"));

        var state = (State<Unit>)actor.RootSnapshot;
        Assert.True(state.Matches("on.A.B"));
        Assert.True(state.Matches("on.K.L"));
    }

    [Fact] // 'should re-enter each regions of parallel state correctly'
    public void Deep_history_on_each_parallel_region_restores_both_regions()
    {
        var machine = Machine.Create("par-deep")
            .Initial("off")
            .State("off", s => s
                .On("SWITCH", t => t.Target("on"))
                .On("DEEP_POWER", t => t.Target("on.A.deepHistory", "on.K.deepHistory")))
            .Parallel("on", p => p
                .On("POWER", t => t.Target("#par-deep.off"))
                .State("A", r => r
                    .Initial("B")
                    .State("B", c => c.On("INNER_A", t => t.Target("C")))
                    .State("C", c => c
                        .Initial("D")
                        .State("D", g => g.On("INNER_A", t => t.Target("E")))
                        .State("E"))
                    .History("deepHistory", HistoryType.Deep))
                .State("K", r => r
                    .Initial("L")
                    .State("L", c => c.On("INNER_K", t => t.Target("M")))
                    .State("M", c => c
                        .Initial("N")
                        .State("N", g => g.On("INNER_K", t => t.Target("O")))
                        .State("O"))
                    .History("deepHistory", HistoryType.Deep)))
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("SWITCH"));
        actor.Send(Ev("INNER_A"));
        actor.Send(Ev("INNER_A"));
        actor.Send(Ev("INNER_K"));
        actor.Send(Ev("INNER_K"));
        actor.Send(Ev("POWER"));
        actor.Send(Ev("DEEP_POWER"));

        var state = (State<Unit>)actor.RootSnapshot;
        Assert.True(state.Matches("on.A.C.E"));
        Assert.True(state.Matches("on.K.M.O"));
    }

    [Fact] // 'should re-enter multiple history states'
    public void Targeting_two_shallow_region_histories_restores_each_regions_shallow_child()
    {
        var machine = Machine.Create("multi-hist")
            .Initial("off")
            .State("off", s => s
                .On("SWITCH", t => t.Target("on"))
                .On("PARALLEL_HISTORY", t => t.Target("on.A.hist", "on.K.hist")))
            .Parallel("on", p => p
                .On("POWER", t => t.Target("#multi-hist.off"))
                .State("A", r => r
                    .Initial("B")
                    .State("B", c => c.On("INNER_A", t => t.Target("C")))
                    .State("C", c => c
                        .Initial("D")
                        .State("D", g => g.On("INNER_A", t => t.Target("E")))
                        .State("E"))
                    .History("hist"))
                .State("K", r => r
                    .Initial("L")
                    .State("L", c => c.On("INNER_K", t => t.Target("M")))
                    .State("M", c => c
                        .Initial("N")
                        .State("N", g => g.On("INNER_K", t => t.Target("O")))
                        .State("O"))
                    .History("hist")))
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("SWITCH"));
        actor.Send(Ev("INNER_A"));
        actor.Send(Ev("INNER_A"));
        actor.Send(Ev("INNER_K"));
        actor.Send(Ev("INNER_K"));
        actor.Send(Ev("POWER"));
        actor.Send(Ev("PARALLEL_HISTORY"));

        var state = (State<Unit>)actor.RootSnapshot;
        Assert.True(state.Matches("on.A.C.D"));
        Assert.True(state.Matches("on.K.M.N"));
    }

    [Fact] // 'should enter the parallel default configuration when a deep history state ... was never visited yet'
    public void Deep_history_into_a_never_visited_parallel_state_uses_the_default_configuration()
    {
        var machine = Machine.Create("unvisited")
            .Initial("off")
            .State("off", s => s.On("GO", t => t.Target("on.hist")))
            .Parallel("on", p => p
                .History("hist", HistoryType.Deep)
                .State("regA", r => r.Initial("a1").State("a1").State("a2"))
                .State("regB", r => r.Initial("b1").State("b1").State("b2")))
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("GO"));

        var state = (State<Unit>)actor.RootSnapshot;
        Assert.True(state.Matches("on.regA.a1"));
        Assert.True(state.Matches("on.regB.b1"));
    }

    // ======================================================================
    // D. Final states and output (final.test.ts)
    // ======================================================================

    private sealed record FinishWith(int Value) : MachineEvent;

    [Fact] // 'should use top-level final state output as machine output without root output'
    public void Top_level_final_state_output_becomes_the_machine_output()
    {
        var machine = Machine.Create("final-output")
            .Initial("start")
            .State("start", s => s.On<FinishWith>(t => t.Target("end")))
            .Final("end", s => s.Output(a => ((FinishWith)a.Event).Value * 2))
            .Build();

        var actor = Interpret(machine);
        actor.Send(new FinishWith(21));

        Assert.Equal(SnapshotStatus.Done, actor.RootSnapshot.Status);
        Assert.Equal(42, actor.RootSnapshot.Output);
    }

    [Fact] // 'should pass top-level final state output to root output mapper'
    public void Root_output_mapper_receives_the_final_state_output()
    {
        var machine = Machine.Create("root-output")
            .Root(r => r.Output(a => $"root: {((DoneStateEvent)a.Event).Output}"))
            .Initial("start")
            .State("start", s => s.On("FINISH", t => t.Target("end")))
            .Final("end", s => s.Output(_ => "final output"))
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("FINISH"));

        Assert.Equal("root: final output", actor.RootSnapshot.Output);
    }

    [Fact] // 'should emit the done state event when all nested states are final'
    public void A_done_state_event_is_emitted_when_all_nested_regions_are_final()
    {
        string? doneEventType = null;

        var machine = Machine.Create("m")
            .Initial("foo")
            .Parallel("foo", p => p
                .On("xstate.done.state.m.foo", t => t
                    .Do(a => doneEventType = a.Event.Type)
                    .Target("#m.bar"))
                .State("first", r => r
                    .Initial("a")
                    .State("a", c => c.On("NEXT_1", t => t.Target("b")))
                    .Final("b"))
                .State("second", r => r
                    .Initial("a")
                    .State("a", c => c.On("NEXT_2", t => t.Target("b")))
                    .Final("b")))
            .State("bar")
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("NEXT_1"));
        actor.Send(Ev("NEXT_2"));

        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("bar"));
        Assert.Equal("xstate.done.state", doneEventType);
    }

    [Fact] // 'state output should be able to use context updated by the entry action of the reached final state'
    public void Final_state_output_sees_context_updated_by_its_own_entry_action()
    {
        object? seen = null;

        var machine = Machine.Create<Counter>("ctx-output")
            .Context(_ => new Counter(0))
            .Initial("a")
            .State("a", s => s
                .On("xstate.done.state.ctx-output.a", t => t
                    .Do(x => seen = ((DoneStateEvent)x.Event).Output))
                .Initial("a1")
                .State("a1", c => c.On("NEXT", t => t.Target("a2")))
                .Final("a2", c => c
                    .EntryAssign(x => x.Context with { Count = 1 })
                    .Output(x => x.Context.Count)))
            .Build();

        Interpret(machine).Send(Ev("NEXT"));

        Assert.Equal(1, seen);
    }

    [Fact] // 'should execute final child state actions first'
    public void Final_child_state_actions_run_before_the_parents_done_actions()
    {
        var actual = new List<string>();

        var machine = Machine.Create("final-order")
            .Initial("foo")
            .State("foo", s => s
                .On("xstate.done.state.final-order.foo", t => t.Do(_ => actual.Add("fooAction")))
                .Initial("bar")
                .State("bar", r => r
                    .On("xstate.done.state.final-order.foo.bar", t => t.Target("barFinal"))
                    .Initial("baz")
                    .Final("baz", c => c.Entry(_ => actual.Add("bazAction"))))
                .Final("barFinal", r => r.Entry(_ => actual.Add("barAction"))))
            .Build();

        Interpret(machine);

        Assert.Equal(["bazAction", "barAction", "fooAction"], actual);
    }

    [Fact] // 'should call exit actions in reversed document order when the machines reaches its final state'
    public void Exit_actions_run_in_reverse_document_order_when_the_machine_finishes()
    {
        var tracked = new List<string>();

        var machine = Machine.Create("finish-exit")
            .Root(r => r
                .Entry(_ => tracked.Add("enter: __root__"))
                .Exit(_ => tracked.Add("exit: __root__")))
            .Initial("a")
            .State("a", s => s
                .Entry(_ => tracked.Add("enter: a"))
                .Exit(_ => tracked.Add("exit: a"))
                .On("EV", t => t.Target("b")))
            .Final("b", s => s
                .Entry(_ => tracked.Add("enter: b"))
                .Exit(_ => tracked.Add("exit: b")))
            .Build();

        var actor = Interpret(machine);
        tracked.Clear();
        actor.Send(Ev("EV"));

        Assert.Equal(["exit: a", "enter: b", "exit: b", "exit: __root__"], tracked);
    }

    // ======================================================================
    // E. Guards and In() (guards.test.ts, stateIn.test.ts)
    // ======================================================================

    [Fact] // guards.test.ts — 'should be able to guard a transition' / candidate ordering
    public void The_first_transition_whose_guard_passes_is_selected()
    {
        var machine = Machine.Create<Counter>("guard-order")
            .Context(_ => new Counter(0))
            .Initial("a")
            .State("a", s => s
                .On("EV", t => t.Guard(_ => false).Target("never"))
                .On("EV", t => t.Guard(x => x.Context.Count == 0).Target("b"))
                .On("EV", t => t.Target("c")))
            .State("b")
            .State("c")
            .State("never")
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("EV"));

        Assert.True(((State<Counter>)actor.RootSnapshot).Matches("b"));
    }

    [Fact] // stateIn.test.ts — In() referring to another parallel region
    public void An_In_guard_can_test_a_state_in_another_parallel_region()
    {
        var machine = Machine.Create("in-guard")
            .Root(r => r.AsParallel())
            .State("A", s => s
                .Initial("a1")
                .State("a1", c => c.On("SWITCH_A", t => t.Target("a2")))
                .State("a2"))
            .State("B", s => s
                .Initial("b1")
                .State("b1", c => c.On("TRY", t => t
                    .Guard(Guards.In<Unit>("in-guard.A.a2"))
                    .Target("b2")))
                .State("b2"))
            .Build();

        var actor = Interpret(machine);

        // A is still in a1, so the guarded transition is blocked.
        actor.Send(Ev("TRY"));
        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("B.b1"));

        actor.Send(Ev("SWITCH_A"));
        actor.Send(Ev("TRY"));
        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("B.b2"));
    }

    [Fact] // guards.test.ts — and/or/not combinators
    public void Guard_combinators_compose()
    {
        var isZero = (Guard<Counter>)(a => a.Context.Count == 0);
        var never = (Guard<Counter>)(_ => false);

        var machine = Machine.Create<Counter>("combinators")
            .Context(_ => new Counter(0))
            .Initial("a")
            .State("a", s => s
                .On("EV", t => t.Guard(Guards.And(isZero, never)).Target("and"))
                .On("EV", t => t.Guard(Guards.Not(never)).Target("not"))
                .On("EV", t => t.Guard(Guards.Or(never, isZero)).Target("or")))
            .State("and")
            .State("not")
            .State("or")
            .Build();

        var actor = Interpret(machine);
        actor.Send(Ev("EV"));

        Assert.True(((State<Counter>)actor.RootSnapshot).Matches("not"));
    }

    // ======================================================================
    // F. Transition selection: descriptors and priority (eventDescriptors.test.ts)
    // ======================================================================

    [Fact] // 'should select wildcard for non-matching event'
    public void A_wildcard_transition_is_used_only_when_nothing_more_specific_matches()
    {
        var machine = Machine.Create("wild")
            .Initial("a")
            .State("a", s => s
                .On("*", t => t.Target("fallback"))
                .On("FOO", t => t.Target("foo")))
            .State("foo")
            .State("fallback")
            .Build();

        Assert.True(((State<Unit>)Interpret(machine).Send(Ev("FOO")).RootSnapshot).Matches("foo"));
        Assert.True(((State<Unit>)Interpret(machine).Send(Ev("BAR")).RootSnapshot).Matches("fallback"));
    }

    [Fact] // 'should select a partial descriptor' — "error.*" prefix matching
    public void A_prefix_wildcard_matches_dotted_event_types()
    {
        var machine = Machine.Create("prefix")
            .Initial("a")
            .State("a", s => s
                .On("*", t => t.Target("any"))
                .On("error.*", t => t.Target("errors"))
                .On("error.communication", t => t.Target("exact")))
            .State("any")
            .State("errors")
            .State("exact")
            .Build();

        Assert.True(((State<Unit>)Interpret(machine).Send(Ev("error.platform")).RootSnapshot).Matches("errors"));
        Assert.True(((State<Unit>)Interpret(machine).Send(Ev("error.communication")).RootSnapshot).Matches("exact"));
        Assert.True(((State<Unit>)Interpret(machine).Send(Ev("other")).RootSnapshot).Matches("any"));
    }

    [Fact] // 'a descendant should be able to override a transition of an ancestor'
    public void A_descendant_transition_overrides_the_ancestors_transition()
    {
        var machine = Machine.Create("override")
            .Initial("parent")
            .State("parent", s => s
                .On("EV", t => t.Target("#override.fromParent"))
                .Initial("child")
                .State("child", c => c.On("EV", t => t.Target("#override.fromChild")))
                .State("other", c => c.On("NOPE", t => t.Target("#override.fromParent"))))
            .State("fromParent")
            .State("fromChild")
            .Build();

        Assert.True(((State<Unit>)Interpret(machine).Send(Ev("EV")).RootSnapshot).Matches("fromChild"));
    }

    // ======================================================================
    // G. Delayed transitions (after.test.ts) — driven by the virtual clock
    // ======================================================================

    private static StateMachine<Unit> LightMachine() =>
        Machine.Create("light")
            .Initial("green")
            .State("green", s => s.After(TimeSpan.FromMilliseconds(1000), t => t.Target("yellow")))
            .State("yellow", s => s.After(TimeSpan.FromMilliseconds(1000), t => t.Target("red")))
            .State("red", s => s.After(TimeSpan.FromMilliseconds(1000), t => t.Target("green")))
            .Build();

    [Fact] // 'should transition after delay'
    public void An_after_transition_fires_once_its_delay_elapses()
    {
        var actor = Interpret(LightMachine());
        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("green"));

        actor.AdvanceTime(TimeSpan.FromMilliseconds(500));
        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("green"));

        actor.AdvanceTime(TimeSpan.FromMilliseconds(510));
        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("yellow"));

        actor.AdvanceTime(TimeSpan.FromMilliseconds(1000));
        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("red"));
    }

    [Fact] // 'uses a canonical after event with delay and state identity'
    public void The_scheduled_after_event_carries_its_delay_and_state_id()
    {
        var actor = Interpret(LightMachine());

        var scheduled = Assert.Single(actor.PendingSends);
        var after = Assert.IsType<AfterEvent>(scheduled.Event);
        Assert.Equal("xstate.after", after.Type);
        Assert.Equal("1000", after.Delay);
        Assert.Equal("light.green", after.StateId);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), scheduled.Time);
    }

    [Fact] // timer cancellation on early exit (logical-timers.test.ts / transition.test.ts)
    public void Leaving_a_state_early_cancels_its_pending_after_timer()
    {
        var machine = Machine.Create("cancel-after")
            .Initial("a")
            .State("a", s => s
                .After(TimeSpan.FromMilliseconds(1000), t => t.Target("timedOut"))
                .On("CANCEL", t => t.Target("b")))
            .State("b")
            .State("timedOut")
            .Build();

        var actor = Interpret(machine);
        Assert.Single(actor.PendingSends);

        actor.Send(Ev("CANCEL"));
        Assert.Empty(actor.PendingSends);

        actor.AdvanceTime(TimeSpan.FromMilliseconds(5000));
        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("b"));
    }

    [Fact] // multiple `after` delays on one state: the earliest wins and the rest are cancelled
    public void The_earliest_of_several_after_delays_wins_and_cancels_the_others()
    {
        var machine = Machine.Create("multi-after")
            .Initial("a")
            .State("a", s => s
                .After(TimeSpan.FromMilliseconds(200), t => t.Target("slow"))
                .After(TimeSpan.FromMilliseconds(100), t => t.Target("fast")))
            .State("fast")
            .State("slow")
            .Build();

        var actor = Interpret(machine);
        Assert.Equal(2, actor.PendingSends.Count);

        actor.AdvanceTime(TimeSpan.FromMilliseconds(150));
        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("fast"));
        Assert.Empty(actor.PendingSends);
    }

    [Fact] // 'parent state should enter child state without re-entering self (relative target)'
    public void An_after_transition_with_a_relative_target_does_not_reenter_the_parent()
    {
        var actual = new List<string>();

        var machine = Machine.Create("relative-after")
            .Initial("one")
            .State("one", s => s
                .Entry(_ => actual.Add("entered one"))
                .After(TimeSpan.FromMilliseconds(10), t => t.Target(".three"))
                .Initial("two")
                .State("two", c => c.Entry(_ => actual.Add("entered two")))
                .State("three", c => c
                    .Entry(_ => actual.Add("entered three"))
                    .Always(t => t.Target("#end"))))
            .Final("end", s => s.Id("end"))
            .Build();

        var actor = Interpret(machine);
        actor.RunToCompletion();

        Assert.Equal(["entered one", "entered two", "entered three"], actual);
        Assert.Equal(SnapshotStatus.Done, actor.RootSnapshot.Status);
    }

    // ======================================================================
    // H. Invoked machines (invoke.test.ts) — driven by the interpreter
    // ======================================================================

    [Fact] // 'should start services (explicit machine, invoke = config)' — onDone carries the child output
    public void An_invoked_machines_output_is_delivered_to_onDone()
    {
        object? received = null;

        var child = Machine.Create<Counter>("fetch")
            .Context(input => new Counter((int)(input ?? 0)))
            .Initial("pending")
            .State("pending", s => s.On("RESOLVE", t => t.Target("success")))
            .Final("success", s => s.Output(a => a.Context.Count * 2))
            .Build();

        var parent = Machine.Create("fetcher")
            .Initial("idle")
            .State("idle", s => s.On("GO", t => t.Target("waiting")))
            .State("waiting", s => s
                .Invoke(
                    child,
                    id: "fetch",
                    input: _ => 21,
                    onDone: t => t
                        .Do(a => received = a.Event.Output)
                        .Target("received"))
                .On("PING", t => t.SendTo("fetch", _ => Ev("RESOLVE"))))
            .State("received")
            .Build();

        var actor = Interpret(parent);
        actor.Send(Ev("GO"));
        Assert.True(actor.IsRunning("fetch"));

        actor.Send(Ev("PING"));

        Assert.Equal(42, received);
        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("received"));
        Assert.False(actor.IsRunning("fetch"));
    }

    [Fact] // errors.test.ts — 'onError catches errors from invoked actor initialization'
    public void A_failing_invoked_machine_triggers_onError()
    {
        Exception? caught = null;

        var child = Machine.Create<Counter>("broken")
            .Context(_ => throw new InvalidOperationException("invoked actor initialization failed"))
            .Initial("a")
            .State("a")
            .Build();

        var parent = Machine.Create("parent")
            .Initial("active")
            .State("active", s => s.Invoke(
                child,
                id: "child",
                onError: t => t
                    .Do(a => caught = a.Event.Error)
                    .Target("failed")))
            .State("failed")
            .Build();

        var actor = Interpret(parent);

        Assert.True(((State<Unit>)actor.RootSnapshot).Matches("failed"));
        Assert.Equal(SnapshotStatus.Active, actor.RootSnapshot.Status);
        Assert.Equal("invoked actor initialization failed", caught?.Message);
    }

    [Fact] // 'should communicate with the child machine (invoke on state)'
    public void A_child_machine_can_send_events_back_to_its_parent()
    {
        var child = Machine.Create("child")
            .Initial("one")
            .State("one", s => s.On("NEXT", t => t.Target("two")))
            .State("two", s => s.Entry(new SendToAction<Unit>(_ => "#_parent", _ => Ev("NEXT"))))
            .Build();

        var parent = Machine.Create("parent")
            .Initial("one")
            .State("one", s => s
                .Invoke(child, id: "foo-child")
                .On("KICK", t => t.SendTo("foo-child", _ => Ev("NEXT")))
                .On("NEXT", t => t.Target("two")))
            .Final("two")
            .Build();

        var actor = Interpret(parent);
        actor.Send(Ev("KICK"));

        Assert.Equal(SnapshotStatus.Done, actor.RootSnapshot.Status);
    }

    [Fact] // 'child can immediately respond to the parent with multiple events'
    public void A_child_can_send_several_events_to_the_parent_in_one_step()
    {
        var child = Machine.Create("child")
            .Initial("init")
            .State("init", s => s.On("FORWARD_DEC", t => t
                .SendTo("#_parent", _ => Ev("DEC"))
                .SendTo("#_parent", _ => Ev("DEC"))
                .SendTo("#_parent", _ => Ev("DEC"))))
            .Build();

        var parent = Machine.Create<Counter>("parent")
            .Context(_ => new Counter(0))
            .Initial("start")
            .State("start", s => s
                .Invoke(child, id: "someService")
                .Always(t => t.Guard(a => a.Context.Count == -3).Target("stop"))
                .On("DEC", t => t.Assign(a => a.Context with { Count = a.Context.Count - 1 }))
                .On("FORWARD_DEC", t => t.SendTo("someService", _ => Ev("FORWARD_DEC"))))
            .Final("stop")
            .Build();

        var actor = Interpret(parent);
        actor.Send(Ev("FORWARD_DEC"));

        Assert.Equal(-3, ((State<Counter>)actor.RootSnapshot).Context.Count);
        Assert.Equal(SnapshotStatus.Done, actor.RootSnapshot.Status);
    }

    [Fact] // 'should stop the invoked actor when the invoking state is exited'
    public void An_invoked_child_is_stopped_when_its_state_is_exited()
    {
        var child = Machine.Create("child")
            .Initial("idle")
            .State("idle")
            .Build();

        var parent = Machine.Create("parent")
            .Initial("active")
            .State("active", s => s
                .Invoke(child, id: "child")
                .On("CANCEL", t => t.Target("idle")))
            .State("idle")
            .Build();

        var actor = Interpret(parent);
        Assert.True(actor.IsRunning("child"));

        actor.Send(Ev("CANCEL"));
        Assert.False(actor.IsRunning("child"));
    }

    [Fact] // 'deep invocations should be stopped when the machine reaches done state'
    public void Deep_invocations_are_stopped_when_the_machine_reaches_a_final_state()
    {
        var grandchild = Machine.Create("grandchild")
            .Initial("idle")
            .State("idle")
            .Build();

        var child = Machine.Create("child")
            .Root(r => r.Invoke(grandchild, id: "grandchild"))
            .Initial("idle")
            .State("idle")
            .Build();

        var parent = Machine.Create("parent")
            .Root(r => r.Invoke(child, id: "child"))
            .Initial("a")
            .State("a", s => s.On("FINISH", t => t.Target("b")))
            .Final("b")
            .Build();

        var actor = Interpret(parent);
        Assert.True(actor.IsRunning("child"));
        Assert.True(actor.IsRunning("grandchild"));

        actor.Send(Ev("FINISH"));

        Assert.Equal(SnapshotStatus.Done, actor.RootSnapshot.Status);
        Assert.False(actor.IsRunning("child"));
        Assert.False(actor.IsRunning("grandchild"));
    }

    [Fact] // 'should be able to restart an invoke when reentering the invoking state'
    public void Reentering_an_invoking_state_restarts_the_child()
    {
        var child = Machine.Create("child")
            .Initial("idle")
            .State("idle")
            .Build();

        var parent = Machine.Create("parent")
            .Initial("active")
            .State("active", s => s
                .Invoke(child, id: "child")
                .On("REENTER", t => t.Target("active").Reenter()))
            .Build();

        var actor = Interpret(parent);
        var firstSession = actor.Actors.Single(a => a.Id == "child").SessionId;

        actor.Send(Ev("REENTER"));

        var sessions = actor.Actors.Where(a => a.Id == "child").ToList();
        Assert.Equal(2, sessions.Count);
        Assert.False(sessions[0].IsAlive);
        Assert.True(sessions[1].IsAlive);
        Assert.NotEqual(firstSession, sessions[1].SessionId);
    }
}
