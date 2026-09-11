using System.Text.Json;
using XState.Json;
using XState.TestKit;
using Xunit;

namespace XState.Tests;

/// <summary>
/// Behavior round trips for the XState v6 JSON config importer: the machines it produces must
/// run exactly like their hand-built equivalents.
/// </summary>
public class JsonImportTests
{
    private static MachineEvent Ev(string type) => new NamedEvent(type);

    private static State<JsonElement> Snapshot(DeterministicInterpreter actor) =>
        (State<JsonElement>)actor.RootSnapshot;

    private static void RunEffects<T>(TransitionResult<T> result)
    {
        foreach (var effect in result.Effects.OfType<ActionEffect>())
        {
            effect.Exec();
        }
    }

    // --- 1. Toggle ---

    [Fact]
    public void A_toggle_config_runs_like_a_hand_built_toggle()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "toggle",
              "initial": "inactive",
              "states": {
                "inactive": { "on": { "TOGGLE": "active" } },
                "active": { "on": { "TOGGLE": { "target": "inactive" } } }
              }
            }
            """);

        Assert.Equal("toggle", machine.Id);

        var inactive = machine.GetInitialState().State;
        Assert.True(inactive.Matches("inactive"));

        var active = machine.Transition(inactive, Ev("TOGGLE")).State;
        Assert.True(active.Matches("active"));

        var back = machine.Transition(active, Ev("TOGGLE")).State;
        Assert.True(back.Matches("inactive"));

        // Unhandled events are inert.
        Assert.True(machine.Transition(back, Ev("NOPE")).State.Matches("inactive"));
    }

    // --- 2. Nested states + target shorthand ---

    [Fact]
    public void Nested_initial_states_and_string_target_shorthand_resolve()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "nested",
              "initial": "a",
              "states": {
                "a": {
                  "initial": "a1",
                  "on": { "OUT": "b" },
                  "states": {
                    "a1": { "on": { "NEXT": "a2" } },
                    "a2": { "on": { "DEEP": "#nested.b" } }
                  }
                },
                "b": {}
              }
            }
            """);

        var state = machine.GetInitialState().State;
        Assert.True(state.Matches("a.a1"));

        state = machine.Transition(state, Ev("NEXT")).State;
        Assert.True(state.Matches("a.a2"));

        // A parent transition wins when the child has no handler.
        state = machine.Transition(state, Ev("OUT")).State;
        Assert.True(state.Matches("b"));
    }

    // --- 3. Named guards ---

    [Fact]
    public void Guards_are_resolved_by_name_from_the_implementations()
    {
        const string Config =
            """
            {
              "id": "gated",
              "initial": "idle",
              "context": { "allowed": false },
              "states": {
                "idle": { "on": { "GO": { "target": "open", "guard": { "type": "isAllowed" } } } },
                "open": {}
              }
            }
            """;

        static MachineImplementations<JsonElement> Impls() =>
            new MachineImplementations<JsonElement>()
                .Guard("isAllowed", a => a.Context.GetProperty("allowed").GetBoolean());

        var blocked = MachineConfig.FromJson(Config, Impls());
        var blockedState = blocked.GetInitialState().State;
        Assert.True(blocked.Transition(blockedState, Ev("GO")).State.Matches("idle"));

        var allowed = MachineConfig.FromJson(Config.Replace("\"allowed\": false", "\"allowed\": true"), Impls());
        var allowedState = allowed.GetInitialState().State;
        Assert.True(allowed.Transition(allowedState, Ev("GO")).State.Matches("open"));
    }

    // --- 4. Missing implementations are aggregated ---

    [Fact]
    public void Every_missing_implementation_is_reported_in_one_error()
    {
        var error = Assert.Throws<InvalidOperationException>(() => MachineConfig.FromJson(
            """
            {
              "id": "incomplete",
              "initial": "a",
              "states": {
                "a": {
                  "entry": "noAction",
                  "invoke": { "src": "noActor" },
                  "after": { "slowly": "b" },
                  "on": { "GO": { "target": "b", "guard": "noGuard" } }
                },
                "b": {}
              }
            }
            """));

        Assert.Contains("incomplete", error.Message);
        Assert.Contains("action 'noAction'", error.Message);
        Assert.Contains("actor 'noActor'", error.Message);
        Assert.Contains("delay 'slowly'", error.Message);
        Assert.Contains("guard 'noGuard'", error.Message);
        // Paths point at the offending config location.
        Assert.Contains("$.states.a.on.GO.guard", error.Message);
    }

    // --- 5. Named actions ---

    [Fact]
    public void Actions_are_resolved_by_name_and_surface_as_effects()
    {
        var log = new List<string>();

        var machine = MachineConfig.FromJson(
            """
            {
              "id": "actions",
              "initial": "a",
              "states": {
                "a": {
                  "entry": "enterA",
                  "exit": ["exitA"],
                  "on": { "GO": { "target": "b", "actions": ["onGo", { "type": "onGoAgain" }] } }
                },
                "b": { "entry": "enterB" }
              }
            }
            """,
            new MachineImplementations<JsonElement>()
                .Action("enterA", _ => log.Add("enterA"))
                .Action("exitA", _ => log.Add("exitA"))
                .Action("onGo", _ => log.Add("onGo"))
                .Action("onGoAgain", _ => log.Add("onGoAgain"))
                .Action("enterB", _ => log.Add("enterB")));

        var initial = machine.GetInitialState();
        RunEffects(initial);
        Assert.Equal(["enterA"], log);

        RunEffects(machine.Transition(initial.State, Ev("GO")));
        Assert.Equal(["enterA", "exitA", "onGo", "onGoAgain", "enterB"], log);
    }

    // --- 6. `after` with a literal delay ---

    [Fact]
    public void After_with_a_millisecond_key_schedules_a_delayed_send()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "timeout",
              "initial": "waiting",
              "states": {
                "waiting": { "after": { "500": "late" } },
                "late": {}
              }
            }
            """);

        var raise = Assert.Single(machine.GetInitialState().Effects.OfType<RaiseEffect>());
        Assert.Equal(TimeSpan.FromMilliseconds(500), raise.Delay);
        Assert.IsType<AfterEvent>(raise.Event);

        var actor = DeterministicInterpreter.Start(machine);
        actor.AdvanceTime(TimeSpan.FromMilliseconds(499));
        Assert.True(Snapshot(actor).Matches("waiting"));

        actor.AdvanceTime(TimeSpan.FromMilliseconds(1));
        Assert.True(Snapshot(actor).Matches("late"));
    }

    // --- 7. `after` with a named delay ---

    [Fact]
    public void After_with_a_named_delay_resolves_from_the_implementations()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "named-delay",
              "initial": "waiting",
              "delays": { "quick": 100 },
              "states": {
                "waiting": { "after": { "slow": "late", "quick": "early" } },
                "late": {},
                "early": {}
              }
            }
            """,
            new MachineImplementations<JsonElement>().Delay("slow", TimeSpan.FromMilliseconds(750)));

        var delays = machine.GetInitialState().Effects.OfType<RaiseEffect>().Select(r => r.Delay).ToList();
        Assert.Equal([TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(100)], delays);

        var actor = DeterministicInterpreter.Start(machine);
        actor.AdvanceTime(TimeSpan.FromMilliseconds(100));

        // The config's own `delays` entry fires first.
        Assert.True(Snapshot(actor).Matches("early"));
    }

    // --- 8. Parallel regions + final output ---

    [Fact]
    public void Parallel_regions_complete_with_aggregated_json_output()
    {
        object? aggregated = null;

        var machine = MachineConfig.FromJson(
            """
            {
              "id": "agg",
              "initial": "work",
              "states": {
                "work": {
                  "type": "parallel",
                  "on": {
                    "xstate.done.state.agg.work": { "target": "success", "actions": "capture" }
                  },
                  "states": {
                    "upload": {
                      "initial": "pending",
                      "states": {
                        "pending": { "on": { "GO": "done" } },
                        "done": { "type": "final", "output": "/file.png" }
                      }
                    },
                    "validate": {
                      "initial": "pending",
                      "states": {
                        "pending": { "on": { "GO": "done" } },
                        "done": { "type": "final", "output": 42 }
                      }
                    }
                  }
                },
                "success": { "type": "final" }
              }
            }
            """,
            new MachineImplementations<JsonElement>()
                .Action("capture", a => aggregated = ((DoneStateEvent)a.Event).Output));

        var actor = DeterministicInterpreter.Start(machine);
        actor.Send(Ev("GO"));

        Assert.Equal(SnapshotStatus.Done, actor.RootSnapshot.Status);

        var regions = Assert.IsType<Dictionary<string, object?>>(aggregated);
        Assert.Equal("/file.png", Assert.IsType<JsonElement>(regions["upload"]).GetString());
        Assert.Equal(42, Assert.IsType<JsonElement>(regions["validate"]).GetInt32());
    }

    // --- 9. History ---

    [Fact]
    public void History_states_restore_the_most_recent_child()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "hist",
              "initial": "on",
              "states": {
                "on": {
                  "initial": "first",
                  "on": { "POWER": "#hist.off" },
                  "states": {
                    "first": { "on": { "SWITCH": "second" } },
                    "second": {},
                    "recall": { "type": "history", "history": "shallow" }
                  }
                },
                "off": { "on": { "POWER": "on.recall" } }
              }
            }
            """);

        var state = machine.GetInitialState().State;
        Assert.True(state.Matches("on.first"));

        state = machine.Transition(state, Ev("SWITCH")).State;
        Assert.True(state.Matches("on.second"));

        state = machine.Transition(state, Ev("POWER")).State;
        Assert.True(state.Matches("off"));

        state = machine.Transition(state, Ev("POWER")).State;
        Assert.True(state.Matches("on.second"));
    }

    // --- 10. Invoke ---

    [Fact]
    public void Invoke_resolves_actor_logic_by_name_and_takes_onDone()
    {
        object? childOutput = null;

        var child = MachineConfig.FromJson(
            """
            {
              "id": "child",
              "initial": "done",
              "states": { "done": { "type": "final", "output": { "ok": true } } }
            }
            """);

        var parent = MachineConfig.FromJson(
            """
            {
              "id": "parent",
              "initial": "running",
              "states": {
                "running": {
                  "invoke": {
                    "src": "child",
                    "id": "worker",
                    "input": { "seed": 1 },
                    "onDone": { "target": "finished", "actions": "capture" }
                  }
                },
                "finished": {}
              }
            }
            """,
            new MachineImplementations<JsonElement>()
                .ActorLogic("child", child)
                .Action("capture", a => childOutput = ((DoneActorEvent)a.Event).Output));

        var actor = DeterministicInterpreter.Start(parent);

        Assert.True(Snapshot(actor).Matches("finished"));
        Assert.Empty(actor.Diagnostics);
        Assert.True(Assert.IsType<JsonElement>(childOutput).GetProperty("ok").GetBoolean());
    }

    // --- 11. reenter ---

    [Fact]
    public void The_reenter_flag_controls_self_transition_re_entry()
    {
        var entries = 0;

        var machine = MachineConfig.FromJson(
            """
            {
              "id": "reenter",
              "initial": "a",
              "states": {
                "a": {
                  "entry": "count",
                  "on": {
                    "OUTER": { "target": "a", "reenter": true },
                    "INNER": { "target": "a" }
                  }
                }
              }
            }
            """,
            new MachineImplementations<JsonElement>().Action("count", _ => entries++));

        var initial = machine.GetInitialState();
        RunEffects(initial);
        Assert.Equal(1, entries);

        var inner = machine.Transition(initial.State, Ev("INNER"));
        RunEffects(inner);
        Assert.Equal(1, entries);

        RunEffects(machine.Transition(inner.State, Ev("OUTER")));
        Assert.Equal(2, entries);
    }

    // --- 12. Context ---

    [Fact]
    public void The_config_context_flows_into_guards_and_snapshots_as_json()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "ctx",
              "initial": "idle",
              "context": { "user": { "name": "Ada" }, "count": 3 },
              "states": {
                "idle": { "on": { "GO": { "target": "named", "guard": "hasName" } } },
                "named": {}
              }
            }
            """,
            new MachineImplementations<JsonElement>()
                .Guard("hasName", a => a.Context.GetProperty("user").GetProperty("name").GetString() == "Ada"));

        var state = machine.GetInitialState().State;
        Assert.Equal(3, state.Context.GetProperty("count").GetInt32());

        var next = machine.Transition(state, Ev("GO")).State;
        Assert.True(next.Matches("named"));
        Assert.Equal("Ada", next.Context.GetProperty("user").GetProperty("name").GetString());
    }

    // --- 13. Transition arrays ---

    [Fact]
    public void An_array_of_transitions_is_evaluated_in_document_order()
    {
        const string Config =
            """
            {
              "id": "candidates",
              "initial": "idle",
              "context": { "count": 0 },
              "states": {
                "idle": {
                  "on": {
                    "GO": [
                      { "target": "high", "guard": "isHigh" },
                      { "target": "low" }
                    ]
                  }
                },
                "high": {},
                "low": {}
              }
            }
            """;

        static MachineImplementations<JsonElement> Impls() =>
            new MachineImplementations<JsonElement>()
                .Guard("isHigh", a => a.Context.GetProperty("count").GetInt32() >= 3);

        var low = MachineConfig.FromJson(Config, Impls());
        Assert.True(low.Transition(low.GetInitialState().State, Ev("GO")).State.Matches("low"));

        var high = MachineConfig.FromJson(Config.Replace("\"count\": 0", "\"count\": 5"), Impls());
        Assert.True(high.Transition(high.GetInitialState().State, Ev("GO")).State.Matches("high"));
    }

    // --- 14. Typed context ---

    [Fact]
    public void A_typed_context_is_built_by_the_supplied_context_factory()
    {
        var machine = MachineConfig.FromJson<Counter>(
            """
            {
              "id": "typed",
              "initial": "idle",
              "context": { "count": 4 },
              "states": {
                "idle": { "on": { "GO": { "target": "big", "guard": "isBig" } } },
                "big": {}
              }
            }
            """,
            new MachineImplementations<Counter>()
                .Context(input => new Counter(((JsonElement)input!).GetProperty("count").GetInt32()))
                .Guard("isBig", a => a.Context.Count > 3));

        var state = machine.GetInitialState().State;
        Assert.Equal(4, state.Context.Count);
        Assert.True(machine.Transition(state, Ev("GO")).State.Matches("big"));
    }

    // --- 15. State-level onDone ---

    [Fact]
    public void A_state_level_onDone_leaves_the_state_when_its_region_finishes()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "j",
              "initial": "a",
              "states": {
                "a": {
                  "initial": "x",
                  "states": {
                    "x": { "on": { "NEXT": "y" } },
                    "y": { "type": "final" }
                  },
                  "onDone": "b"
                },
                "b": {}
              }
            }
            """);

        var next = machine.Transition(machine.GetInitialState().State, Ev("NEXT")).State;

        Assert.Contains("j.b", next.Configuration);
        Assert.True(next.Matches("b"));
    }

    [Fact]
    public void A_state_level_onDone_supports_the_object_and_array_forms()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "j",
              "initial": "a",
              "states": {
                "a": {
                  "initial": "x",
                  "states": {
                    "x": { "on": { "NEXT": "y" } },
                    "y": { "type": "final" }
                  },
                  "onDone": [
                    { "target": "blocked", "guard": "never" },
                    { "target": "b" }
                  ]
                },
                "b": {},
                "blocked": {}
              }
            }
            """,
            new MachineImplementations<JsonElement>().Guard("never", _ => false));

        Assert.True(machine.Transition(machine.GetInitialState().State, Ev("NEXT")).State.Matches("b"));
    }

    // --- XState v4 keys are rejected instead of silently dropped ---

    [Theory]
    [InlineData("\"cond\": \"never\"", "cond", "guard")]
    [InlineData("\"internal\": false", "internal", "reenter")]
    [InlineData("\"onEntry\": \"noop\"", "onEntry", "entry")]
    [InlineData("\"onExit\": \"noop\"", "onExit", "exit")]
    [InlineData("\"activities\": \"beep\"", "activities", "invoke")]
    public void Legacy_v4_keys_are_rejected_with_their_v5_replacement(
        string legacyJson,
        string legacyKey,
        string replacement)
    {
        // `cond`/`internal` sit on the transition object; the rest sit on the state object.
        var (transitionExtra, stateExtra) = legacyKey is "cond" or "internal"
            ? (", " + legacyJson, "")
            : ("", ", " + legacyJson);

        var error = Assert.Throws<InvalidOperationException>(() => MachineConfig.FromJson(
            $$"""
            {
              "id": "legacy",
              "initial": "a",
              "states": {
                "a": { "on": { "GO": { "target": "b"{{transitionExtra}} } }{{stateExtra}} },
                "b": {}
              }
            }
            """,
            new MachineImplementations<JsonElement>()
                .Guard("never", _ => false)
                .Action("noop", _ => { })));

        Assert.Contains($"'{legacyKey}'", error.Message);
        Assert.Contains($"'{replacement}'", error.Message);
        Assert.Contains("legacy", error.Message);
    }


    // --- Strictness: unknown keys are typos, not annotations ---

    [Fact]
    public void An_unknown_key_is_rejected_with_its_path_and_the_key_it_resembles()
    {
        var error = Assert.Throws<JsonException>(() => MachineConfig.FromJson(
            """
            {
              "id": "typo",
              "initial": "a",
              "states": { "a": { "entyr": "noop" } }
            }
            """));

        Assert.Contains("$.states.a.entyr", error.Message);
        Assert.Contains("did you mean 'entry'", error.Message);
    }

    [Fact]
    public void Unknown_keys_are_ignored_when_the_reader_is_asked_to_be_lenient()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "extras",
              "initial": "a",
              "states": {
                "a": { "order": 3, "on": { "GO": { "target": "b", "x-note": 1 } } },
                "b": {}
              }
            }
            """,
            options: new JsonConfigOptions { IgnoreUnknownKeys = true });

        Assert.True(machine.Transition(machine.GetInitialState().State, Ev("GO")).State.Matches("b"));
    }

    [Fact]
    public void Meta_and_description_are_read_onto_states_and_transitions()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "annotated",
              "description": "a machine with annotations",
              "meta": { "author": "someone" },
              "initial": "a",
              "states": {
                "a": {
                  "description": "the first state",
                  "meta": { "note": "hi" },
                  "on": {
                    "GO": {
                      "target": "b",
                      "description": "the only transition",
                      "meta": { "kind": "manual" }
                    }
                  }
                },
                "b": {}
              }
            }
            """);

        Assert.Equal("a machine with annotations", machine.Root.Description);
        Assert.Equal("someone", ((JsonElement)machine.Root.Meta!).GetProperty("author").GetString());

        var a = machine.GetNodeById("annotated.a");
        Assert.Equal("the first state", a.Description);
        Assert.Equal("hi", ((JsonElement)a.Meta!).GetProperty("note").GetString());

        var transition = Assert.Single(a.Transitions);
        Assert.Equal("the only transition", transition.Description);
        Assert.Equal("manual", ((JsonElement)transition.Meta!).GetProperty("kind").GetString());
    }

    [Theory]
    // Known v6 keys this port cannot honour are named, with their path, instead of being dropped.
    [InlineData("""{ "id": "m", "route": { "guard": "g" }, "initial": "a", "states": { "a": {} } }""", "route", "$")]
    [InlineData(
        """{ "id": "m", "initial": "a", "states": { "a": { "on": { "GO": { "matches": { "x": 1 } } } } } }""",
        "matches", "$.states.a.on.GO")]
    [InlineData(
        """{ "id": "m", "initial": "a", "states": { "a": { "invoke": { "src": "s", "registryKey": "k" } } } }""",
        "registryKey", "$.states.a.invoke")]
    [InlineData("""{ "id": "m", "schemas": { "context": {} }, "initial": "a", "states": { "a": {} } }""", "schemas", "$")]
    [InlineData("""{ "id": "m", "@exprLang": "js", "initial": "a", "states": { "a": {} } }""", "@exprLang", "$")]
    [InlineData("""{ "id": "m", "initial": "a", "states": { "a": { "input": 1 } } }""", "input", "$.states.a")]
    [InlineData("""{ "id": "m", "initial": "a", "states": { "a": { "context": { "x": 1 } } } }""", "context", "$.states.a")]
    [InlineData("""{ "id": "m", "initial": "a", "states": { "a": { "version": "1" } } }""", "version", "$.states.a")]
    public void Known_but_unsupported_keys_are_rejected_loudly(string config, string key, string path)
    {
        var error = Assert.Throws<NotSupportedException>(() => MachineConfig.FromJson(config));

        Assert.Contains($"'{key}'", error.Message);
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void Choice_states_are_rejected_with_the_builder_alternative()
    {
        var error = Assert.Throws<NotSupportedException>(() => MachineConfig.FromJson(
            """
            {
              "id": "routing",
              "initial": "pick",
              "states": {
                "pick": { "type": "choice", "choice": [{ "target": "a" }] },
                "a": {}
              }
            }
            """));

        Assert.Contains("$.states.pick", error.Message);
        Assert.Contains(".Choice(", error.Message);
    }

    [Fact]
    public void An_empty_schemas_object_is_accepted()
    {
        var machine = MachineConfig.FromJson(
            """{ "id": "m", "schemas": {}, "initial": "a", "states": { "a": {} } }""");

        Assert.True(machine.GetInitialState().State.Matches("a"));
    }

    // --- version, internalEvents ---

    [Fact]
    public void The_machine_version_and_internal_events_are_read_from_the_root()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "versioned",
              "version": "1.4.0",
              "internalEvents": ["TICK"],
              "initial": "a",
              "states": { "a": { "on": { "TICK": "b" } }, "b": {} } }
            """);

        Assert.Equal("1.4.0", machine.Version);
        Assert.Equal(["TICK"], machine.InternalEvents);

        // An internal event arriving from outside is dead-lettered, not delivered.
        var result = machine.Transition(machine.GetInitialState().State, Ev("TICK"));
        Assert.True(result.State.Matches("a"));
        Assert.Equal("internalEvent", Assert.Single(result.Effects.OfType<DeadLetterEffect>()).Reason);
    }

    // --- Built-in actions (createMachineFromConfig.ts:785-826) ---

    [Fact]
    public void The_built_in_raise_action_queues_an_event_to_self()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "raiser",
              "initial": "a",
              "states": {
                "a": {
                  "on": {
                    "NEXT": { "actions": [{ "type": "@xstate.raise", "event": { "type": "TO_B" } }] },
                    "TO_B": "b"
                  }
                },
                "b": {}
              }
            }
            """);

        var next = machine.Transition(machine.GetInitialState().State, Ev("NEXT")).State;
        Assert.True(next.Matches("b"));
    }

    [Fact]
    public void The_built_in_raise_action_honours_a_delay_and_an_id_that_cancel_can_name()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "delayed",
              "initial": "a",
              "states": {
                "a": {
                  "on": {
                    "ARM": {
                      "actions": [
                        { "type": "@xstate.raise", "event": { "type": "LATE" }, "id": "armed", "delay": 500 }
                      ]
                    },
                    "DISARM": { "actions": [{ "type": "@xstate.cancel", "id": "armed" }] },
                    "LATE": "b"
                  }
                },
                "b": {}
              }
            }
            """);

        var armed = machine.Transition(machine.GetInitialState().State, Ev("ARM"));
        var raise = Assert.Single(armed.Effects.OfType<RaiseEffect>());
        Assert.Equal(TimeSpan.FromMilliseconds(500), raise.Delay);
        Assert.Equal("armed", raise.Id);
        Assert.Equal("LATE", raise.Event.Type);

        var disarmed = machine.Transition(armed.State, Ev("DISARM"));
        Assert.Equal("armed", Assert.Single(disarmed.Effects.OfType<CancelEffect>()).SendId);
    }

    [Fact]
    public void The_built_in_emit_action_emits_the_event_to_subscribers()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "emitter",
              "initial": "a",
              "states": {
                "a": {
                  "on": {
                    "NEXT": {
                      "actions": [
                        { "type": "@xstate.emit", "event": { "type": "EMITTED", "msg": "hello" } }
                      ]
                    }
                  }
                }
              }
            }
            """);

        var result = machine.Transition(machine.GetInitialState().State, Ev("NEXT"));
        var emitted = Assert.Single(result.Effects.OfType<EmitEffect>());

        Assert.Equal("EMITTED", emitted.Event.Type);
        Assert.Equal(
            "hello",
            ((JsonElement)((NamedEvent)emitted.Event).Data!).GetProperty("msg").GetString());
    }

    [Fact]
    public void The_built_in_assign_action_merges_its_patch_into_the_json_context()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "counter",
              "context": { "count": 0, "name": "ada" },
              "initial": "idle",
              "states": {
                "idle": {
                  "on": {
                    "INC": {
                      "actions": [{ "type": "@xstate.assign", "context": { "count": 1, "extra": true } }]
                    }
                  }
                }
              }
            }
            """);

        var next = machine.Transition(machine.GetInitialState().State, Ev("INC")).State;

        Assert.Equal(1, next.Context.GetProperty("count").GetInt32());
        Assert.True(next.Context.GetProperty("extra").GetBoolean());
        // Untouched properties survive the merge (v6 uses Object.assign).
        Assert.Equal("ada", next.Context.GetProperty("name").GetString());
    }

    [Fact]
    public void The_built_in_log_action_carries_its_args_as_log_params()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "logger",
              "initial": "a",
              "states": { "a": { "entry": [{ "type": "@xstate.log", "args": ["hello"] }] } }
            }
            """);

        var effect = Assert.Single(
            machine.GetInitialState().Effects.OfType<ActionEffect>(), e => e.Type == "xstate.log");
        var @params = Assert.IsType<LogParams>(effect.Params);

        Assert.Equal("hello", Assert.IsType<JsonElement>(@params.Message).GetString());
    }

    [Fact]
    public void An_at_xstate_action_that_v6_does_not_define_is_rejected()
    {
        var error = Assert.Throws<NotSupportedException>(() => MachineConfig.FromJson(
            """
            {
              "id": "m",
              "initial": "a",
              "states": { "a": { "entry": [{ "type": "@xstate.sendTo", "to": "x" }] } }
            }
            """));

        Assert.Contains("@xstate.sendTo", error.Message);
        Assert.Contains("$.states.a.entry[0]", error.Message);
    }

    // --- Machine-level action definitions ---

    [Fact]
    public void A_named_action_definition_expands_into_its_actions_in_order()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "defs",
              "context": { "count": 0 },
              "actions": {
                "incTwice": [
                  { "type": "@xstate.assign", "context": { "count": 1 } },
                  { "type": "@xstate.assign", "context": { "count": 2 } }
                ]
              },
              "entry": [{ "type": "incTwice" }]
            }
            """);

        Assert.Equal(2, machine.GetInitialState().State.Context.GetProperty("count").GetInt32());
    }

    [Fact]
    public void A_reference_passes_its_params_down_into_the_definition_it_names()
    {
        object? seen = null;

        var machine = MachineConfig.FromJson(
            """
            {
              "id": "params",
              "actions": { "track": { "type": "record" } },
              "entry": [{ "type": "track", "params": { "value": 2 } }]
            }
            """,
            new MachineImplementations<JsonElement>().Action("record", a => seen = a.Params));

        RunEffects(machine.GetInitialState());

        Assert.Equal(2, Assert.IsType<JsonElement>(seen).GetProperty("value").GetInt32());
    }

    [Fact]
    public void A_named_action_reference_carries_its_params_to_the_implementation_and_the_effect()
    {
        object? seen = null;

        var machine = MachineConfig.FromJson(
            """
            {
              "id": "params",
              "initial": "a",
              "states": { "a": { "entry": [{ "type": "track", "params": { "key": "loaded" } }] } }
            }
            """,
            new MachineImplementations<JsonElement>().Action("track", a => seen = a.Params));

        var initial = machine.GetInitialState();
        var effect = Assert.Single(initial.Effects.OfType<ActionEffect>());

        Assert.Equal("track", effect.Type);
        Assert.Equal("loaded", Assert.IsType<JsonElement>(effect.Params).GetProperty("key").GetString());

        RunEffects(initial);
        Assert.Equal("loaded", Assert.IsType<JsonElement>(seen).GetProperty("key").GetString());
    }

    [Fact]
    public void Circular_action_definitions_are_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() => MachineConfig.FromJson(
            """
            {
              "id": "loop",
              "actions": { "a": { "type": "b" }, "b": { "type": "a" } },
              "entry": [{ "type": "a" }]
            }
            """));

        Assert.Equal("Circular action reference: a -> b -> a", error.Message);
    }

    // --- Guards: params, definitions, built-ins ---

    [Fact]
    public void A_guard_reference_passes_its_params_to_the_implementation()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "gated",
              "context": { "count": 5 },
              "initial": "idle",
              "states": {
                "idle": {
                  "on": {
                    "GO": [
                      { "target": "big", "guard": { "type": "atLeast", "params": { "min": 10 } } },
                      { "target": "small", "guard": { "type": "atLeast", "params": { "min": 3 } } }
                    ]
                  }
                },
                "big": {},
                "small": {}
              }
            }
            """,
            new MachineImplementations<JsonElement>().Guard(
                "atLeast",
                a => a.Context.GetProperty("count").GetInt32() >=
                     ((JsonElement)a.Params!).GetProperty("min").GetInt32()));

        Assert.True(machine.Transition(machine.GetInitialState().State, Ev("GO")).State.Matches("small"));
    }

    [Fact]
    public void A_declarative_guard_definition_is_evaluated_with_the_reference_params()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "declarative",
              "context": { "role": "admin" },
              "guards": { "isAdmin": { "when": { "type": "hasRole", "params": { "role": "admin" } } } },
              "initial": "idle",
              "states": {
                "idle": { "on": { "GO": { "target": "open", "guard": { "type": "isAdmin" } } } },
                "open": {}
              }
            }
            """,
            new MachineImplementations<JsonElement>().Guard(
                "hasRole",
                a => a.Context.GetProperty("role").GetString() ==
                     ((JsonElement)a.Params!).GetProperty("role").GetString()));

        Assert.True(machine.Transition(machine.GetInitialState().State, Ev("GO")).State.Matches("open"));
    }

    [Fact]
    public void The_built_in_stateIn_and_not_guards_are_resolved()
    {
        const string Config =
            """
            {
              "id": "regions",
              "type": "parallel",
              "states": {
                "left": {
                  "initial": "idle",
                  "states": { "idle": { "id": "ready" }, "busy": {} }
                },
                "right": {
                  "initial": "waiting",
                  "states": {
                    "waiting": {
                      "on": {
                        "GO": [
                          {
                            "target": "allowed",
                            "guard": { "type": "xstate.stateIn", "params": { "stateId": "#ready" } }
                          },
                          {
                            "target": "denied",
                            "guard": { "type": "xstate.not", "params": { "guard": { "type": "never" } } }
                          }
                        ]
                      }
                    },
                    "allowed": {},
                    "denied": {}
                  }
                }
              }
            }
            """;

        static MachineImplementations<JsonElement> Impls() =>
            new MachineImplementations<JsonElement>().Guard("never", _ => false);

        var machine = MachineConfig.FromJson(Config, Impls());
        Assert.True(machine.Transition(machine.GetInitialState().State, Ev("GO")).State.Matches("right.allowed"));

        // With the left region elsewhere, `xstate.stateIn` fails and `xstate.not(never)` wins.
        var moved = MachineConfig.FromJson(Config.Replace("\"initial\": \"idle\"", "\"initial\": \"busy\""), Impls());
        Assert.True(moved.Transition(moved.GetInitialState().State, Ev("GO")).State.Matches("right.denied"));
    }

    // --- Timeouts, onError, onSnapshot ---

    [Fact]
    public void A_state_timeout_and_onTimeout_are_read()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "sla",
              "initial": "waiting",
              "states": {
                "waiting": { "timeout": "1s", "onTimeout": { "target": "escalated" } },
                "escalated": {}
              }
            }
            """);

        var actor = DeterministicInterpreter.Start(machine);
        actor.AdvanceTime(TimeSpan.FromMilliseconds(999));
        Assert.True(Snapshot(actor).Matches("waiting"));

        actor.AdvanceTime(TimeSpan.FromMilliseconds(1));
        Assert.True(Snapshot(actor).Matches("escalated"));
    }

    [Fact]
    public void An_invoke_timeout_onTimeout_and_onSnapshot_are_read()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "work",
              "initial": "working",
              "states": {
                "working": {
                  "invoke": {
                    "src": "waiter",
                    "id": "child",
                    "timeout": 1000,
                    "onTimeout": { "target": "timedOut" },
                    "onDone": { "target": "finished" }
                  }
                },
                "timedOut": {},
                "finished": {}
              }
            }
            """,
            new MachineImplementations<JsonElement>().ActorLogic("waiter", new WaiterLogic()));

        var actor = DeterministicInterpreter.Start(machine);
        actor.AdvanceTime(TimeSpan.FromSeconds(1));
        Assert.True(Snapshot(actor).Matches("timedOut"));
    }

    [Fact]
    public void A_state_level_onError_catches_an_invoked_actor_failure()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "guarded",
              "initial": "running",
              "states": {
                "running": {
                  "invoke": { "src": "boom", "id": "child" },
                  "onError": { "target": "failed" }
                },
                "failed": {}
              }
            }
            """,
            new MachineImplementations<JsonElement>().ActorLogic("boom", new WaiterLogic()));

        var state = machine.GetInitialState().State;
        var failed = machine.Transition(state, new ErrorActorEvent("child", new InvalidOperationException("boom")));

        Assert.True(failed.State.Matches("failed"));
    }

    // --- initial content, transition input and context ---

    [Fact]
    public void An_initial_object_carries_its_target_and_state_input()
    {
        object? seen = null;

        var machine = MachineConfig.FromJson(
            """
            {
              "id": "seeded",
              "initial": { "target": "a", "input": { "seed": 7 } },
              "states": { "a": { "entry": "capture" } }
            }
            """,
            new MachineImplementations<JsonElement>().Action("capture", a => seen = a.Input));

        RunEffects(machine.GetInitialState());

        Assert.Equal(7, Assert.IsType<JsonElement>(seen).GetProperty("seed").GetInt32());
    }

    [Fact]
    public void A_transition_input_is_recorded_under_the_target_state()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "inputs",
              "initial": "idle",
              "states": {
                "idle": { "on": { "GO": { "target": "active", "input": 42 } } },
                "active": { "id": "active" }
              }
            }
            """);

        var next = machine.Transition(machine.GetInitialState().State, Ev("GO")).State;

        Assert.Equal(42, Assert.IsType<JsonElement>(next.StateInputs["active"]).GetInt32());
    }

    [Fact]
    public void A_transition_context_patch_is_applied_before_the_transition_actions()
    {
        var order = new List<string>();

        var machine = MachineConfig.FromJson(
            """
            {
              "id": "patched",
              "context": { "count": 0, "name": "ada" },
              "initial": "idle",
              "states": {
                "idle": { "on": { "GO": { "target": "done", "context": { "count": 9 }, "actions": "look" } } },
                "done": {}
              }
            }
            """,
            new MachineImplementations<JsonElement>()
                .Action("look", a => order.Add(a.Context.GetProperty("count").GetInt32().ToString())));

        var result = machine.Transition(machine.GetInitialState().State, Ev("GO"));
        RunEffects(result);

        Assert.Equal(9, result.State.Context.GetProperty("count").GetInt32());
        Assert.Equal("ada", result.State.Context.GetProperty("name").GetString());
        Assert.Equal(["9"], order);
    }

    // --- Delays ---

    [Fact]
    public void A_delay_definition_with_a_duration_object_is_read()
    {
        var machine = MachineConfig.FromJson(
            """
            {
              "id": "durations",
              "delays": { "slow": { "duration": "1.5s" } },
              "initial": "waiting",
              "states": { "waiting": { "after": { "slow": "late" } }, "late": {} }
            }
            """);

        var raise = Assert.Single(machine.GetInitialState().Effects.OfType<RaiseEffect>());
        Assert.Equal(TimeSpan.FromMilliseconds(1500), raise.Delay);
    }

    [Fact]
    public void Circular_guard_definitions_are_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() => MachineConfig.FromJson(
            """
            {
              "id": "loop",
              "guards": { "a": { "when": { "type": "b" } }, "b": { "when": { "type": "a" } } },
              "initial": "idle",
              "states": {
                "idle": { "on": { "GO": { "target": "done", "guard": { "type": "a" } } } },
                "done": {}
              }
            }
            """));

        Assert.Equal("Circular guard reference: a -> b -> a", error.Message);
    }

    [Fact]
    public void Leniency_covers_unknown_keys_only_never_unsupported_ones()
    {
        // `IgnoreUnknownKeys` forgives a typo, but a key the schema defines still has to mean
        // something — dropping it would change behaviour.
        var error = Assert.Throws<NotSupportedException>(() => MachineConfig.FromJson(
            """
            {
              "id": "m",
              "initial": "a",
              "states": { "a": { "on": { "GO": { "target": "b", "matches": { "x": 1 } } } }, "b": {} }
            }
            """,
            options: new JsonConfigOptions { IgnoreUnknownKeys = true }));

        Assert.Contains("'matches'", error.Message);
    }

    [Fact]
    public void An_expression_delay_is_rejected_because_there_is_no_evaluator()
    {
        var error = Assert.Throws<NotSupportedException>(() => MachineConfig.FromJson(
            """
            {
              "id": "expr",
              "delays": { "computed": { "duration": { "@expr": "context.wait" } } },
              "initial": "waiting",
              "states": { "waiting": { "after": { "computed": "late" } }, "late": {} }
            }
            """));

        Assert.Contains("$.delays.computed", error.Message);
    }
}
