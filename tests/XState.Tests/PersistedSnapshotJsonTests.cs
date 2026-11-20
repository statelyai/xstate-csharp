using System.Text.Json;
using System.Text.Json.Nodes;
using XState.Persistence;
using Xunit;

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
                Occurrence = 0,
                Seq = 0,
                DelayMs = 500,
                Type = "@xstate.sendTo",
                Event = new NamedEvent("ping"),
                Target = EffectTarget.Parent,
                ScheduledAt = 1234
            },
            new PersistedTimer
            {
                Occurrence = 1,
                DelayMs = 5000,
                Type = "@xstate.raise",
                Event = new AfterEvent("5000", "job.shop"),
                Target = null
            }
        ],
        Machine = new PersistedMachineIdentity("job", "1.0.0"),
        NextActorIds = new Dictionary<string, int> { ["x"] = 1 },
        NextTimerId = 1,
        NextTimerOccurrence = 2,
        NextIncarnation = 2
    };

    private const string GoldenJson =
        "{\"status\":\"active\",\"value\":{\"shop\":\"cart\"}," +
        "\"historyValue\":{\"job.shop.hist\":[{\"id\":\"job.shop.cart\"}]},\"context\":{\"count\":1}," +
        "\"children\":{\"w\":{\"address\":\"job/w\",\"src\":\"worker\",\"syncSnapshot\":true,\"snapshot\":{\"status\":\"active\",\"value\":\"busy\",\"historyValue\":{},\"context\":null,\"children\":{},\"timers\":[],\"nextTimerId\":0,\"nextIncarnation\":0},\"localIncarnation\":\"0\"}," +
        "\"r\":{\"address\":\"job/r\",\"src\":\"remote\",\"remote\":true,\"incarnation\":\"1\"}}," +
        "\"timers\":[{\"occurrence\":0,\"seq\":0,\"delay\":500,\"type\":\"@xstate.sendTo\",\"event\":{\"type\":\"ping\",\"@event\":\"NamedEvent\",\"Name\":\"ping\",\"Data\":null},\"target\":{\"type\":\"parent\"},\"scheduledAt\":1234}," +
        "{\"occurrence\":1,\"delay\":5000,\"type\":\"@xstate.raise\",\"event\":{\"type\":\"xstate.after\",\"@event\":\"AfterEvent\",\"Delay\":\"5000\",\"StateId\":\"job.shop\"},\"target\":\"self\"}]," +
        "\"stateInputs\":{\"job.shop\":{\"n\":2}},\"nextTimerId\":1,\"nextTimerOccurrence\":2,\"nextActorIds\":{\"x\":1},\"nextIncarnation\":2," +
        "\"machine\":{\"id\":\"job\",\"version\":\"1.0.0\"}}";

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
    public void Actor_refs_in_context_persist_as_at_actor_and_read_back()
    {
        var snapshot = Golden() with
        {
            Context = new Dictionary<string, object?> { ["peer"] = new SerializedActorRef("job/w") }
        };

        var json = SnapshotJson.Serialize(snapshot);
        Assert.Contains("\"context\":{\"peer\":{\"@actor\":\"job/w\"}}", json);

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
            """{"id":"t","delay":10,"type":"@xstate.sendTo","event":{"type":"ping"},"target":{"@actor":"job/w"}}"""));

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
        { "bad history", """{"status":"active","value":"shop","context":null,"children":{},"timers":[],"historyValue":{"h":["id"]}}""" },
        { "null children", """{"status":"active","value":"shop","context":null,"children":null,"timers":[],"historyValue":{}}""" },
        { "null timers", """{"status":"active","value":"shop","context":null,"children":{},"timers":null,"historyValue":{}}""" },
        { "null history", """{"status":"active","value":"shop","context":null,"children":{},"timers":[],"historyValue":null}""" },
        { "null counters", """{"status":"active","value":"shop","context":null,"children":{},"timers":[],"historyValue":{},"nextActorIds":null}""" },
        { "negative timer counter", """{"status":"active","value":"shop","context":null,"children":{},"timers":[],"historyValue":{},"nextTimerId":-1}""" },
        { "fractional timer counter", """{"status":"active","value":"shop","context":null,"children":{},"timers":[],"historyValue":{},"nextTimerId":0.5}""" },
        { "negative timer occurrence counter", """{"status":"active","value":"shop","context":null,"children":{},"timers":[],"historyValue":{},"nextTimerOccurrence":-1}""" },
        { "fractional timer occurrence counter", """{"status":"active","value":"shop","context":null,"children":{},"timers":[],"historyValue":{},"nextTimerOccurrence":0.5}""" },
        { "string timer occurrence counter", """{"status":"active","value":"shop","context":null,"children":{},"timers":[],"historyValue":{},"nextTimerOccurrence":"invalid"}""" },
        { "negative timer occurrence", """{"status":"active","value":"shop","context":null,"children":{},"timers":[{"occurrence":-1,"delay":1,"type":"@xstate.raise","event":{"type":"e"},"target":"self"}],"historyValue":{}}""" },
        { "fractional timer occurrence", """{"status":"active","value":"shop","context":null,"children":{},"timers":[{"occurrence":0.5,"delay":1,"type":"@xstate.raise","event":{"type":"e"},"target":"self"}],"historyValue":{}}""" },
        { "string timer occurrence", """{"status":"active","value":"shop","context":null,"children":{},"timers":[{"occurrence":"invalid","delay":1,"type":"@xstate.raise","event":{"type":"e"},"target":"self"}],"historyValue":{}}""" },
        { "string timer counter", """{"status":"active","value":"shop","context":null,"children":{},"timers":[],"historyValue":{},"nextTimerId":"1"}""" }
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

    [Fact]
    public void No_persisted_field_name_starts_with_a_dollar_sign_or_an_underscore()
    {
        var worker = Machine.Create("worker").Initial("busy").State("busy").Build();
        var machine = Machine.Create("job")
            .Actor("worker", worker)
            .Initial("running")
            .State("running", s => s
                .Invoke("worker", id: "w")
                .On<Go>(t => t
                    .Do(new SpawnAction<Unit>(worker, Src: "worker"))
                    .Raise(new Next(), delay: TimeSpan.FromSeconds(1), id: "later")
                    .Raise(new Ping(), delay: TimeSpan.FromSeconds(2))))
            .Build();

        var state = machine.Transition(machine.GetInitialState().State, new Go()).State;
        var persisted = machine.Persist(
            state, new PersistOptions { ChildSnapshot = _ => worker.Persist(worker.GetInitialState().State) }) with
        {
            Context = new Dictionary<string, object?> { ["peer"] = new SerializedActorRef("job/w", "0") }
        };

        using var document = JsonDocument.Parse(SnapshotJson.Serialize(persisted));
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("children").EnumerateObject().Count());
        Assert.Equal(1, root.GetProperty("nextActorIds").GetProperty("worker").GetInt32());
        Assert.Collection(
            root.GetProperty("timers").EnumerateArray(),
            t => Assert.Equal("later", t.GetProperty("id").GetString()),
            t => Assert.Equal(0, t.GetProperty("seq").GetInt32()));
        Assert.Equal("job/w", root.GetProperty("context").GetProperty("peer").GetProperty("@actor").GetString());

        var names = new List<string>();
        CollectPropertyNames(root, names);
        Assert.DoesNotContain(names, n => n.StartsWith('$') || n.StartsWith('_'));
    }

    [Fact]
    public void Next_actor_ids_are_omitted_when_empty_and_read_back_as_empty_when_absent()
    {
        var machine = Machine.Create("job").Initial("shop").State("shop", s => s.Initial("cart").State("cart")).Build();
        var json = SnapshotJson.Serialize(machine.Persist(machine.GetInitialState().State));

        Assert.Null(JsonNode.Parse(json)!["nextActorIds"]);

        var read = SnapshotJson.Deserialize(json);
        Assert.Empty(read.NextActorIds);
        Assert.Empty(machine.Restore(read, new RestoreOptions<Unit> { ContextConverter = _ => Unit.Value }).State.NextActorIds);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0.5")]
    public void An_invalid_next_timer_id_is_an_invalid_snapshot_not_a_json_exception(string value)
    {
        var json = $$"""{"status":"active","value":"shop","context":null,"children":{},"timers":[],"historyValue":{},"nextTimerId":{{value}}}""";

        Assert.Throws<InvalidSnapshotException>(() =>
            JsonSerializer.Deserialize<PersistedSnapshot>(json, SnapshotJson.DefaultOptions()));
    }

    [Fact]
    public void A_negative_next_timer_id_is_rejected_on_restore()
    {
        var machine = Machine.Create("job").Initial("shop").State("shop", s => s.Initial("cart").State("cart")).Build();
        var persisted = machine.Persist(machine.GetInitialState().State) with { NextTimerId = -1 };

        Assert.Throws<InvalidSnapshotException>(() => machine.Restore(persisted));
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(9, 9)]
    public void Restore_raises_next_timer_id_above_every_persisted_timer_seq(int persistedNext, int expected)
    {
        var machine = Machine.Create("job")
            .Initial("idle")
            .State("idle", s => s.On<Go>(t => t.Raise(new Ping(), delay: TimeSpan.FromSeconds(1))))
            .Build();

        var state = machine.Transition(machine.GetInitialState().State, new Go()).State;
        var persisted = machine.Persist(state);
        persisted = persisted with
        {
            Timers = [Assert.Single(persisted.Timers) with { Seq = 5 }],
            NextTimerId = persistedNext
        };

        Assert.Equal(expected, machine.Restore(persisted).State.NextTimerId);
    }

    private static void CollectPropertyNames(JsonElement element, List<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    names.Add(property.Name);
                    CollectPropertyNames(property.Value, names);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectPropertyNames(item, names);
                }

                break;
        }
    }
}
