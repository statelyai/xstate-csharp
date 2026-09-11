using System.Text.Json;
using XState.Json;
using Xunit;

namespace XState.Tests;

/// <summary>
/// <see cref="MachineConfig.ToJson{TContext}"/> — xstate v6 <c>serializeMachine</c>: an imported
/// machine hands back its own config (lossless), and a machine built with the fluent builder is
/// walked back into the config shape, with every delegate marked as opaque code.
/// </summary>
public class JsonExportTests
{
    private static MachineEvent Ev(string type) => new NamedEvent(type);

    /// <summary>Formatting-independent comparison: both sides re-serialized the same way.</summary>
    private static string Canonical(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }

    private static string Canonical(JsonElement element) => JsonSerializer.Serialize(element);

    public static TheoryData<string> Configs =>
    [
        """
        { "id": "toggle", "initial": "inactive",
          "states": { "inactive": { "on": { "TOGGLE": "active" } },
                      "active": { "on": { "TOGGLE": { "target": "inactive" } } } } }
        """,
        """
        { "id": "nested", "initial": "a",
          "states": { "a": { "initial": "a1", "on": { "OUT": "b" },
                             "states": { "a1": { "on": { "NEXT": "a2" } },
                                         "a2": { "on": { "DEEP": "#nested.b" } } } },
                      "b": {} } }
        """,
        """
        { "id": "timeout", "initial": "waiting",
          "states": { "waiting": { "after": { "500": "late" } }, "late": {} } }
        """,
        """
        { "id": "hist", "initial": "on",
          "states": { "on": { "initial": "first", "on": { "POWER": "#hist.off" },
                              "states": { "first": { "on": { "SWITCH": "second" } }, "second": {},
                                          "recall": { "type": "history", "history": "shallow" } } },
                      "off": { "on": { "POWER": "on.recall" } } } }
        """,
        """
        { "id": "annotated", "description": "annotated", "meta": { "author": "someone" },
          "version": "1.4.0", "internalEvents": ["TICK"], "initial": "a",
          "states": { "a": { "meta": { "note": "hi" }, "tags": ["first"],
                             "on": { "GO": { "target": "b", "description": "d", "meta": { "k": 1 } } } },
                      "b": { "type": "final", "output": { "ok": true } } } }
        """,
        """
        { "id": "counter", "context": { "count": 0 }, "initial": "idle",
          "actions": { "incTwice": [ { "type": "@xstate.assign", "context": { "count": 1 } },
                                     { "type": "@xstate.assign", "context": { "count": 2 } } ] },
          "entry": [ { "type": "incTwice" } ],
          "states": { "idle": { "on": { "GO": { "target": "done", "context": { "count": 9 },
                                                "input": 42 } } },
                      "done": {} } }
        """,
        """
        { "id": "sla", "initial": { "target": "waiting", "input": { "seed": 1 } },
          "delays": { "slow": { "duration": "1.5s" } },
          "states": { "waiting": { "timeout": "1s", "onTimeout": { "target": "escalated" },
                                   "after": { "slow": "escalated" } },
                      "escalated": {} } }
        """
    ];

    // --- 1. An imported machine exports its own config, verbatim ---

    [Theory]
    [MemberData(nameof(Configs))]
    public void A_config_imported_from_json_round_trips_byte_equal(string config)
    {
        var machine = MachineConfig.FromJson(config);

        Assert.Equal(Canonical(config), Canonical(MachineConfig.ToJson(machine)));

        // And the round trip is stable: re-importing the export exports the same bytes again.
        var revived = MachineConfig.FromJson(MachineConfig.ToJsonString(machine));
        Assert.Equal(Canonical(config), Canonical(MachineConfig.ToJson(revived)));
    }

    [Fact]
    public void An_imported_machine_keeps_its_source_config()
    {
        var machine = MachineConfig.FromJson("""{ "id": "m", "initial": "a", "states": { "a": {} } }""");

        Assert.NotNull(machine.SourceJson);
        Assert.Equal("m", machine.SourceJson!.Value.GetProperty("id").GetString());
    }

    // --- 2. A builder machine is walked back into the config shape ---

    [Fact]
    public void A_builder_machine_exports_a_config_that_re_imports_with_the_same_behavior()
    {
        var entries = new List<string>();

        var machine = Machine.Create<JsonElement>("counter")
            .Context(_ => JsonConfigFixtures.Empty)
            .Version("2.0.0")
            .InternalEvents("TICK")
            .Action("bump", a => entries.Add("bump"))
            .Guard("always", _ => true)
            .Initial("idle")
            .State("idle", s => s
                .Entry("bump")
                .On("GO", t => t.Target("active").Guard("always").Do("bump", "p")))
            .State("active", s => s
                .Tag("busy")
                .Description("running")
                .On("STOP", t => t.Target("idle").Reenter()))
            .Build();

        var json = MachineConfig.ToJsonString(machine, indented: true);

        var revived = MachineConfig.FromJson(
            json,
            new MachineImplementations<JsonElement>()
                .Action("bump", a => entries.Add("bump"))
                .Guard("always", _ => true));

        Assert.Equal("2.0.0", revived.Version);
        Assert.Equal(["TICK"], revived.InternalEvents);
        Assert.Equal("running", revived.GetNodeById("counter.active").Description);
        Assert.Contains("busy", revived.GetNodeById("counter.active").Tags);

        // Same structure, same run.
        foreach (var built in new[] { machine, revived })
        {
            var state = built.GetInitialState().State;
            Assert.True(state.Matches("idle"));

            state = built.Transition(state, Ev("GO")).State;
            Assert.True(state.Matches("active"));

            state = built.Transition(state, Ev("STOP")).State;
            Assert.True(state.Matches("idle"));
        }

        // The named action's params ride along.
        var transition = Assert.Single(
            revived.GetNodeById("counter.idle").Transitions, t => t.TargetIds is ["#counter.active"]);
        Assert.Equal("bump", Assert.Single(transition.Actions).Name);
    }

    [Fact]
    public void Delayed_transitions_timeouts_and_invokes_survive_the_builder_export()
    {
        var machine = Machine.Create<JsonElement>("work")
            .Context(_ => JsonConfigFixtures.Empty)
            .Actor("waiter", new WaiterLogic())
            .Initial("working")
            .State("working", s => s
                .After(TimeSpan.FromMilliseconds(500), t => t.Target("late"))
                .Timeout(TimeSpan.FromSeconds(1))
                .OnTimeout(t => t.Target("timedOut"))
                .Invoke(
                    "waiter",
                    id: "child",
                    onDone: t => t.Target("finished"),
                    onError: t => t.Target("failed")))
            .State("late")
            .State("timedOut")
            .State("finished")
            .State("failed")
            .Build();

        var json = MachineConfig.ToJson(machine);
        var working = json.GetProperty("states").GetProperty("working");

        // A transition that is nothing but a target is written in the shorthand (string) form.
        Assert.Equal("#work.late", working.GetProperty("after").GetProperty("500").GetString());
        Assert.Equal(1000, working.GetProperty("timeout").GetDouble());
        Assert.Equal("#work.timedOut", working.GetProperty("onTimeout").GetString());

        var invoke = working.GetProperty("invoke");
        Assert.Equal("waiter", invoke.GetProperty("src").GetString());
        Assert.Equal("child", invoke.GetProperty("id").GetString());
        Assert.Equal("#work.finished", invoke.GetProperty("onDone").GetString());
        Assert.Equal("#work.failed", invoke.GetProperty("onError").GetString());

        // The invoke's completion transitions stay on the invoke — they are never duplicated into
        // `on` (xstate v6 json.test.ts "should not double-serialize invoke transitions").
        Assert.False(working.TryGetProperty("on", out _));

        var revived = MachineConfig.FromJson(
            MachineConfig.ToJsonString(machine),
            new MachineImplementations<JsonElement>().ActorLogic("waiter", new WaiterLogic()));

        Assert.Equal(TimeSpan.FromMilliseconds(500), revived.GetNodeById("work.working").After[0].Delay.Resolve(default));
    }

    [Fact]
    public void A_state_level_onDone_is_exported_under_its_own_key()
    {
        var machine = Machine.Create<JsonElement>("j")
            .Context(_ => JsonConfigFixtures.Empty)
            .Initial("a")
            .State("a", s => s
                .Initial("x")
                .State("x", x => x.On("NEXT", t => t.Target("y")))
                .Final("y")
                .OnDone(t => t.Target("b")))
            .State("b")
            .Build();

        var json = MachineConfig.ToJson(machine);

        Assert.Equal("#j.b", json.GetProperty("states").GetProperty("a").GetProperty("onDone").GetString());

        var revived = MachineConfig.FromJson(MachineConfig.ToJsonString(machine));
        Assert.True(revived.Transition(revived.GetInitialState().State, Ev("NEXT")).State.Matches("b"));
    }

    // --- 3. Delegates have no portable form ---

    [Fact]
    public void Inline_delegates_are_exported_as_opaque_code_markers_and_refuse_to_re_import()
    {
        var machine = Machine.Create<JsonElement>("inline")
            .Context(_ => JsonConfigFixtures.Empty)
            .Initial("idle")
            .State("idle", s => s
                .Entry(_ => { })
                .On("GO", t => t.Target("done").Guard(_ => true)))
            .Final("done", s => s.Output(_ => 42))
            .Build();

        var json = MachineConfig.ToJson(machine);
        var idle = json.GetProperty("states").GetProperty("idle");

        Assert.Equal("<opaque>", idle.GetProperty("entry").GetProperty("@code").GetString());
        Assert.Equal(
            "<opaque>",
            idle.GetProperty("on").GetProperty("GO").GetProperty("guard").GetProperty("@code").GetString());
        Assert.Equal(
            "<opaque>",
            json.GetProperty("states").GetProperty("done").GetProperty("output").GetProperty("@code").GetString());

        // A marker is not silently dropped on the way back in.
        var error = Assert.Throws<NotSupportedException>(
            () => MachineConfig.FromJson(MachineConfig.ToJsonString(machine)));
        Assert.Contains("@expr/@code", error.Message);
    }
}

internal static class JsonConfigFixtures
{
    internal static JsonElement Empty { get; } = JsonDocument.Parse("{}").RootElement.Clone();
}
