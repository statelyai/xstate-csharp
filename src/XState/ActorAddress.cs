namespace XState;

/// <summary>
/// Deterministic actor addresses — a port of xstate v6 <c>createActor.ts</c> (address
/// construction) and <c>system.ts</c> (<c>encodeAddressSegment</c>, <c>getRootActorId</c>).
/// <para>
/// An address is the path of actor ids from the root actor down to an actor, joined by
/// <c>/</c>: <c>parent.Address + "/" + EncodeSegment(childId)</c>. Because every id along
/// that path is itself deterministic (an explicit invoke/spawn id, or a generated
/// <c>{prefix}:{n}</c> allocated from the parent snapshot's counters), the address is
/// reproduced exactly by a replay from the same snapshot.
/// </para>
/// <para>
/// <b>The address — never a session id — is the durable identity of an actor</b> (xstate v6
/// <c>docs/durable-execution.md</c>). A session id is minted per process by whatever runtime
/// happens to be running the actor and is meaningless after a restart; an address survives
/// persist/restore and identifies the same logical actor across runs.
/// </para>
/// </summary>
public static class ActorAddress
{
    /// <summary>Separator between address segments.</summary>
    public const char Separator = '/';

    /// <summary>
    /// Placeholder id carried by anonymous machine logic (the SCXML importer uses it for a
    /// <c>&lt;scxml&gt;</c> without a <c>name</c>). It never earns a named id prefix or a named
    /// address root.
    /// </summary>
    public const string AnonymousMachineId = "(machine)";

    /// <summary>Root address of an actor whose logic is anonymous.</summary>
    public const string AnonymousRoot = "x:0";

    /// <summary>
    /// Encodes one actor id as an address segment. <c>/</c> separates segments, so it is
    /// percent-encoded, and the escape character <c>%</c> is escaped <em>first</em> so the
    /// encoding stays injective: the ids <c>a/b</c> and <c>a%2Fb</c> must not produce the same
    /// address.
    /// </summary>
    public static string EncodeSegment(string id) =>
        id.Contains('/') || id.Contains('%')
            ? id.Replace("%", "%25", StringComparison.Ordinal)
                .Replace("/", "%2F", StringComparison.Ordinal)
            : id;

    /// <summary>The address of the child registered under <paramref name="id"/>.</summary>
    public static string Child(string parentAddress, string id) =>
        $"{parentAddress}{Separator}{EncodeSegment(id)}";

    /// <summary>
    /// The root segment for a parentless actor: the logic's own id, or <see cref="AnonymousRoot"/>
    /// when the logic is anonymous (xstate v6 <c>getRootActorId</c>).
    /// </summary>
    public static string Root(string? machineId) =>
        string.IsNullOrEmpty(machineId) || machineId == AnonymousMachineId
            ? AnonymousRoot
            : EncodeSegment(machineId);
}
