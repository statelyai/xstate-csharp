namespace XState.Json;

/// <summary>
/// Reader options for <see cref="MachineConfig"/>.
/// </summary>
public sealed class JsonConfigOptions
{
    /// <summary>
    /// When true, a key that is not part of the XState v6 machine schema is ignored instead of
    /// throwing. Keys that the schema <em>does</em> define but this port cannot honour still throw
    /// a <see cref="NotSupportedException"/>: dropping them silently would change behaviour.
    /// </summary>
    public bool IgnoreUnknownKeys { get; set; }

    internal static readonly JsonConfigOptions Default = new();
}
