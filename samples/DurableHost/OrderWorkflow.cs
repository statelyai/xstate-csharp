using XState;

namespace XState.Samples.DurableHost;

/// <summary>The order the workflow is processing. Plain data: it is persisted as JSON.</summary>
public sealed record OrderContext(string OrderId, string? PaymentRef = null);

/// <summary>The payment child's context.</summary>
public sealed record PaymentContext(string OrderId);

/// <summary>Emitted to the actor's subscribers when the order ships.</summary>
public sealed record OrderShipped(string OrderId, string PaymentRef) : MachineEvent;

/// <summary>
/// The workflow. Nothing here knows about the host: no clock, no I/O, no threading — a machine is
/// a value, and running it is somebody else's job.
/// </summary>
public static class OrderWorkflow
{
    /// <summary>Short enough to watch, long enough to outlive the crash.</summary>
    public static readonly TimeSpan PackingTime = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The invoked child. It completes on its initial snapshot, so the host relays its
    /// <c>xstate.done.actor</c> straight back; a real one would be its own durable execution.
    /// </summary>
    public static StateMachine<PaymentContext> Payment() =>
        Machine.Create<PaymentContext>("payment")
            .Context(input => new PaymentContext((string)input!))
            .Initial("charged")
            .Final("charged", s => s.Output(a => $"pay_{a.Context.OrderId}"))
            .Build();

    /// <summary>
    /// Charge, pack, ship. Three things a durable host has to carry across a restart: an invoked
    /// child (registered under a source key, so the snapshot can name it), a delayed transition
    /// (a logical timer on the snapshot's ledger), and an emitted event.
    /// </summary>
    public static StateMachine<OrderContext> Order() =>
        Machine.Create<OrderContext>("order")
            // Persisted with every snapshot; restoring into a different version is refused.
            .Version("1.0.0")
            // Registering the logic under a key is what makes a snapshot holding this child
            // persistable — inline logic has nothing to name it by.
            .Actor("payment", Payment())
            .Context(input => new OrderContext((string)input!))
            .Initial("charging")
            .State("charging", s => s.Invoke(
                "payment",
                id: "pay",
                input: a => a.Context.OrderId,
                onDone: t => t
                    .Assign(a => a.Context with { PaymentRef = a.Event.Output as string })
                    .Target("packing")))
            // A delayed transition is a logical timer: the machine records the id, the host owns
            // the clock, and `xstate.timer` is the only thing that comes back.
            .State("packing", s => s.After(PackingTime, t => t.Target("shipped")))
            .Final("shipped", s => s
                .EntryEmit(a => new OrderShipped(a.Context.OrderId, a.Context.PaymentRef!))
                .Output(a => a.Context.PaymentRef))
            .Build();
}
