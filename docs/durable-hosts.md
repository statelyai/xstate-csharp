# Durable hosts

XState.NET ships no runtime. A transition is a pure function:

```text
(machine, state, event) => (nextState, effects)
```

The effects are data. Executing them — persisting, retrying, timing, messaging, running
children — is the host's job. This page is the contract a host has to honour; it is the .NET
counterpart of XState v6's durable-execution documentation, and names the v6 operation each
member corresponds to.

`DurableExecution<TContext>` (in `XState.Durable`) is the small piece between the two. It adds
nothing to the semantics; it adds *identity*: a stable id per effect and per wait, so the journal
a crashed worker left behind lines up with what a fresh worker recomputes.

A complete, runnable host is in [`samples/DurableHost`](../samples/DurableHost). Every snippet
below is from it or from `tests/XState.Tests`.

## Write a host

A host implements `IDurableHost`. Only two members have no default:

- `ExecuteAction` — run one action. This is where the host's step or activity model plugs in.
- `WaitForEvent` — wait durably for the next event addressed to the execution's root. A durable
  loop is defined by being able to park here and come back in another process.

Every other operation throws `NotSupportedException` naming itself. That is deliberate: on a
durable host, an operation you did not implement must fail loudly rather than quietly running
local behaviour.

```csharp
public sealed class JournalingHost : IDurableHost
{
    public ValueTask ScheduleTimer(DurableEffect effect, string id, TimeSpan delay)
    {
        // Timer ids are per actor: key the alarm by the owning actor's address as well, and
        // deliver the firing to that actor's mailbox.
        var cts = new CancellationTokenSource();
        _timers[(effect.Source, id)] = cts;

        _ = Task.Delay(delay, cts.Token).ContinueWith(
            task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    MailboxOf(effect.Source).Writer.TryWrite(new TimerEvent(id));
                }
            },
            TaskScheduler.Default);

        return default;
    }

    public ValueTask ExecuteAction(DurableEffect effect, ActionEffect action)
    {
        if (_done.Contains(effect.Id))
        {
            return default; // already journaled under this id
        }

        action.Exec();
        _done.Add(effect.Id); // record completion only after the action ran; persist it with the journal
        return default;
    }

    public async ValueTask<MachineEvent> WaitForEvent(DurableWait wait, CancellationToken ct) =>
        await _mailbox.Reader.ReadAsync(ct);
}
```

`DurableHostBase` supplies one implementation — `ExecuteAction` runs the action's local closure —
and leaves the rest throwing. It suits tests and hosts whose actions are genuinely local.

### Operations

| `IDurableHost` | xstate v6 | When it runs |
|---|---|---|
| `SpawnActor` | `spawnActor` | A child is created (not yet started). |
| `StartActor` | `startActor` | A child spawned earlier in the same macrostep is started. |
| `StopActor` | `stopActor` | A child is stopped. |
| `Terminate` | `terminateActor` | The actor reached `Done` or `Error`; relay the done/error event to the parent. |
| `SendEvent` | `sendEvent` | Deliver an event to another actor now. |
| `ScheduleTimer` | `scheduleTimer` | Arm a logical timer on an actor. |
| `CancelTimer` | `cancelTimer` | Cancel one. |
| `DeadLetter` | `deadLetter` | An event was rejected at the actor boundary and never delivered. |
| `EmitEvent` | `emitEvent` | Emit an event to the actor's subscribers. |
| `ExecuteAction` | `executeAction` | Run one action. Required. |
| `WaitForEvent` | `waitForEvent` | Park until the next root event. Required. |

Three v6 operations have no counterpart here. `cancelAllTimers` is unnecessary: stopping an actor
emits one `CancelEffect` per outstanding timer, so a host only ever needs `CancelTimer`.
`enqueueRootEvent` is replaced by `DurableExecution<TContext>.ReportRootEvent`, called on the
execution rather than configured on the host. `runLogic` and `runStep` have no counterpart because
this port has no live actor tree and no step vocabulary: a child is `IActorLogic`, and running it
is the host's business.

## Identity

Every actor has a deterministic **address**: the `/`-joined path of actor ids from the root
(`order`, `order/pay`). `DurableExecution<TContext>.RootAddress` reports the root's before any
transition runs, so a host can label mailboxes without a snapshot. Generated child ids are
per-source counters carried on the snapshot (`order/worker:0`), so a replay reproduces them
exactly. An id containing `/` is percent-encoded in the address, and `%` is escaped first, so the
encoding stays injective.

**The address, never a session id, is an actor's durable identity.** It survives persist/restore;
a per-process handle does not.

An **incarnation** identifies one instance of an address. Every spawn mints a token from a
snapshot counter and records it on `ChildRecord.Incarnation`; the host echoes it as the
`SessionId` of the done/error event it relays. A completion carrying an earlier incarnation's
token is dropped, so a restarted child of the same id cannot be confused with its predecessor:

```csharp
// The stopped child's completion arrives late. It names a live id, but not this child.
var stale = machine.Transition(restarted.State, new DoneActorEvent("worker", null, firstIncarnation));
Assert.Same(restarted.State, stale.State);
Assert.Empty(stale.Effects);
```

A host that does not track incarnations passes `null`, and the event is accepted as-is.

## Timers

A delay is never a `Task.Delay` inside the machine. It is a **logical timer**: an entry on
`State<TContext>.Timers` with a deterministic id, a declared delay, a kind (`Raise` or `SendTo`)
and an event. The host owns only the clock.

`ScheduleTimer(effect, id, delay)` is the whole scheduling contract; `effect.Source` is the owning actor's address. When the delay
elapses, the host sends the owning actor `new TimerEvent(id)` — that is all it says. The machine
looks the id up on its own ledger and decides what firing means:

- A live id releases the timer's event and removes the entry.
- An id no longer on the ledger is a stale firing. It produces no effects and no state change.
  Cancellation always races firing, so this case is normal, not exceptional.

A delayed send is a timer on the **sender**, not the target: the sender owns the ledger entry and
is the only actor that can cancel it. When the timer fires, the delayed send becomes an immediate
`SendToEffect`.

`ScheduleTimer` and `CancelTimer` receive the originating `DurableEffect` (the delayed
`RaiseEffect`/`SendToEffect`, or the `CancelEffect`) so a journal can key them by effect id like
every other operation. Key the host-side alarm itself by `(effect.Source, id)`, which is stable
across replays.

The snapshot deliberately records no deadline or remaining time. Persist the deadline yourself when
you accept `ScheduleTimer`, and hand it back on restore:
`RearmTimers(restored.Timers, remainingDelay: t => dueAt[t.Id] - now)` re-arms each timer with what
is left (clamped at zero). Without a `remainingDelay`, a timer restarts from its full declared
delay on every restore, so a worker that crashes repeatedly would postpone it indefinitely.

## The effect contract

`InitialTransition` and `Transition` stay pure. `DurableExecution<TContext>` tags their ordered
effects with ids, and waits with ids of their own:

```csharp
var (state, first) = execution.InitialTransition();
Assert.Equal(["0:0"], first.Select(e => e.Id));

var (_, second) = execution.Transition(state, new Ping());
Assert.Equal(["1:0", "1:1"], second.Select(e => e.Id));
```

An effect id is `{transitionIndex}:{effectIndex}`; a wait id is `event:{transitionIndex}`. The
position of an effect *is* its identity, because a pure transition produces the same effects in
the same order every time it is replayed from the same snapshot and event. The transition index
advances once per transition whether or not the transition produced any effects, so a replay that
sees the same events recomputes the same ids.

Each `DurableEffect` also carries a `Descriptor` — an `EffectDescriptor`, the serializable view of
the effect. Live logic and closures are dropped, and actor references become addresses:

```csharp
var spawn = EffectDescriptor.Of(new SpawnEffect("kid", Child(), 7, "kidSrc", "0"), "root/parent");
Assert.Equal("root/parent/kid", spawn.Actor);
Assert.Equal("root/parent", spawn.Source);
Assert.Equal("kidSrc", spawn.Src);
```

Journal the descriptor, not the effect. Payload fields (`Event`, `Input`, `Params`, `Output`) pass
through by reference and are only as serializable as their values.

`ExecuteEffects` hands the batch to the host one effect at a time, in initiation order. Calls must
not overlap; starting a second batch before the first settles throws. A failure aborts the batch
and propagates — the effects came from a pure transition, so a retrying host recomputes the
identical batch and re-executes it from the top, skipping whatever it has already journaled.
Delivery is at-most-once: an undeliverable event is dropped and reported through `DeadLetter`, not
retried.

Events addressed to the execution's own root do not go through `SendEvent`. The host reports them
with `ReportRootEvent`, and `WaitForEvent` hands them out — in order — before it parks:

```csharp
if (snapshot.Status is SnapshotStatus.Done)
{
    Execution!.ReportRootEvent(new DoneActorEvent(start.Id, snapshot.Output, child.Incarnation));
}
```

That keeps the drive loop uniform: `Transition(state, await WaitForEvent())`, whether the next
event came from a child that just finished or from a wait that outlived the process.

### Journaling rules

- **Key by effect id, and skip what you have already done.** That is what makes a replay
  cheap and correct.
- **Journal only work that must not run twice.** A journaled closure is skipped on replay. Local
  bookkeeping — rebuilding the child table, re-arming timers — must run on every replay, or the
  worker rebuilds a different world than the one it recorded, silently.
- **Do not nest journaled operations.** On engines whose journaled step cannot start another
  journaled operation, journal `action.Exec()` only when the action body is a plain external side
  effect.

## Determinism

Replay only reconstructs the same effects when every transition is a pure function of the snapshot
and the event. What runs *inside* `Transition` is: guards, `Assign` assigners, `Input` and
`Output` resolvers, delay resolvers, `Choice` and `ScriptAction`. None of them may read the clock,
generate random values, mutate external state or perform I/O. `DateTime.UtcNow`, `Random.Shared`
and `Guid.NewGuid()` each produce a different snapshot — and so a different effect sequence — on
replay, and nothing detects the divergence.

```csharp
// Wrong: a different context on every replay fold.
.On<Approve>(t => t.Assign(a => a.Context with { ApprovedAt = DateTime.UtcNow }))

// Right: the value travels on the event the host already journaled.
.On<Approve>(t => t.Assign(a => a.Context with { ApprovedAt = a.Event.At }))
```

Entry and exit action bodies are *not* on that list, and this is a deliberate deviation from v6:
here they are always deferred as `ActionEffect`s and run in `ExecuteAction`, under an effect id
the host can journal. A side effect in an entry action is fine; one in a guard or an assigner is
a bug.

Derive idempotency keys from what the snapshot already carries — addresses and effect ids are
stable across replays and make good seeds.

## Checkpoints

Checkpoint *after* `ExecuteEffects` resolves: "effects executed" is what makes a snapshot safe to
resume from. Persist `NextTransitionIndex` alongside the snapshot, and pass it back as
`DurableExecutionOptions.TransitionIndex` when the next worker recreates the execution, so effect
ids continue where the journal left off instead of colliding with it.

```csharp
var snapshot = machine.Persist(state, new PersistOptions { EmbedChildren = false });
File.WriteAllText(
    path,
    JsonSerializer.Serialize(new Checkpoint(execution.NextTransitionIndex, snapshot), Json));
```

Persist `MachineId` and `MachineVersion` too, and reject a worker whose machine disagrees: a
changed machine reorders effect ids, and memoized results silently misalign. `Restore` enforces
this for you — see [Persistence](persistence.md).

`Run` is convenience over the explicit loop and only starts *fresh* executions; a nonzero
`TransitionIndex`, or an execution that has already transitioned, throws
`DurableExecutionResumeException`. A resuming worker writes the loop out:

```csharp
await execution.ExecuteEffects(effects);
Save(machine, execution, state, path);

while (state.Status is SnapshotStatus.Active)
{
    var evt = await execution.WaitForEvent();

    (state, effects) = execution.Transition(state, evt);
    await execution.ExecuteEffects(effects);
    Save(machine, execution, state, path);
}
```

`Run` resolves with the machine's output when it completes, throws the machine's error when it
fails, and throws `DurableExecutionCancelledException` when it was stopped — stopping is not an
outcome, so there is nothing to return.

## Rejected events

A machine may declare a boundary check with `ValidateEvents`. An event it rejects is never
delivered: `Transition` returns the snapshot unchanged with a single `DeadLetterEffect`, and never
throws.

```csharp
var rejected = machine.Transition(initial, new Go());

Assert.Same(initial, rejected.State);
var dead = Assert.IsType<DeadLetterEffect>(Assert.Single(rejected.Effects));
Assert.Equal("invalidEvent", dead.Reason);
```

`Reason` is `"stopped"` (the actor is no longer running), `"invalidEvent"` (the validator refused
it) or `"internalEvent"`. Replay stays total: a poisoned event replays to the same unchanged
snapshot and the same rejection effect every time.

The validator never sees the host's own protocol events. Rejecting `xstate.timer` would strand a
timer on the ledger forever.

## Driving effects yourself

`DurableExecution<TContext>` is sugar. `StateMachine<TContext>.Transition` and `GetInitialState`
are the whole pure API, and `EffectDescriptor.Of(effect, sourceAddress)` is the whole projection
onto journalable data. A host that has its own identity scheme, its own ordering guarantees or its
own batch semantics can take `(state, effects)` and execute them entirely its own way:

```csharp
var (state, effects) = machine.Transition(previous, evt);

foreach (var effect in effects)
{
    var descriptor = EffectDescriptor.Of(effect, "order");
    // journal `descriptor`, dispatch `effect` however the host prefers
}
```

Dropping `ExecuteEffects` means supplying its three services yourself: ordered one-at-a-time
handoff, root-event capture and replay into `Transition`, and batch-failure discard for retries.

## What next?

- [Persistence](persistence.md) — persist/restore, JSON, versions and migrations.
- [`samples/DurableHost`](../samples/DurableHost) — a host you can run.
