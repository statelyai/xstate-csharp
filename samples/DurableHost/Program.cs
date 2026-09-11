using System.Text.Json;
using XState;
using XState.Durable;
using XState.Persistence;

namespace XState.Samples.DurableHost;

/// <summary>
/// What the host writes after every batch of effects: the snapshot, and where the journal's
/// numbering had got to. Both are needed — the snapshot says where the machine is, the transition
/// index says which effect ids the next worker may hand out.
/// </summary>
public sealed record Checkpoint(int NextTransitionIndex, PersistedSnapshot Snapshot);

public static class Program
{
    private static readonly JsonSerializerOptions Json =
        new(SnapshotJson.DefaultOptions()) { WriteIndented = true };

    public static async Task Main()
    {
        var machine = OrderWorkflow.Order();
        var path = Path.Combine(Path.GetTempPath(), "xstate-durable-host-sample.json");
        File.Delete(path);

        Console.WriteLine($"machine  {machine.Id} v{machine.Version}");
        Console.WriteLine($"file     {path}");

        // --- Worker 1 -------------------------------------------------------------------------
        // Charges the order, enters `packing`, arms the timer — and dies the moment the loop parks.

        Console.WriteLine("\nworker 1");
        var host = new JournalingHost { CrashOnPark = true };
        var execution = new DurableExecution<OrderContext>(machine, host);

        var (state, effects) = execution.InitialTransition("order-42");

        try
        {
            await Drive(machine, execution, state, effects, path);
        }
        catch (WorkerCrashedException crash)
        {
            Console.WriteLine($"    ✗ {crash.Message}");
        }

        // --- Worker 2 -------------------------------------------------------------------------
        // A different worker, a different host, the same journal. It reads the checkpoint, restores
        // the snapshot, re-arms what the machine says is outstanding, and carries on.

        Console.WriteLine("\nworker 2");
        var checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path), Json)!;

        var restored = machine.Restore(
            checkpoint.Snapshot,
            new RestoreOptions<OrderContext> { ContextConverter = ReadContext });

        var resumedHost = new JournalingHost();
        var resumed = new DurableExecution<OrderContext>(
            machine,
            resumedHost,
            // Effect ids continue where the journal left off instead of colliding with it.
            new DurableExecutionOptions { TransitionIndex = checkpoint.NextTransitionIndex });

        Console.WriteLine($"    restored at {restored.State.Value}, context {restored.State.Context}");

        // `Restore` hands back the two things the machine believes exist but cannot re-create
        // itself: its children, and its timers. This checkpoint has no live child (the payment
        // actor completed before the crash) and one timer to re-arm.
        resumedHost.AttachRestoredChildren(restored.Children);
        await resumed.RearmTimers(restored.Timers);

        var final = await Drive(machine, resumed, restored.State, [], path);

        Console.WriteLine($"\ndone     status={final.Status} output={final.Output}");
        File.Delete(path);
    }

    /// <summary>
    /// The durable loop, spelled out: execute this transition's effects, checkpoint, then wait for
    /// the next event and do it again. <c>Run</c> is the same loop from the initial transition; a
    /// worker resuming a checkpoint has to write it out, because it does not start at the
    /// beginning.
    /// </summary>
    private static async Task<State<OrderContext>> Drive(
        StateMachine<OrderContext> machine,
        DurableExecution<OrderContext> execution,
        State<OrderContext> state,
        IReadOnlyList<DurableEffect> effects,
        string path)
    {
        // Checkpoint *after* the effects: "effects executed" is what makes a snapshot safe to
        // resume from.
        await execution.ExecuteEffects(effects);
        Save(machine, execution, state, path);

        while (state.Status is SnapshotStatus.Active)
        {
            // Root events an earlier batch reported (here: the payment child completing) come out
            // of here first. Only when none are queued does the loop park on the host.
            var evt = await execution.WaitForEvent();

            (state, effects) = execution.Transition(state, evt);
            await execution.ExecuteEffects(effects);
            Save(machine, execution, state, path);
        }

        return state;
    }

    /// <summary>Writes the checkpoint. Plain JSON — no delegates, no live references.</summary>
    private static void Save(
        StateMachine<OrderContext> machine,
        DurableExecution<OrderContext> execution,
        State<OrderContext> state,
        string path)
    {
        // Children by address, not embedded: the pure core never holds a child's state, so
        // embedding needs the host to supply each child's snapshot through
        // `PersistOptions.ChildSnapshot`. This host runs its child in-process and has nothing
        // durable to hand over.
        var snapshot = machine.Persist(state, new PersistOptions { EmbedChildren = false });

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new Checkpoint(execution.NextTransitionIndex, snapshot), Json));
    }

    /// <summary>
    /// Turns the persisted context back into <c>OrderContext</c>. No serializer can guess
    /// <c>TContext</c> from a snapshot alone; under <see cref="SnapshotJson"/>'s options the
    /// loosely typed slots come back as plain dictionaries.
    /// </summary>
    private static OrderContext ReadContext(object? raw)
    {
        var map = (IReadOnlyDictionary<string, object?>)raw!;
        return new OrderContext((string)map["OrderId"]!, map["PaymentRef"] as string);
    }
}
