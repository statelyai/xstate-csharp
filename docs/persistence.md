# Persistence

`Persist` turns a snapshot into plain data. `Restore` turns it back.

```csharp
var persisted = machine.Persist(state);
var restored = machine.Restore(persisted);
```

Persist the result of `Persist`, never a `State<TContext>`. A live snapshot holds the machine that
produced it and CLR exceptions; `PersistedSnapshot` holds neither. It carries the state value, the
context, the child ledger, the timer ledger, history, the generated-id counters, and the identity
of the machine that wrote it — everything a different process needs to reconstruct the actor, and
nothing that only means something inside the process that produced it.

## Children

By default `Persist` embeds each child's own persisted snapshot, producing a whole-tree
checkpoint. The pure core never holds a child's state, so it has to ask for it:

```csharp
var persisted = machine.Persist(
    initial.State,
    new PersistOptions { ChildSnapshot = _ => Persist(worker) });
```

A live child with no `ChildSnapshot` to supply it throws — silently dropping a child is worse than
refusing to write the snapshot.

`EmbedChildren = false` records children by **address** instead. Each child's state stays with
whatever runtime owns it, and restoring produces a handle to re-attach to rather than logic to
re-create:

```csharp
var byAddress = machine.Persist(state, new PersistOptions { EmbedChildren = false });
var child = Assert.Single(byAddress.Children);
Assert.True(child.Remote);
Assert.Null(child.Snapshot);
```

Either way, a child is only persistable if its logic has a **registered source key** — `Actor(...)`
on the builder, named by `Invoke("worker", …)` or `SpawnAction(logic, Src: "worker")`. Inline logic
has nothing to name it by, so a snapshot holding one cannot be restored; `AllowInlineActors` writes
it anyway, and the resulting snapshot fails at restore. That is the point of the option: it exists
for inspection, not recovery.

## Restore

`Restore` returns the rebuilt snapshot plus the two things the machine believes exist but cannot
re-create itself:

```csharp
public sealed record RestoreResult<TContext>(
    State<TContext> State,
    IReadOnlyList<ChildToRestore> Children,
    IReadOnlyList<LogicalTimer> Timers);
```

Bring them back before you resume. Each `ChildToRestore` carries its id, address, incarnation and
either the `Logic` to re-create (resolved from the machine's registered sources) or `Remote = true`
for one to re-attach to. Each `LogicalTimer` carries the id to re-arm and its declared delay:

```csharp
foreach (var timer in restored.Timers)
{
    await resumedHost.ScheduleTimer(resumed.RootAddress, timer.Id, timer.Delay);
}
```

Restoring folds the counters forward, so a generated id a restored snapshot already used is never
handed out again:

```csharp
var restored = machine.Restore(persisted);
Assert.Equal(4, restored.State.NextActorIds["worker"]);
```

## JSON

`SnapshotJson.DefaultOptions()` gives `System.Text.Json` options that round-trip a
`PersistedSnapshot`. Two things in a snapshot are not plain JSON on their own, and this is what
handles them.

`MachineEvent` is a polymorphic hierarchy, so an event is written with a `$event` discriminator
carrying its CLR type name alongside the `type` the machine matches on. Pass your own event types
to have them read back as themselves; an unregistered one comes back as a `NamedEvent` keeping its
`type` and its payload's raw JSON, so an event this process has no type for is preserved rather
than lost:

```csharp
var unknown = Assert.IsType<NamedEvent>(JsonSerializer.Deserialize<MachineEvent>(json, options));
Assert.Equal("Ping", unknown.Type);

var known = JsonSerializer.Deserialize<MachineEvent>(json, SnapshotJson.DefaultOptions(typeof(Ping)));
Assert.Equal(new Ping(), known);
```

The loosely typed slots (`Value`, `Context`, `Output`, event payloads) are `object`, which
`System.Text.Json` would otherwise hand back as `JsonElement`. Under these options they come back
as plain strings, numbers, lists and dictionaries, which `Restore` reads directly.

The context still needs a converter: no serializer can guess `TContext` from a snapshot alone.

```csharp
var restored = machine.Restore(
    wire,
    new RestoreOptions<Unit> { ContextConverter = _ => Unit.Value });
```

Omit `ContextConverter` and the value is cast directly, which is correct only for an in-process
persist/restore.

## Versions and migrations

Declare a version on a machine that is going to outlive a deploy:

```csharp
Machine.Create<OrderContext>("order").Version("1.0.0")
```

`Persist` writes it. `Restore` refuses a snapshot whose version disagrees, unless you say how to
bring it forward:

```csharp
var restored = current.Restore(persisted, new RestoreOptions<Unit>
{
    Migrate = (snapshot, from) => snapshot with
    {
        Machine = new PersistedMachineIdentity("job", "2.0.0"),
        Version = "2.0.0"
    }
});
```

`Migrate` is the one-machine case. When storage holds snapshots from several past versions,
register them with `MachineVersions`, which turns "which machine wrote this?" into an answerable
question:

```csharp
var versions = MachineVersions.Create(V1, V2);
var parsed = versions.ParseSnapshot(SnapshotOf(V2));

Assert.Equal(new PersistedMachineIdentity("cart", "2.0.0"), parsed.Identity);
Assert.Same(V2, parsed.Machine);
```

Identity comes from the nested `Machine` first, then the legacy top-level `Version`, then the
`unversioned` fallback configured at registration — the version that was live before versioning
existed. Every registered machine must declare a version, share one id, and be registered once.

`MigrateSnapshot` brings a snapshot to a target version. An exact migration handles its own source
version; `MachineVersions.Wildcard` (`"*"`) handles everything else, including a snapshot whose
identity could not be resolved at all — which is the case a wildcard exists for. The result is
stamped with the target identity, so it restores into the target machine:

```csharp
var migrated = versions.MigrateSnapshot(SnapshotOf(V1), "2.0.0", new Dictionary<string, Func<PersistedSnapshot, PersistedSnapshot>>
{
    ["1.0.0"] = snapshot => snapshot with { Value = "checkout" }
});

Assert.True(V2.Restore(migrated).State.Matches("checkout"));
```

`AdaptEvents` does the same for a recorded event history — same dispatch, same wildcard rule.
Adapters receive and return whole arrays, so they may insert, drop, combine or reorder events. It
adapts a materialized history only: it neither stores nor replays events, and produces no
snapshot.

The target version must be backed by an actual machine. Only a machine can interpret restored
state.

## What throws, and why

Restoring is a *pure* API, and it refuses rather than guesses. Unlike v6 — which swallows restore
errors and reports a failed actor — every one of these surfaces as an
`InvalidOperationException` at the call site, where the host can decide what to do with a snapshot
it cannot use. One exception is different: without a `ContextConverter`, `Restore` casts
`snapshot.Context` straight to `TContext`, and a context that came through a serializer (a
`JsonElement`, a dictionary) fails that cast with an `InvalidCastException`. Pass a
`ContextConverter` for any snapshot that was not persisted in-process.

| Message | Cause |
|---|---|
| `An inline child actor cannot be persisted.` | A live child whose logic has no registered source key. |
| `Child 'w' has no persisted snapshot.` | Embedding was on and `ChildSnapshot` returned nothing. |
| `Machine ID mismatch: …` | The snapshot names a different machine. |
| `Persisted snapshot version 'x' does not match machine version 'y' …` | No `Migrate` was supplied. |
| `Persisted snapshot version 'x' conflicts with machine version 'y'.` | The nested and legacy versions on one snapshot disagree. |
| `Persisted snapshot references state 's' which does not exist …` | The state value names a state this machine no longer has. |
| `Unable to restore child actor 'w': child source '…' is not provided …` | The source key is not registered on this machine. |
| `Unable to restore timer 't': target actor 'ghost' is unavailable.` | A delayed send aimed at a child the snapshot no longer holds. |

The last three are the shape of a machine change that a migration should have handled: a removed
state, a renamed actor source, a child that no longer exists.

An errored snapshot restores its failure as a `PersistedErrorException` carrying the original
exception's type name and message. A stack trace does not cross a process boundary, and neither
does a custom exception type that the restoring process may not have.

## What next?

- [Durable hosts](durable-hosts.md) — the effect contract a checkpointing host honours.
- [`samples/DurableHost`](../samples/DurableHost) — checkpoint, crash, restore, resume.
