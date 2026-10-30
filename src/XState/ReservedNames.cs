namespace XState;

/// <summary>Checks that keep user names out of the built-in <c>xstate.</c> / <c>@xstate.</c> namespaces.</summary>
internal static class ReservedNames
{
    /// <summary>
    /// utils.ts <c>assertUnreservedSourceName</c>: throws when a user source name (action, guard,
    /// actor or delay) enters the <c>xstate.</c> / <c>@xstate.</c> namespaces, which are reserved
    /// for built-in events, effects and source keys. Always enforced (this port has no dev mode).
    /// </summary>
    internal static void AssertUnreservedSourceName(string kind, string name)
    {
        if (name.StartsWith("xstate.", StringComparison.Ordinal) ||
            name.StartsWith("@xstate.", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Invalid {kind} name '{name}': the 'xstate.' and '@xstate.' prefixes are reserved for built-in names.");
        }
    }

    private static readonly string[] LifecycleCategories =
    [
        "xstate.done.actor",
        "xstate.error.actor",
        "xstate.snapshot.actor",
        "xstate.done.state",
        "xstate.after",
        "xstate.timeout.state",
        "xstate.timeout.actor",
    ];

    /// <summary>
    /// Rejects a transition key of the obsolete suffixed lifecycle form
    /// (<c>xstate.done.actor.{id}</c>, <c>xstate.after.{delay}.{id}</c>, ...). Lifecycle events
    /// carry only their category type (xstate v6 alpha.68), so such a key would never match.
    /// </summary>
    internal static void AssertNotObsoleteEventType(string key)
    {
        if (ObsoleteCategory(key) is { } category)
        {
            throw new ArgumentException(
                $"Obsolete event type '{key}': suffixed lifecycle events are no longer matched. " +
                $"Use '{category}' with a matches filter.");
        }
    }

    private static string? ObsoleteCategory(string key)
    {
        if (!key.StartsWith("xstate.", StringComparison.Ordinal) || key.EndsWith(".*", StringComparison.Ordinal) ||
            LifecycleCategories.Contains(key))
        {
            return null;
        }

        string[] suffixed =
        [
            "xstate.done.actor", "xstate.error.actor", "xstate.done.state", "xstate.after",
            "xstate.timeout.actor", "xstate.snapshot", "xstate.timeout",
        ];

        foreach (var prefix in suffixed)
        {
            if (key.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return prefix switch
                {
                    "xstate.snapshot" => "xstate.snapshot.actor",
                    "xstate.timeout" => "xstate.timeout.state",
                    _ => prefix,
                };
            }
        }

        return null;
    }
}
