# XState for .NET

State machines and statecharts for .NET — a port of [XState](https://github.com/statelyai/xstate) (v6 semantics) by the author of XState.

- **`XState`** — zero-dependency, pure interpreter core: `(machine, state, event) => (nextState, effects)`
- **`XState.Scxml`** — SCXML import/export + ECMAScript datamodel (via Jint), tested against the W3C SCXML conformance suite

> ⚠️ Alpha. API surface is settling.

## Install

```bash
dotnet add package XState
```

```bash
dotnet add package XState.Scxml
```

Both packages target **.NET 8 (LTS)** and **.NET 10**.

## The pure core

The heart of the library is a pure function — no side effects, fully inspectable:

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

`Transition` is synchronous and pure, with no threading constraints. XState.NET ships **no
runtime**: you decide how the effects are executed — transactionally, async, distributed, replayed,
or in tests against a virtual clock.

## Effects are data

Every side effect a transition wants is an `Effect` record carrying the same `Kind` discriminant as
XState v6, and every one projects onto a serializable `EffectDescriptor` in which live logic and
closures are dropped and actor references become addresses:

| Effect | `Kind` | Meaning |
|---|---|---|
| `SpawnEffect` / `StartEffect` | `@xstate.spawn`, `@xstate.start` | Create a child; start it at the end of the macrostep. |
| `StopChildEffect` | `@xstate.stop` | Stop a child. |
| `RaiseEffect` / `SendToEffect` | `@xstate.raise`, `@xstate.sendTo` | A delayed self-raise; a send to another actor, now or delayed. |
| `CancelEffect` | `@xstate.cancel` | Cancel a logical timer by id. |
| `TerminateEffect` | `@xstate.terminate` | The actor reached `Done` or `Error`; relay it to the parent. |
| `DeadLetterEffect` | `@xstate.deadLetter` | An event was rejected at the boundary and never delivered. |
| `EmitEffect` | `emit` | Emit an event to subscribers. |
| `ActionEffect` | `action` | Run an action — by `(type, params)` if it has a portable identity. |

Time works the same way. A delay is a **logical timer**: the machine records an id on its own
`Timers` ledger, and your clock only reports that an id elapsed, by sending the actor an
`xstate.timer` event. The machine decides what firing means, and ignores an id it no longer holds —
so a cancel that races a firing is ordinary, not a bug.

## Durable execution

Because transitions are pure and effects are data, the same machine runs unchanged on a durable
host — a workflow engine that owns persistence, retries, timers and messaging.

`DurableExecution<TContext>` adds the identity such a host needs: a stable id per effect
(`"{transitionIndex}:{effectIndex}"`) and per wait (`"event:{transitionIndex}"`), so the journal a
crashed worker left behind lines up with what a fresh worker recomputes.

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

A host implements `IDurableHost`. Only `ExecuteAction` and `WaitForEvent` have no default; every
other operation throws by name rather than quietly running local behaviour.

Snapshots are persisted and restored through a pure API: `machine.Persist(state)` yields plain
data, and `machine.Restore(persisted)` hands back the snapshot plus the children and timers the
machine believes exist but cannot re-create itself. Restoring refuses a snapshot from a different
machine or version rather than guessing.

- [`docs/durable-hosts.md`](docs/durable-hosts.md) — the host contract: identity, timers, effect ids, journaling, determinism, checkpoints.
- [`docs/persistence.md`](docs/persistence.md) — persist/restore, JSON, versions and migrations.
- [`samples/DurableHost`](samples/DurableHost) — a runnable host that checkpoints, crashes mid-workflow, and resumes in a second worker.

## Statechart features

Full statechart semantics, aligned with SCXML and XState v6:

- Hierarchical (nested) states, parallel regions, final states with output (`donedata`)
- Entry/exit/transition actions in SCXML document order
- Guards (incl. `In()` state predicates), eventless (`always`) transitions
- Delayed transitions (`After`) as cancellable logical timers — your clock, your scheduler
- History states (shallow/deep), internal vs external transitions
- Invoked/spawned child machines as spawn/stop effects, with `xstate.done.actor` / `xstate.error.actor` lifecycle events, and incarnation tokens so a late completion from a restarted child is dropped

## JSON machine configs

Machines defined as XState JSON configs import directly, with named implementations
supplied from C#:

```csharp
using XState.Json;

var machine = MachineConfig.FromJson(json, new MachineImplementations<JsonElement>()
    .Guard("isValid", args => /* ... */ true)
    .Action("notify", args => Console.WriteLine("notified"))
    .Delay("timeout", TimeSpan.FromSeconds(30)));
```

The reader follows the v6 `machine.schema.json`: `params`, `meta`, `description`, `version`,
`timeout`/`onTimeout`, `onError`, `onSnapshot`, state and transition `input`, root `actions`/`guards`
definition maps, and the built-in `@xstate.raise`/`cancel`/`log`/`emit`/`assign` actions and
`xstate.stateIn`/`xstate.not` guards. An unknown key throws with its JSON path (relax with
`JsonConfigOptions.IgnoreUnknownKeys`); a key v6 defines but the port cannot honour (`route`,
`matches`, `@expr`, non-empty `schemas`) throws `NotSupportedException` rather than being
dropped. `MachineConfig.ToJson(machine)` returns the source config verbatim for an imported
machine and a best-effort config for a builder-built one.

## SCXML

```csharp
using XState.Scxml;

// Import: SCXML document -> runnable machine (ECMAScript datamodel via Jint)
var machine = ScxmlConverter.Parse(File.ReadAllText("traffic-light.scxml"));

// Export: machine definition -> SCXML document
string xml = ScxmlConverter.ToScxml(machine);
```

Export is strict by default. A machine holding an action, guard or event descriptor that did not
come from `Parse` has no SCXML text, and writing it out anyway would silently change behaviour — a
dropped guard turns a conditional transition into an unconditional one. So serialization throws
`NotSupportedException` naming the state and the part. To get a structurally faithful document for
diagrams or diffing, ask for placeholders instead:

```csharp
string xml = ScxmlConverter.ToScxmlString(
    machine,
    new ScxmlSerializationOptions { EmitPlaceholders = true });
```

Conformance is measured against the [W3C SCXML Implementation Report tests](https://www.w3.org/Voice/2013/scxml-irp/),
vendored unmodified under `tests/XState.Scxml.Tests/w3c/`. Of **201** IRP tests, 5 need a human to
read a log and are excluded; of the **196** executed, **180 pass** (91.8%) and 16 are known
failing, each with a recorded reason. `KnownFailing` is an exact list, not a floor: an unexpected
pass fails the suite. Every document is run twice — as written, and after a
`Parse → ToScxml → Parse` round trip, which must reach the same verdict. See
[CONFORMANCE.md](CONFORMANCE.md) for the breakdown.

## Layout

```text
src/XState/            core: model, builder, pure transition algorithm, durable + persistence APIs
src/XState.Scxml/      SCXML parser/serializer, Jint datamodel
samples/DurableHost/   a durable host: journal, timers, checkpoint, crash, resume
docs/                  durable-hosts.md, persistence.md
tests/XState.Tests/            core semantics tests (ported from xstate)
tests/XState.Scxml.Tests/      W3C conformance harness
tests/XState.TestKit/          deterministic interpreter (virtual clock) used by the tests
```

## Building and testing locally

The libraries target `net8.0` and `net10.0`; the test projects and the sample are `net10.0`-only,
so building the solution requires the [.NET 10 SDK](https://dotnet.microsoft.com/download)
(pinned in `global.json`). Then, from the repo root:

```bash
dotnet build XState.slnx
```

```bash
dotnet test XState.slnx
```

That runs both test projects: the core semantics tests and the W3C SCXML conformance
suite (expected pass/known-failing counts are tracked in [CONFORMANCE.md](CONFORMANCE.md)).

To run a subset:

```bash
dotnet test tests/XState.Tests
```

```bash
dotnet test XState.slnx --filter "FullyQualifiedName~W3cConformanceTests"
```

Tests execute effects against `XState.TestKit`'s `DeterministicInterpreter` — a virtual-clock
execution layer — so the whole suite is deterministic and runs in a few seconds with no
real timers.

To watch a workflow survive a crash:

```bash
dotnet run --project samples/DurableHost
```

## License

MIT
