namespace XState;

/// <summary>
/// Base type for all events. Typed transitions (<c>On&lt;T&gt;()</c>) match by CLR type;
/// string transitions (<c>On("TYPE")</c>) match by <see cref="Type"/> with SCXML-style
/// wildcard/prefix rules ("*", "error.*").
/// </summary>
public abstract record MachineEvent
{
    /// <summary>Event type identifier. Defaults to the CLR type name.</summary>
    public virtual string Type => GetType().Name;
}

/// <summary>An event identified only by its string type (imported machines, SCXML, JSON).</summary>
public sealed record NamedEvent(string Name, object? Data = null) : MachineEvent
{
    public override string Type => Name;
}

// --- Canonical internal events (mirror xstate v6 eventUtils.ts / constants.ts) ---

/// <summary>@xstate.init (v6 <c>XSTATE_INIT</c>).</summary>
public sealed record InitEvent(object? Input = null) : MachineEvent
{
    public override string Type => "@xstate.init";
}

/// <summary>
/// @xstate.stop (v6 <c>XSTATE_STOP</c>) — the actor is being stopped. Stopping stops the
/// live children and cancels the outstanding timers; it does <em>not</em> run exit actions.
/// </summary>
public sealed record StopEvent : MachineEvent
{
    public override string Type => "@xstate.stop";
}

/// <summary>
/// xstate.timer (v6 <c>XSTATE_TIMER</c>) — the host-neutral firing input for a logical timer
/// the machine scheduled (a delayed raise or a delayed send-to). The host schedules nothing on
/// the machine's behalf beyond the id: when the delay elapses it sends
/// <c>TimerEvent(Id)</c> back to the actor that owns the timer, and the machine looks the id up
/// in its own <see cref="State{TContext}.Timers"/> ledger. An id that is no longer on the ledger
/// is a stale firing and is ignored.
/// </summary>
public sealed record TimerEvent(string Id) : MachineEvent
{
    public override string Type => "xstate.timer";
}

/// <summary>xstate.done.state — a compound/parallel state's final state was reached.</summary>
public sealed record DoneStateEvent(string StateId, object? Output = null) : MachineEvent
{
    public override string Type => "xstate.done.state";
}

/// <summary>
/// xstate.done.actor — an invoked/spawned actor completed.
/// </summary>
/// <param name="SessionId">
/// The child's <em>incarnation token</em> (<see cref="ChildRecord.Incarnation"/>), echoed by the
/// host. Null means "the host does not track incarnations", and the event is accepted as-is
/// (xstate v6 <c>matchesActorSession</c>). A non-null token that disagrees with the ledger's
/// belongs to an earlier incarnation of the same id and is dropped.
/// </param>
public sealed record DoneActorEvent(string ActorId, object? Output, string? SessionId = null) : MachineEvent
{
    public override string Type => "xstate.done.actor";
}

/// <summary>xstate.error.actor — an invoked/spawned actor errored.</summary>
/// <param name="SessionId">The child's incarnation token; see <see cref="DoneActorEvent"/>.</param>
public sealed record ErrorActorEvent(string ActorId, Exception Error, string? SessionId = null) : MachineEvent
{
    public override string Type => "xstate.error.actor";
}

/// <summary>xstate.after — a delayed transition's timer fired for a state.</summary>
public sealed record AfterEvent(object Delay, string StateId) : MachineEvent
{
    public override string Type => "xstate.after";

    /// <summary>Timer id of the scheduled raise (xstate.after.{delay}.{stateId}).</summary>
    public string SendId => $"xstate.after.{Delay}.{StateId}";
}

/// <summary>xstate.timeout — a state-level timeout elapsed without the state exiting.</summary>
public sealed record TimeoutEvent(string StateId) : MachineEvent
{
    public override string Type => "xstate.timeout";

    public string SendId => $"xstate.timeout.{StateId}";
}

/// <summary>xstate.timeout.actor — an invoked actor exceeded its timeout.</summary>
public sealed record ActorTimeoutEvent(string ActorId, string? SessionId = null) : MachineEvent
{
    public override string Type => "xstate.timeout.actor";

    public string SendId => $"xstate.timeout.actor.{ActorId}";
}

/// <summary>
/// xstate.route — navigate to the state whose explicit id is named by <see cref="To"/>
/// (<c>"#dashboard"</c>). Only a state that declares both an explicit id and <c>Route(...)</c> is
/// reachable this way; an unmatched <c>to</c> leaves the machine untouched.
/// </summary>
public sealed record RouteEvent(string To) : MachineEvent
{
    public override string Type => "xstate.route";
}

/// <summary>
/// xstate.snapshot.actor — an invoked actor the parent asked to observe
/// (<c>onSnapshot</c> / xstate v6 <c>syncSnapshot</c>) reported a new snapshot. Only
/// <see cref="SnapshotStatus.Active"/> snapshots are relayed: a child that finished or failed
/// reports through <see cref="DoneActorEvent"/>/<see cref="ErrorActorEvent"/> instead.
/// </summary>
/// <param name="SessionId">The child's incarnation token; see <see cref="DoneActorEvent"/>.</param>
public sealed record SnapshotEvent(string ActorId, object? Snapshot, string? SessionId = null) : MachineEvent
{
    public override string Type => "xstate.snapshot.actor";
}

/// <summary>xstate.error.{kind} — platform error (e.g. communication errors per SCXML).</summary>
public sealed record ErrorPlatformEvent(string Kind, object? Error = null) : MachineEvent
{
    public override string Type => $"xstate.error.{Kind}";
}
