namespace XState;

/// <summary>
/// A serializable view of one <see cref="Effect"/> — a port of xstate v6
/// <c>effectDescriptor.ts</c> (<c>getEffectDescriptor</c>).
/// <para>
/// Actor references become <em>addresses</em> (<see cref="ActorAddress"/>), the actor's durable
/// identity, so a host can journal, deduplicate and route effects without holding live actor
/// references. Non-serializable members of the effect — the spawned <see cref="IActorLogic"/>
/// and the <see cref="ActionEffect.Exec"/> closure — are dropped; payload fields
/// (<see cref="Event"/>, <see cref="Input"/>, <see cref="Params"/>, <see cref="Output"/>) pass
/// through by reference and are only as serializable as their values.
/// </para>
/// </summary>
/// <param name="Kind">The effect discriminant (<see cref="Effect.Kind"/>).</param>
/// <param name="Source">Address of the actor that produced the effect.</param>
/// <param name="Actor">Address of the actor the effect acts on (spawn/start/stop/terminate).</param>
/// <param name="Target">Address of the delivery target (sendTo, dead letter).</param>
/// <param name="Id">Actor id, timer id, or cancellation id, depending on the kind.</param>
/// <param name="Src">Registered actor-source key of a spawn, or null for inline logic.</param>
public sealed record EffectDescriptor(
    string Kind,
    string? Source,
    string? Actor,
    string? Target,
    string? Id,
    string? Src,
    object? Input,
    MachineEvent? Event,
    TimeSpan? Delay,
    string? Type,
    object? Params,
    SnapshotStatus? Status,
    object? Output,
    string? Error,
    string? Reason)
{
    /// <summary>
    /// Projects <paramref name="effect"/>, produced by the actor at
    /// <paramref name="sourceAddress"/>, onto its serializable descriptor.
    /// </summary>
    public static EffectDescriptor Of(Effect effect, string sourceAddress)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(sourceAddress);

        return effect switch
        {
            SpawnEffect e => Empty(e) with
            {
                Source = sourceAddress,
                Actor = ActorAddress.Child(sourceAddress, e.Id),
                Id = e.Id,
                Src = e.Src,
                Input = e.Input
            },

            StartEffect e => Empty(e) with
            {
                Source = sourceAddress,
                Actor = ActorAddress.Child(sourceAddress, e.Id),
                Id = e.Id
            },

            RaiseEffect e => Empty(e) with
            {
                Source = sourceAddress,
                Id = e.Id,
                Event = e.Event,
                Delay = e.Delay
            },

            SendToEffect e => Empty(e) with
            {
                Source = sourceAddress,
                Target = ResolveAddress(sourceAddress, e.Target),
                Id = e.Id,
                Event = e.Event,
                Delay = e.Delay
            },

            CancelEffect e => Empty(e) with { Source = sourceAddress, Id = e.SendId },

            StopChildEffect e => Empty(e) with
            {
                Source = sourceAddress,
                Actor = ActorAddress.Child(sourceAddress, e.ChildId),
                Id = e.ChildId
            },

            TerminateEffect e => Empty(e) with
            {
                Source = sourceAddress,
                Actor = sourceAddress,
                Status = e.Status,
                Output = e.Output,
                Error = e.Error?.Message
            },

            DeadLetterEffect e => Empty(e) with
            {
                Source = sourceAddress,
                Target = sourceAddress,
                Event = e.Event,
                Reason = e.Reason,
                Error = e.Error?.Message
            },

            EmitEffect e => Empty(e) with
            {
                Source = sourceAddress,
                Type = e.Event.Type,
                Event = e.Event
            },

            // `action` effects carry no source in v6: they are portable by (type, params) alone.
            ActionEffect e => Empty(e) with { Type = e.Type, Params = e.Params },

            _ => Empty(effect) with { Source = sourceAddress }
        };
    }

    /// <summary>
    /// The address a <see cref="SendToEffect.Target"/> names: a child id becomes a child address,
    /// <see cref="EffectTarget.Self"/> the sender itself, and <see cref="EffectTarget.Parent"/> the
    /// sender's address minus its last segment.
    /// </summary>
    private static string ResolveAddress(string sourceAddress, string target) => target switch
    {
        EffectTarget.Self => sourceAddress,
        EffectTarget.Parent => ParentOf(sourceAddress),
        _ => ActorAddress.Child(sourceAddress, target)
    };

    private static string ParentOf(string address)
    {
        var separator = address.LastIndexOf(ActorAddress.Separator);
        return separator < 0 ? address : address[..separator];
    }

    private static EffectDescriptor Empty(Effect effect) =>
        new(effect.Kind, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
}
