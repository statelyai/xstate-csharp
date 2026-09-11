namespace XState;

/// <summary>
/// Side-effect intents produced by a pure transition, executed in order by the host runtime
/// (or inspected/discarded by callers of the pure API).
/// <para>
/// Every effect is <em>data</em>: <see cref="Kind"/> is the same discriminant string xstate v6
/// uses (<c>effectDescriptor.ts</c>), and <see cref="EffectDescriptor.Of"/> projects an effect
/// onto a fully serializable descriptor a durable host can journal.
/// </para>
/// </summary>
public abstract record Effect
{
    /// <summary>The v6 effect discriminant (<c>@xstate.spawn</c>, <c>emit</c>, <c>action</c>, …).</summary>
    public abstract string Kind { get; }
}

/// <summary>Well-known <see cref="SendToEffect.Target"/> aliases.</summary>
public static class EffectTarget
{
    /// <summary>The sending actor itself (an external-queue delivery, not an internal raise).</summary>
    public const string Self = "#_self";

    /// <summary>The sending actor's parent.</summary>
    public const string Parent = "#_parent";
}

/// <summary>
/// Create a child actor — not yet started; the matching <see cref="StartEffect"/> arrives at the
/// end of the macrostep.
/// </summary>
/// <param name="Src">
/// The registered source key this logic was resolved from, or null for inline (unregistered)
/// logic. Inline logic is runnable but not persistable: a snapshot whose live child has no
/// <c>Src</c> cannot be restored.
/// </param>
/// <param name="Incarnation">
/// Token minted for this particular child instance; the host echoes it as the
/// <c>SessionId</c> of the <see cref="DoneActorEvent"/>/<see cref="ErrorActorEvent"/> it relays,
/// so a completion from a previous incarnation of the same id can be dropped.
/// </param>
/// <param name="SyncSnapshot">
/// Whether the parent asked to observe this child's snapshots (xstate v6 <c>syncSnapshot</c>,
/// set by an invoke's <c>onSnapshot</c>). The host relays a <see cref="SnapshotEvent"/> to the
/// parent after every transition the child takes.
/// </param>
public sealed record SpawnEffect(
    string Id,
    IActorLogic Logic,
    object? Input,
    string? Src,
    string Incarnation,
    bool SyncSnapshot = false) : Effect
{
    public override string Kind => "@xstate.spawn";
}

/// <summary>
/// Start a child spawned earlier in this macrostep. Emitted at the <em>end</em> of the macrostep,
/// one per <see cref="SpawnEffect"/>, in spawn order (xstate v6 <c>deriveDeferredStarts</c>).
/// </summary>
public sealed record StartEffect(string Id) : Effect
{
    public override string Kind => "@xstate.start";
}

/// <summary>
/// A <em>delayed</em> raise: the event is destined for this actor's own internal queue once the
/// delay elapses. An immediate raise never surfaces as an effect — it goes straight onto the
/// internal queue inside the macrostep. The host schedules a timer under <see cref="Id"/> on the
/// raising actor and sends it <see cref="TimerEvent"/> when it fires.
/// </summary>
public sealed record RaiseEffect(MachineEvent Event, string? Id, TimeSpan Delay) : Effect
{
    public override string Kind => "@xstate.raise";
}

/// <summary>
/// Send an event to another actor. <see cref="Target"/> is a child id,
/// <see cref="EffectTarget.Parent"/> or <see cref="EffectTarget.Self"/> — never null.
/// <see cref="Delay"/> null means deliver now; otherwise the host schedules a timer under
/// <see cref="Id"/> <em>on the sender</em> and sends it <see cref="TimerEvent"/> when it fires.
/// </summary>
public sealed record SendToEffect(
    string Target,
    MachineEvent Event,
    string? Id = null,
    TimeSpan? Delay = null) : Effect
{
    public override string Kind => "@xstate.sendTo";
}

/// <summary>Cancel a logical timer by id.</summary>
public sealed record CancelEffect(string SendId) : Effect
{
    public override string Kind => "@xstate.cancel";
}

/// <summary>Stop a child actor.</summary>
public sealed record StopChildEffect(string ChildId) : Effect
{
    public override string Kind => "@xstate.stop";
}

/// <summary>
/// The actor reached a terminal status. Appended once when a macrostep ends with
/// <see cref="SnapshotStatus.Done"/> or <see cref="SnapshotStatus.Error"/> (xstate v6
/// <c>completeMacrostep</c> / <c>finalizeTransitionResult</c>); the host relays the corresponding
/// <see cref="DoneActorEvent"/>/<see cref="ErrorActorEvent"/> to the parent.
/// </summary>
public sealed record TerminateEffect(SnapshotStatus Status, object? Output, Exception? Error) : Effect
{
    public override string Kind => "@xstate.terminate";
}

/// <summary>
/// An event was rejected at the actor boundary and never delivered; the snapshot is unchanged.
/// <see cref="Reason"/> is one of <c>"stopped"</c>, <c>"invalidEvent"</c>, <c>"internalEvent"</c>
/// (xstate v6 <c>EventRejectionReason</c>).
/// </summary>
public sealed record DeadLetterEffect(MachineEvent Event, string Reason, Exception? Error = null) : Effect
{
    public override string Kind => "@xstate.deadLetter";
}

/// <summary>Emit an event to this actor's subscribers.</summary>
public sealed record EmitEffect(MachineEvent Event) : Effect
{
    public override string Kind => "emit";
}

/// <summary>
/// Run an action. <see cref="Type"/> null means an inline closure (runnable in-process only,
/// journaled by effect position); a non-null <see cref="Type"/> gives the action a portable
/// identity (<c>type</c> + <c>params</c>) a durable host can replay. <see cref="Exec"/> is always
/// available locally.
/// </summary>
public sealed record ActionEffect(string? Type, object? Params, Action Exec) : Effect
{
    public override string Kind => "action";
}

/// <summary>
/// Parameters of the built-in <c>xstate.log</c> action, so a host can read a log action's payload
/// without running it.
/// </summary>
public sealed record LogParams(object? Message, string? Label = null);
