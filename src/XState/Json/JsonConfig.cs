using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XState.Json;

/// <summary>JSON-shape helpers shared by the config reader (context-type independent).</summary>
internal static partial class JsonConfig
{
    /// <summary>Stand-in context for configs that declare none.</summary>
    internal static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>Placeholder logic used while collecting missing implementations before throwing.</summary>
    internal static readonly IActorLogic MissingActorLogic = new MissingActor();

    private sealed class MissingActor : IActorLogic
    {
        public (ISnapshot State, IReadOnlyList<Effect> Effects) GetInitialSnapshot(object? input) =>
            throw new InvalidOperationException("This actor source had no implementation at build time.");

        public (ISnapshot State, IReadOnlyList<Effect> Effects) Transition(ISnapshot state, MachineEvent evt) =>
            throw new InvalidOperationException("This actor source had no implementation at build time.");
    }

    /// <summary>Property lookup that treats non-objects and explicit nulls as "absent".</summary>
    internal static bool TryProp(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind is not JsonValueKind.Object ||
            !element.TryGetProperty(name, out value) ||
            value.ValueKind is JsonValueKind.Null)
        {
            value = default;
            return false;
        }

        return true;
    }

    internal static string? GetString(JsonElement element, string name) =>
        TryProp(element, name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The reserved <c>@expr</c>/<c>@code</c> slots require an evaluator; this port has none.</summary>
    internal static void RejectExpression(JsonElement element, string path, string machineId)
    {
        if (element.ValueKind is JsonValueKind.Object &&
            (element.TryGetProperty("@expr", out _) || element.TryGetProperty("@code", out _)))
        {
            throw new NotSupportedException(
                $"Inline expressions (@expr/@code) at {path} in machine '{machineId}' are not supported; " +
                "provide a named implementation instead.");
        }
    }

    /// <summary>
    /// Shallow-merges <paramref name="patch"/> into <paramref name="target"/>, mirroring the
    /// <c>Object.assign</c> that v6's <c>@xstate.assign</c> and transition <c>context</c> perform.
    /// Properties keep their order: existing ones are overwritten in place, new ones are appended.
    /// </summary>
    internal static JsonElement Merge(JsonElement target, JsonElement patch)
    {
        if (patch.ValueKind is not JsonValueKind.Object)
        {
            return patch;
        }

        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            var written = new HashSet<string>(StringComparer.Ordinal);
            if (target.ValueKind is JsonValueKind.Object)
            {
                foreach (var property in target.EnumerateObject())
                {
                    written.Add(property.Name);
                    writer.WritePropertyName(property.Name);
                    (patch.TryGetProperty(property.Name, out var replacement)
                        ? replacement
                        : property.Value).WriteTo(writer);
                }
            }

            foreach (var property in patch.EnumerateObject())
            {
                if (written.Add(property.Name))
                {
                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    internal static bool TryDuration(JsonElement element, out TimeSpan value)
    {
        if (element.ValueKind is JsonValueKind.Number && element.TryGetDouble(out var ms))
        {
            value = TimeSpan.FromMilliseconds(ms);
            return true;
        }

        if (element.ValueKind is JsonValueKind.String)
        {
            return TryDuration(element.GetString()!, out value);
        }

        value = default;
        return false;
    }

    /// <summary>Mirrors delay.ts <c>parseDelayToMilliseconds</c>: plain ms, "500ms", "1.5s", ISO-8601.</summary>
    internal static bool TryDuration(string text, out TimeSpan value)
    {
        var trimmed = text.Trim();

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
        {
            value = TimeSpan.FromMilliseconds(plain);
            return true;
        }

        if (MillisecondsPattern().Match(trimmed) is { Success: true } ms)
        {
            value = TimeSpan.FromMilliseconds(int.Parse(ms.Groups[1].Value, CultureInfo.InvariantCulture));
            return true;
        }

        if (SecondsPattern().Match(trimmed) is { Success: true } seconds)
        {
            var whole = seconds.Groups[1].Value is { Length: > 0 } w
                ? int.Parse(w, CultureInfo.InvariantCulture)
                : 0;
            var fraction = seconds.Groups[2].Value.Length > 0 && seconds.Groups[3].Value is { Length: > 0 } f
                ? int.Parse(f.PadRight(3, '0')[..3], CultureInfo.InvariantCulture)
                : 0;
            value = TimeSpan.FromMilliseconds(whole * 1000 + fraction);
            return true;
        }

        if (Iso8601Pattern().Match(trimmed) is { Success: true } iso &&
            iso.Groups.Cast<Group>().Skip(1).Any(g => g.Success))
        {
            static double Part(Match match, string name) =>
                match.Groups[name].Success
                    ? double.Parse(match.Groups[name].Value.Replace(',', '.'), CultureInfo.InvariantCulture)
                    : 0;

            value = TimeSpan.FromMilliseconds(
                Part(iso, "weeks") * 7 * 24 * 60 * 60 * 1000 +
                Part(iso, "days") * 24 * 60 * 60 * 1000 +
                Part(iso, "hours") * 60 * 60 * 1000 +
                Part(iso, "minutes") * 60 * 1000 +
                Part(iso, "seconds") * 1000);
            return true;
        }

        value = default;
        return false;
    }

    [GeneratedRegex(@"^(\d+)ms$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MillisecondsPattern();

    [GeneratedRegex(@"^(\d*)(\.?)(\d*)s$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecondsPattern();

    [GeneratedRegex(
        @"^P(?:(?<weeks>\d+(?:[.,]\d+)?)W)?(?:(?<days>\d+(?:[.,]\d+)?)D)?(?:T(?:(?<hours>\d+(?:[.,]\d+)?)H)?(?:(?<minutes>\d+(?:[.,]\d+)?)M)?(?:(?<seconds>\d+(?:[.,]\d+)?)S)?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Iso8601Pattern();
}
