using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using XState.Persistence;

namespace XState.Tests;

/// <summary>
/// The persisted snapshot's wire shape — xstate v6 <c>persisted-snapshot/v1</c>
/// (<c>persistedSnapshot.schema.json</c>), plus the port's documented extensions.
/// </summary>
public class PersistedSnapshotJsonTests
{
    private static PersistedSnapshot Golden() => new()
    {
        Status = PersistedStatus.Active,
        Value = new Dictionary<string, object?> { ["shop"] = "cart" },
        Context = new Dictionary<string, object?> { ["count"] = 1L },
        HistoryValue = new Dictionary<string, IReadOnlyList<string>> { ["job.shop.hist"] = ["job.shop.cart"] },
        StateInputs = new Dictionary<string, object?> { ["job.shop"] = new Dictionary<string, object?> { ["n"] = 2L } },
        Children =
        [
            new PersistedChild
            {
                Id = "w",
                Address = "job/w",
                Src = "worker",
                SyncSnapshot = true,
                Incarnation = "0",
                Snapshot = new PersistedSnapshot { Status = PersistedStatus.Active, Value = "busy", Context = null }
            },
            new PersistedChild { Id = "r", Address = "job/r", Src = "remote", Incarnation = "1", Remote = true }
        ],
        Timers =
        [
            new PersistedTimer
            {
                Seq = 0,
                DelayMs = 500,
                Type = "@xstate.sendTo",
                Event = new NamedEvent("ping"),
                Target = EffectTarget.Parent,
                ScheduledAt = 1234
            },
            new PersistedTimer
            {
                DelayMs = 5000,
                Type = "@xstate.raise",
                Event = new AfterEvent("5000", "job.shop"),
                Target = null
            }
        ],
        Machine = new PersistedMachineIdentity("job", "1.0.0"),
        NextActorIds = new Dictionary<string, int> { ["x"] = 1 },
        NextTimerId = 1,
        NextIncarnation = 2
    };

    private const string GoldenJson =
        "{\"status\":\"active\",\"value\":{\"shop\":\"cart\"},\"context\":{\"count\":1}," +
        "\"historyValue\":{\"job.shop.hist\":[{\"id\":\"job.shop.cart\"}]},\"stateInputs\":{\"job.shop\":{\"n\":2}}," +
        "\"children\":{\"w\":{\"address\":\"job/w\",\"src\":\"worker\",\"syncSnapshot\":true,\"snapshot\":{\"status\":\"active\",\"value\":\"busy\",\"context\":null,\"historyValue\":{},\"children\":{},\"timers\":[],\"_nextActorIds\":{},\"_nextTimerId\":0,\"_nextIncarnation\":0},\"_incarnation\":\"0\"}," +
        "\"r\":{\"address\":\"job/r\",\"src\":\"remote\",\"remote\":true,\"incarnation\":\"1\"}}," +
        "\"timers\":[{\"seq\":0,\"delay\":500,\"type\":\"@xstate.sendTo\",\"event\":{\"type\":\"ping\",\"$event\":\"NamedEvent\",\"Name\":\"ping\",\"Data\":null},\"target\":{\"type\":\"parent\"},\"scheduledAt\":1234}," +
        "{\"delay\":5000,\"type\":\"@xstate.raise\",\"event\":{\"type\":\"xstate.after\",\"$event\":\"AfterEvent\",\"Delay\":\"5000\",\"StateId\":\"job.shop\"},\"target\":\"self\"}]," +
        "\"machine\":{\"id\":\"job\",\"version\":\"1.0.0\"},\"_nextActorIds\":{\"x\":1},\"_nextTimerId\":1,\"_nextIncarnation\":2}";

    [Fact]
    public void A_snapshot_serializes_to_the_v6_envelope_and_round_trips()
    {
        var json = SnapshotJson.Serialize(Golden());
        Assert.Equal(GoldenJson, json);

        var read = SnapshotJson.Deserialize(json);
        Assert.Equal(["w", "r"], read.Children.Select(c => c.Id));
        Assert.Null(read.Timers[1].Target);
        Assert.Equal(EffectTarget.Parent, read.Timers[0].Target);
        Assert.Equal(["job.shop.cart"], read.HistoryValue["job.shop.hist"]);
        Assert.Equal(GoldenJson, SnapshotJson.Serialize(read));
    }

    [Fact]
    public void A_machine_persists_children_and_timers_in_the_v6_shape()
    {
        var worker = Machine.Create("worker").Initial("busy").State("busy").Build();
        var machine = Machine.Create("job")
            .Actor("worker", worker)
            .Initial("running")
            .State("running", s => s
                .Invoke("worker", id: "w")
                .After(TimeSpan.FromSeconds(5), t => t.Target("done")))
            .State("done")
            .Build();

        var state = machine.GetInitialState().State;
        var node = JsonNode.Parse(SnapshotJson.Serialize(machine.Persist(
            state, new PersistOptions { ChildSnapshot = _ => worker.Persist(worker.GetInitialState().State) })))!;

        // Unversioned: no `machine`, and never a top-level `version`.
        Assert.Null(node["machine"]);
        Assert.Null(node["version"]);
        Assert.Equal("job/w", (string?)node["children"]!["w"]!["address"]);
        Assert.Equal("worker", (string?)node["children"]!["w"]!["src"]);
        Assert.Null(node["children"]!["w"]!["remote"]);
        Assert.Equal("self", (string?)node["timers"]![0]!["target"]);
        Assert.Equal(5000, (double)node["timers"]![0]!["delay"]!);

        var restored = machine.Restore(
            SnapshotJson.Deserialize(node.ToJsonString()),
            new RestoreOptions<Unit> { ContextConverter = _ => Unit.Value });
        Assert.Equal(state.Timers, restored.Timers);
        Assert.Equal(state.Children.ToList(), restored.State.Children.ToList());
    }

    [Fact]
    public void A_machine_without_both_identity_keys_is_an_invalid_snapshot()
    {
        var node = JsonNode.Parse(GoldenJson)!;
        node["machine"] = new JsonObject { ["id"] = "job" };
        var snapshot = SnapshotJson.Deserialize(node.ToJsonString());

        var machine = Machine.Create("job").Version("1.0.0").Initial("shop").State("shop", s => s.Initial("cart").State("cart")).Build();
        Assert.Throws<InvalidSnapshotException>(() => machine.Restore(snapshot));
    }

    [Fact]
    public void Actor_refs_in_context_persist_as_dollar_actor_and_read_back()
    {
        var snapshot = Golden() with
        {
            Context = new Dictionary<string, object?> { ["peer"] = new SerializedActorRef("job/w") }
        };

        var json = SnapshotJson.Serialize(snapshot);
        Assert.Contains("\"context\":{\"peer\":{\"$actor\":\"job/w\"}}", json);

        var context = Assert.IsType<Dictionary<string, object?>>(SnapshotJson.Deserialize(json).Context);
        Assert.Equal(new SerializedActorRef("job/w"), context["peer"]);
    }

    [Fact]
    public void A_circular_context_cannot_be_persisted()
    {
        var cycle = new Dictionary<string, object?>();
        cycle["self"] = cycle;

        Assert.Throws<JsonException>(() => SnapshotJson.Serialize(Golden() with { Context = cycle }));
    }

    [Fact]
    public void A_timer_aimed_at_a_serialized_actor_ref_resolves_to_the_child_with_that_address()
    {
        var worker = Machine.Create("worker").Initial("busy").State("busy").Build();
        var machine = Machine.Create("job").Actor("worker", worker).Initial("running")
            .State("running", s => s.Invoke("worker", id: "w")).Build();

        var node = JsonNode.Parse(SnapshotJson.Serialize(machine.Persist(
            machine.GetInitialState().State, new PersistOptions { EmbedChildren = false })))!;
        node["timers"] = new JsonArray(JsonNode.Parse(
            """{"id":"t","delay":10,"type":"@xstate.sendTo","event":{"type":"ping"},"target":{"$actor":"job/w"}}"""));

        var restored = machine.Restore(SnapshotJson.Deserialize(node.ToJsonString()),
            new RestoreOptions<Unit> { ContextConverter = _ => Unit.Value });
        Assert.Equal("w", Assert.Single(restored.Timers).Target);
    }

    [Fact]
    public void Unknown_fields_on_the_envelope_children_and_timers_are_ignored()
    {
        var node = JsonNode.Parse(GoldenJson)!;
        node["futureField"] = 1;
        node["_futurePrivate"] = new JsonObject();
        node["children"]!["w"]!["futureField"] = "x";
        node["timers"]![0]!["futureField"] = true;

        Assert.Equal(GoldenJson, SnapshotJson.Serialize(SnapshotJson.Deserialize(node.ToJsonString())));
    }

    public static TheoryData<string, string> Malformed => new()
    {
        { "not json", "{" },
        { "missing value", """{"status":"active","context":null,"children":{},"timers":[],"historyValue":{}}""" },
        { "unknown status", """{"status":"paused","value":"shop","context":null,"children":{},"timers":[],"historyValue":{}}""" },
        { "unknown state", """{"status":"active","value":"gone","context":null,"children":{},"timers":[],"historyValue":{}}""" },
        { "numeric value", """{"status":"active","value":7,"context":null,"children":{},"timers":[],"historyValue":{}}""" },
        { "two children of a compound", """{"status":"active","value":{"shop":"cart","gone":"x"},"context":null,"children":{},"timers":[],"historyValue":{}}""" },
        { "children as array", """{"status":"active","value":"shop","context":null,"children":[],"timers":[],"historyValue":{}}""" },
        { "remote without src", """{"status":"active","value":"shop","context":null,"children":{"r":{"address":"job/r","remote":true}},"timers":[],"historyValue":{}}""" },
        { "unknown src", """{"status":"active","value":"shop","context":null,"children":{"c":{"address":"job/c","src":"nope"}},"timers":[],"historyValue":{}}""" },
        { "inline child with no invoker", """{"status":"active","value":"shop","context":null,"children":{"c":{"address":"job/c"}},"timers":[],"historyValue":{}}""" },
        { "bad timer type", """{"status":"active","value":"shop","context":null,"children":{},"timers":[{"delay":1,"type":"@xstate.cancel","event":{"type":"e"},"target":"self"}],"historyValue":{}}""" },
        { "bad timer target", """{"status":"active","value":"shop","context":null,"children":{},"timers":[{"delay":1,"type":"@xstate.raise","event":{"type":"e"},"target":{"type":"sibling"}}],"historyValue":{}}""" },
        { "unknown child target", """{"status":"active","value":"shop","context":null,"children":{},"timers":[{"delay":1,"type":"@xstate.sendTo","event":{"type":"e"},"target":"ghost"}],"historyValue":{}}""" },
        { "machine without version", """{"status":"active","value":"shop","context":null,"children":{},"timers":[],"historyValue":{},"machine":{"id":"job"}}""" },
        { "bad history", """{"status":"active","value":"shop","context":null,"children":{},"timers":[],"historyValue":{"h":["id"]}}""" }
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void A_malformed_snapshot_is_an_invalid_snapshot(string name, string json)
    {
        _ = name;
        var machine = Machine.Create("job").Initial("shop").State("shop", s => s.Initial("cart").State("cart")).Build();

        Assert.Throws<InvalidSnapshotException>(() =>
            machine.Restore(SnapshotJson.Deserialize(json), new RestoreOptions<Unit> { ContextConverter = _ => Unit.Value }));
    }
}
