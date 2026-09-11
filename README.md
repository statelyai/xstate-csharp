# XState for .NET

State machines and statecharts for .NET. A port of [XState](https://github.com/statelyai/xstate) (v6 semantics) by the author of XState.

- **`XState`**: zero-dependency pure interpreter core: `(machine, state, event) => (nextState, effects)`
- **`XState.Scxml`**: SCXML import/export with an ECMAScript datamodel (Jint), tested against the W3C SCXML conformance suite

> ⚠️ Alpha. API surface is settling.

## Install

```bash
dotnet add package XState
```

```bash
dotnet add package XState.Scxml
```

Both packages target **.NET 8 (LTS)** and **.NET 10**.

## Core

Transitions are a pure function with no side effects:

```
(machine, state, event) => (nextState, effects)
```

```csharp
using XState;

public record ToggleContext(int Count);
public record Toggle : MachineEvent;

var machine = Machine.Create<ToggleContext>("toggle")
    .Context(_ => new ToggleContext(Count: 0))
    .Initial("inactive")
    .State("inactive", s => s
        .On<Toggle>(t => t.Target("active")))
    .State("active", s => s
        .On<Toggle>(t => t
            .Assign(a => a.Context with { Count = a.Context.Count + 1 })
            .Target("inactive")))
    .Build();

var (state, effects) = machine.GetInitialState();
var result = machine.Transition(state, new Toggle());

result.State.Matches("active"); // true
```

`Transition` is synchronous and has no threading constraints. XState.NET ships no runtime. You
decide how effects run: transactionally, async, distributed, replayed, or in tests against a
virtual clock.

## Effects

Every side effect is an `Effect` record with the same `Kind` discriminant as XState v6. Each one
projects onto a serializable `EffectDescriptor`: closures are dropped and actor references become
addresses.

| Effect | `Kind` | Meaning |
|---|---|---|
| `SpawnEffect` / `StartEffect` | `@xstate.spawn`, `@xstate.start` | Create a child; start it at the end of the macrostep. |
| `StopChildEffect` | `@xstate.stop` | Stop a child. |
| `RaiseEffect` / `SendToEffect` | `@xstate.raise`, `@xstate.sendTo` | Delayed self-raise; send to another actor, now or delayed. |
| `CancelEffect` | `@xstate.cancel` | Cancel a logical timer by id. |
| `TerminateEffect` | `@xstate.terminate` | Actor reached `Done` or `Error`; relay to the parent. |
| `DeadLetterEffect` | `@xstate.deadLetter` | Event rejected at the boundary, never delivered. |
| `EmitEffect` | `emit` | Emit an event to subscribers. |
| `ActionEffect` | `action` | Run an action, by `(type, params)` if it has a portable identity. |

Delays are logical timers. The machine records an id on its `Timers` ledger; your clock reports
that the id elapsed by sending an `xstate.timer` event. The machine ignores ids it no longer holds,
so a cancel racing a firing is not a bug.

## Durable execution

Pure transitions plus effects-as-data means the same machine runs unchanged on a durable host: a
workflow engine that owns persistence, retries, timers and messaging.

`DurableExecution<TContext>` adds the identity a host needs: a stable id per effect
(`"{transitionIndex}:{effectIndex}"`) and per wait (`"event:{transitionIndex}"`), so a crashed
worker's journal lines up with what a fresh worker recomputes.

```csharp
using XState.Durable;

var execution = new DurableExecution<OrderContext>(machine, host);

var (state, effects) = execution.InitialTransition("order-42");
await execution.ExecuteEffects(effects);

while (state.Status is SnapshotStatus.Active)
{
    (state, effects) = execution.Transition(state, await execution.WaitForEvent());
    await execution.ExecuteEffects(effects);
}
```

A host implements `IDurableHost`. Only `ExecuteAction` and `WaitForEvent` lack defaults; every
other operation throws by name rather than quietly running local behaviour.

Persistence is pure: `machine.Persist(state)` yields plain data, and `machine.Restore(persisted)`
returns the snapshot plus the children and timers the machine expects but cannot re-create.
Restore refuses a snapshot from a different machine or version.

- [`docs/durable-hosts.md`](docs/durable-hosts.md): host contract (identity, timers, effect ids, journaling, determinism, checkpoints)
- [`docs/persistence.md`](docs/persistence.md): persist/restore, JSON, versions and migrations
- [`samples/DurableHost`](samples/DurableHost): runnable host that checkpoints, crashes mid-workflow, and resumes in a second worker

## Statechart features

Aligned with SCXML and XState v6:

- Nested states, parallel regions, final states with output (`donedata`)
- Entry/exit/transition actions in SCXML document order
- Guards (including `In()` state predicates), eventless (`always`) transitions
- Delayed transitions (`After`) as cancellable logical timers
- History states (shallow/deep), internal vs external transitions
- Invoked/spawned child machines as spawn/stop effects, with `xstate.done.actor` / `xstate.error.actor` lifecycle events; incarnation tokens drop late completions from a restarted child

## JSON configs

XState JSON configs import directly, with named implementations supplied from C#:

```csharp
using XState.Json;

var machine = MachineConfig.FromJson(json, new MachineImplementations<JsonElement>()
    .Guard("isValid", args => /* ... */ true)
    .Action("notify", args => Console.WriteLine("notified"))
    .Delay("timeout", TimeSpan.FromSeconds(30)));
```

The reader follows the v6 `machine.schema.json`: `params`, `meta`, `description`, `version`,
`timeout`/`onTimeout`, `onError`, `onSnapshot`, state and transition `input`, root `actions`/`guards`
maps, built-in `@xstate.raise`/`cancel`/`log`/`emit`/`assign` actions, and `xstate.stateIn`/`xstate.not`
guards. Unknown keys throw with their JSON path (relax with `JsonConfigOptions.IgnoreUnknownKeys`).
Keys v6 defines but the port cannot honour (`route`, `matches`, `@expr`, non-empty `schemas`)
throw `NotSupportedException`. `MachineConfig.ToJson(machine)` returns the source config verbatim
for an imported machine and a best-effort config for a builder-built one.

## SCXML

```csharp
using XState.Scxml;

// Import: SCXML document -> runnable machine (ECMAScript datamodel via Jint)
var machine = ScxmlConverter.Parse(File.ReadAllText("traffic-light.scxml"));

// Export: machine definition -> SCXML document
string xml = ScxmlConverter.ToScxml(machine);
```

Export is strict by default. An action, guard or event descriptor that did not come from `Parse`
has no SCXML text, and writing it out would silently change behaviour (a dropped guard makes a
transition unconditional). Serialization throws `NotSupportedException` naming the state and part.
For a structurally faithful document for diagrams or diffing, emit placeholders:

```csharp
string xml = ScxmlConverter.ToScxmlString(
    machine,
    new ScxmlSerializationOptions { EmitPlaceholders = true });
```

Conformance is measured against the [W3C SCXML IRP tests](https://www.w3.org/Voice/2013/scxml-irp/),
vendored unmodified under `tests/XState.Scxml.Tests/w3c/`. Of 201 tests, 5 need manual log review
and are excluded. Of the 196 executed, 180 pass (91.8%) and 16 are known failing with recorded
reasons. `KnownFailing` is an exact list: an unexpected pass fails the suite. Every document runs
twice, as written and after a `Parse → ToScxml → Parse` round trip, and both must agree. See
[CONFORMANCE.md](CONFORMANCE.md).

## Layout

```text
src/XState/                 core: model, builder, transition algorithm, durable + persistence APIs
src/XState.Scxml/           SCXML parser/serializer, Jint datamodel
samples/DurableHost/        durable host: journal, timers, checkpoint, crash, resume
docs/                       durable-hosts.md, persistence.md
tests/XState.Tests/         core semantics tests (ported from xstate)
tests/XState.Scxml.Tests/   W3C conformance harness
tests/XState.TestKit/       deterministic interpreter (virtual clock) used by the tests
```

## Build and test

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned in `global.json`).

```bash
dotnet build XState.slnx
```

```bash
dotnet test XState.slnx
```

Subsets:

```bash
dotnet test tests/XState.Tests
```

```bash
dotnet test XState.slnx --filter "FullyQualifiedName~W3cConformanceTests"
```

Tests run against `XState.TestKit`'s `DeterministicInterpreter` (virtual clock), so the suite is
deterministic and finishes in seconds.

Crash-and-resume demo:

```bash
dotnet run --project samples/DurableHost
```

## License

MIT
