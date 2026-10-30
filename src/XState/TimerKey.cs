using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XState;

/// <summary>
/// The identity of a logical timer (xstate v6 <c>TimerKey</c>, <c>timerKey.ts</c>): an explicit
/// <see cref="ById">id</see>, else the actor's <see cref="BySeq">sequence number</see> for an
/// id-less delayed raise/send, else — for built-in <c>after</c>/<c>timeout</c> timers, which
/// have neither — <see cref="ByEvent">their event</see>. Hosts key their scheduler by
/// (source address, <see cref="Serialize"/>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$key")]
[JsonDerivedType(typeof(ById), "id")]
[JsonDerivedType(typeof(BySeq), "seq")]
[JsonDerivedType(typeof(ByEvent), "event")]
public abstract record TimerKey
{
    private TimerKey() { }

    /// <summary>A timer scheduled with an explicit id.</summary>
    public sealed record ById(string Id) : TimerKey;

    /// <summary>An id-less delayed raise/send, numbered by the actor's timer sequence.</summary>
    public sealed record BySeq(int Seq) : TimerKey;

    /// <summary>
    /// A timer identified by its event. For the built-in timer events only the identity fields
    /// are compared (<c>xstate.after</c>: state id + delay; <c>xstate.timeout.state</c>: state
    /// id; <c>xstate.timeout.actor</c>: actor id — never the incarnation token).
    /// </summary>
    public sealed record ByEvent(MachineEvent Event) : TimerKey;

    /// <summary>The key of a logical timer (v6 <c>getTimerKey</c>): id, else seq, else event.</summary>
    internal static TimerKey For(LogicalTimer timer) => For(timer.Id, timer.Seq, timer.Event);

    /// <summary>
    /// The key of a delayed <see cref="RaiseEffect"/>/<see cref="SendToEffect"/> from its
    /// <c>Id</c>, <c>Seq</c> and <c>Event</c>: id, else seq, else event.
    /// </summary>
    public static TimerKey For(string? id, int? seq, MachineEvent evt) =>
        id is not null ? new ById(id)
        : seq is { } n ? new BySeq(n)
        : new ByEvent(evt);

    /// <summary>Whether <paramref name="timer"/> has this key (v6 <c>matchesTimerKey</c>).</summary>
    internal bool Matches(LogicalTimer timer) => this switch
    {
        ById k => timer.Id == k.Id,
        _ when timer.Id is not null => false,
        BySeq k => timer.Seq == k.Seq,
        _ when timer.Seq is not null => false,
        ByEvent k => SameIdentity(timer.Event, k.Event),
        _ => false
    };

    /// <summary>
    /// A stable string form (v6 <c>serializeTimerKey</c>): <c>["id","…"]</c>, <c>["seq",n]</c> or
    /// <c>["event",{…identity}]</c>. Two keys that match the same timer serialize alike.
    /// </summary>
    public string Serialize() => this switch
    {
        ById k => JsonSerializer.Serialize(new object[] { "id", k.Id }),
        BySeq k => JsonSerializer.Serialize(new object[] { "seq", k.Seq }),
        ByEvent k => "[\"event\"," + SerializeIdentity(k.Event) + "]",
        _ => throw new InvalidOperationException("Unknown timer key.")
    };

    private static bool SameIdentity(MachineEvent timerEvent, MachineEvent pattern) => (timerEvent, pattern) switch
    {
        (AfterEvent a, AfterEvent b) => a.StateId == b.StateId && DelayKey(a.Delay) == DelayKey(b.Delay),
        (TimeoutEvent a, TimeoutEvent b) => a.StateId == b.StateId,
        (ActorTimeoutEvent a, ActorTimeoutEvent b) => a.ActorId == b.ActorId,
        _ => timerEvent.Equals(pattern)
    };

    private static string DelayKey(object delay) => Convert.ToString(delay, CultureInfo.InvariantCulture) ?? "";

    private static string SerializeIdentity(MachineEvent evt) => evt switch
    {
        AfterEvent a => JsonSerializer.Serialize(new { type = a.Type, delay = DelayKey(a.Delay), stateId = a.StateId }),
        TimeoutEvent t => JsonSerializer.Serialize(new { type = t.Type, stateId = t.StateId }),
        ActorTimeoutEvent t => JsonSerializer.Serialize(new { type = t.Type, actorId = t.ActorId }),
        _ => JsonSerializer.Serialize(new { type = evt.Type, value = evt.ToString() })
    };
}
