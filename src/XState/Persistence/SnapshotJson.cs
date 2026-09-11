using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace XState.Persistence;

/// <summary>
/// <c>System.Text.Json</c> options that round-trip a <see cref="PersistedSnapshot"/>.
/// <para>
/// Two things in a snapshot are not plain JSON on their own. <see cref="MachineEvent"/> is a
/// polymorphic hierarchy, so an event is written with a <c>$event</c> discriminator carrying its
/// CLR type name alongside the <c>type</c> the machine matches on. And the loosely typed payload
/// slots (<see cref="PersistedSnapshot.Value"/>, <see cref="PersistedSnapshot.Context"/>,
/// <see cref="PersistedSnapshot.Output"/>, event payloads) are <c>object</c>, which
/// <c>System.Text.Json</c> would otherwise hand back as <see cref="JsonElement"/>; here they come
/// back as plain strings, numbers, lists and dictionaries, which
/// <see cref="StateMachine{TContext}.Restore"/> reads directly.
/// </para>
/// <para>
/// The context still needs <see cref="RestoreOptions{TContext}.ContextConverter"/>: no serializer
/// can guess <c>TContext</c> from a snapshot alone.
/// </para>
/// </summary>
public static class SnapshotJson
{
    /// <summary>
    /// Options that serialize and deserialize a <see cref="PersistedSnapshot"/>.
    /// </summary>
    /// <param name="eventTypes">
    /// Additional <see cref="MachineEvent"/> types to recognise by CLR name on the way back in.
    /// Built-in events are always recognised; an event whose <c>$event</c> name is not registered
    /// is read back as a <see cref="NamedEvent"/> keeping its <c>type</c> and its payload's raw
    /// JSON, so an unknown event is preserved rather than lost.
    /// </param>
    public static JsonSerializerOptions DefaultOptions(params Type[] eventTypes)
    {
        if (eventTypes is null or { Length: 0 })
        {
            return DefaultOptionsCache.Value;
        }

        return BuildOptions(eventTypes);
    }

    // System.Text.Json caches its metadata per options instance, so the no-argument options are
    // built once and frozen rather than rebuilt (and re-reflected) on every call.
    private static readonly Lazy<JsonSerializerOptions> DefaultOptionsCache = new(() =>
    {
        var options = BuildOptions([]);
        options.MakeReadOnly();
        return options;
    });

    private static JsonSerializerOptions BuildOptions(Type[] eventTypes)
    {
        var registry = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in BuiltInEventTypes)
        {
            registry[type.Name] = type;
        }

        foreach (var type in eventTypes)
        {
            if (!typeof(MachineEvent).IsAssignableFrom(type))
            {
                throw new ArgumentException(
                    $"'{type.FullName}' is not a {nameof(MachineEvent)}.", nameof(eventTypes));
            }

            // The wire discriminator is the simple CLR name, so two types sharing one (a custom
            // `TimerEvent`, say) would silently deserialize into the wrong type.
            if (registry.TryGetValue(type.Name, out var existing) && existing != type)
            {
                throw new ArgumentException(
                    $"Event type name '{type.Name}' is already registered for '{existing.FullName}'; " +
                    $"'{type.FullName}' cannot share it.", nameof(eventTypes));
            }

            registry[type.Name] = type;
        }

        // The payload options serialize an event's own properties. They deliberately omit the
        // polymorphic converter — that is what stops it recursing into itself.
        var payloadOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = EventShapeResolver,
            Converters = { new LooseObjectConverter() }
        };

        return new JsonSerializerOptions
        {
            TypeInfoResolver = EventShapeResolver,
            Converters =
            {
                new LooseObjectConverter(),
                new MachineEventConverter(registry, payloadOptions)
            }
        };
    }

    private static readonly Type[] BuiltInEventTypes =
    [
        typeof(InitEvent),
        typeof(StopEvent),
        typeof(NamedEvent),
        typeof(DoneStateEvent),
        typeof(DoneActorEvent),
        typeof(ErrorActorEvent),
        typeof(AfterEvent),
        typeof(TimeoutEvent),
        typeof(ActorTimeoutEvent),
        typeof(TimerEvent),
        typeof(ErrorPlatformEvent)
    ];

    /// <summary>
    /// Drops <see cref="MachineEvent.Type"/> from an event's own properties: it is derived from the
    /// CLR type, is written once as the canonical lowercase <c>type</c> field, and has no setter
    /// to read back into.
    /// </summary>
    private static readonly DefaultJsonTypeInfoResolver EventShapeResolver = CreateEventShapeResolver();

    private static DefaultJsonTypeInfoResolver CreateEventShapeResolver()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (!typeof(MachineEvent).IsAssignableFrom(typeInfo.Type))
            {
                return;
            }

            for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
            {
                if (typeInfo.Properties[i].Name == nameof(MachineEvent.Type))
                {
                    typeInfo.Properties.RemoveAt(i);
                }
            }
        });

        return resolver;
    }

    /// <summary>The discriminator property carrying an event's CLR type name.</summary>
    internal const string Discriminator = "$event";

    /// <summary>The canonical event type property — what the machine actually matches on.</summary>
    internal const string TypeProperty = "type";

    private sealed class MachineEventConverter(
        IReadOnlyDictionary<string, Type> registry,
        JsonSerializerOptions payloadOptions) : JsonConverter<MachineEvent>
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeof(MachineEvent).IsAssignableFrom(typeToConvert);

        public override MachineEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var element = document.RootElement;

            var eventType = element.TryGetProperty(Discriminator, out var name) && name.ValueKind is JsonValueKind.String
                ? name.GetString()
                : null;
            var typeName = element.TryGetProperty(TypeProperty, out var t) && t.ValueKind is JsonValueKind.String
                ? t.GetString()!
                : eventType ?? "";

            if (eventType is null || !registry.TryGetValue(eventType, out var clrType))
            {
                // An event this process has no type for still has to survive: keep the type the
                // machine matches on, and the payload verbatim.
                return new NamedEvent(typeName, element.GetRawText());
            }

            // `ErrorActorEvent` carries a live `Exception`, which no serializer can round-trip.
            // Only its message crosses the boundary; see `PersistedError` for the same trade on
            // the snapshot's own error.
            if (clrType == typeof(ErrorActorEvent))
            {
                return new ErrorActorEvent(
                    GetString(element, nameof(ErrorActorEvent.ActorId)) ?? "",
                    new Exception(GetString(element, nameof(ErrorActorEvent.Error))),
                    GetString(element, nameof(ErrorActorEvent.SessionId)));
            }

            return (MachineEvent)element.Deserialize(clrType, payloadOptions)!;
        }

        public override void Write(Utf8JsonWriter writer, MachineEvent value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString(TypeProperty, value.Type);
            writer.WriteString(Discriminator, value.GetType().Name);

            if (value is ErrorActorEvent error)
            {
                writer.WriteString(nameof(ErrorActorEvent.ActorId), error.ActorId);
                writer.WriteString(nameof(ErrorActorEvent.Error), error.Error.Message);
                if (error.SessionId is { } session)
                {
                    writer.WriteString(nameof(ErrorActorEvent.SessionId), session);
                }
            }
            else
            {
                using var payload = JsonSerializer.SerializeToDocument(value, value.GetType(), payloadOptions);
                foreach (var property in payload.RootElement.EnumerateObject())
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        private static string? GetString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>
    /// Reads <c>object</c>-typed slots into plain CLR values instead of <see cref="JsonElement"/>,
    /// and writes them by their runtime type.
    /// </summary>
    private sealed class LooseObjectConverter : JsonConverter<object>
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(object);

        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return ToClr(document.RootElement);
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            var runtimeType = value.GetType();
            if (runtimeType == typeof(object))
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
                return;
            }

            JsonSerializer.Serialize(writer, value, runtimeType, options);
        }

        private static object? ToClr(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        map[property.Name] = ToClr(property.Value);
                    }

                    return map;

                case JsonValueKind.Array:
                    var items = new List<object?>();
                    foreach (var item in element.EnumerateArray())
                    {
                        items.Add(ToClr(item));
                    }

                    return items;

                case JsonValueKind.String:
                    return element.GetString();

                case JsonValueKind.Number:
                    return element.TryGetInt64(out var integer) ? integer : element.GetDouble();

                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                default:
                    return null;
            }
        }
    }
}
