using System.Text.Json;

namespace XState.Json;

/// <summary>
/// The property lists of the XState v6 machine schema (<c>machine.schema.json</c>, <c>$defs</c>),
/// and the checks that hold a config to them: an unknown key is a typo and throws, a key the
/// schema defines but this port cannot honour throws <see cref="NotSupportedException"/>. Neither
/// is ever dropped silently.
/// </summary>
internal static class JsonSchemaKeys
{
    /// <summary><c>$defs.stateNode</c> — the machine root is a state node too.</summary>
    internal static readonly string[] StateNode =
    [
        "@exprLang", "id", "key", "type", "initial", "states", "on", "onError", "after", "always",
        "choice", "route", "invoke", "entry", "exit", "timeout", "onTimeout", "meta", "description",
        "history", "target", "output", "input", "tags", "context", "version", "internalEvents",
        "schemas", "actions", "guards", "actors", "delays",
        // Port extension: `onDone` is part of the v6 state-node *config* (types.v6.ts) but was
        // never added to the JSON schema; the port reads it so a done-state handler survives
        // a round trip through JSON.
        "onDone"
    ];

    /// <summary>Keys that only mean something on the machine root (v6 <c>MachineJSON</c>).</summary>
    internal static readonly string[] RootOnly =
    [
        "@exprLang", "version", "internalEvents", "schemas", "actions", "guards", "actors", "delays",
        "context"
    ];

    internal static readonly string[] TransitionObject =
        ["target", "actions", "context", "matches", "guard", "input", "description", "reenter", "meta"];

    internal static readonly string[] InvokeObject =
        ["id", "registryKey", "src", "input", "onDone", "onError", "onSnapshot", "onTimeout", "timeout"];

    internal static readonly string[] GuardObject = ["type", "params"];

    /// <summary>v6 <c>CustomActionJSON</c>.</summary>
    internal static readonly string[] ActionObject = ["type", "params"];

    internal static readonly string[] Initial = ["target", "input"];

    internal static readonly string[] DelayDefinition = ["duration"];

    internal static readonly string[] GuardDefinition = ["when"];

    /// <summary>Built-in action types (v6 <c>BuiltInActionJSON</c>) and the keys each carries.</summary>
    internal static readonly Dictionary<string, string[]> BuiltInActions = new(StringComparer.Ordinal)
    {
        ["@xstate.raise"] = ["type", "event", "id", "delay"],
        ["@xstate.cancel"] = ["type", "id"],
        ["@xstate.log"] = ["type", "args"],
        ["@xstate.emit"] = ["type", "event"],
        ["@xstate.assign"] = ["type", "context"]
    };

    /// <summary>XState v4 keys, and the v5/v6 key that replaced them.</summary>
    internal static readonly (string Legacy, string Replacement)[] LegacyStateKeys =
        [("onEntry", "entry"), ("onExit", "exit"), ("activities", "invoke")];

    internal static readonly (string Legacy, string Replacement)[] LegacyTransitionKeys =
        [("cond", "guard"), ("internal", "reenter")];

    /// <summary>
    /// Checks every key of <paramref name="element"/> against <paramref name="known"/>. An unknown
    /// key throws with the JSON path and the closest known key, unless the reader was asked to be
    /// lenient.
    /// </summary>
    internal static void Validate(
        JsonElement element,
        string path,
        IReadOnlyList<string> known,
        string machineId,
        JsonConfigOptions options)
    {
        if (options.IgnoreUnknownKeys || element.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (known.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }

            var suggestion = Closest(property.Name, known);
            throw new JsonException(
                $"Unknown key '{path}.{property.Name}' in machine '{machineId}'" +
                (suggestion is null ? "." : $" (did you mean '{suggestion}'?)"));
        }
    }

    /// <summary>The known key nearest to <paramref name="name"/>, or null when none is close.</summary>
    private static string? Closest(string name, IReadOnlyList<string> known)
    {
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in known)
        {
            var distance = Distance(name, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        // Only volunteer a suggestion when it is genuinely close — one edit for a short key,
        // up to three for a long one (so a transposition like "entyr" still finds "entry").
        return bestDistance <= Math.Clamp((name.Length / 3) + 1, 1, 3) ? best : null;
    }

    /// <summary>Levenshtein distance, case-insensitive.</summary>
    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
