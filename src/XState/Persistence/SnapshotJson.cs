using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace XState.Persistence;

/// <summary>
/// <c>System.Text.Json</c> options that round-trip a <see cref="PersistedSnapshot"/> in xstate v6's
/// <c>persisted-snapshot/v1</c> shape (<c>persistedSnapshot.schema.json</c>; see
/// <c>docs/persistence.md</c> for the shape and the port's extensions).
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
    /// <summary>Serializes <paramref name="snapshot"/> to <c>persisted-snapshot/v1</c> JSON.</summary>
    public static string Serialize(PersistedSnapshot snapshot, JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(snapshot, options ?? DefaultOptions());

    /// <summary>
    /// Reads <c>persisted-snapshot/v1</c> JSON. Malformed JSON, or JSON that does not have the
    /// envelope's shape, throws <see cref="InvalidSnapshotException"/>; unknown fields are ignored.
    /// </summary>
    public static PersistedSnapshot Deserialize(string json, JsonSerializerOptions? options = null)
    {
        try
        {
            return JsonSerializer.Deserialize<PersistedSnapshot>(json, options ?? DefaultOptions())
                   ?? throw new InvalidSnapshotException("Persisted snapshot cannot be null.");
        }
        catch (JsonException e)
        {
            throw new InvalidSnapshotException($"Persisted snapshot is malformed: {e.Message}", e);
        }
    }

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

    /// <summary>The key of a serialized actor reference (v6 <c>{ $actor: address }</c>).</summary>
    internal const string ActorRefKey = "$actor";

    /// <summary>
    /// Whether <paramref name="element"/> is exactly a serialized actor reference:
    /// <c>$actor</c> plus, optionally, a string <c>incarnation</c>.
    /// </summary>
    internal static bool IsActorRef(JsonElement element)
    {
        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name == ActorRefKey && property.Value.ValueKind is JsonValueKind.String)
            {
                found = true;
            }
            else if (property.Name != "incarnation" || property.Value.ValueKind is not JsonValueKind.String)
            {
                return false;
            }
        }

        return found;
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
                case JsonValueKind.Object when IsActorRef(element):
                    return new SerializedActorRef(
                        element.GetProperty(ActorRefKey).GetString()!,
                        element.TryGetProperty("incarnation", out var incarnation) ? incarnation.GetString() : null);

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

/// <summary>Writes <see cref="SerializedActorRef"/> as <c>{ "$actor": address, "incarnation"?: … }</c>.</summary>
internal sealed class SerializedActorRefConverter : JsonConverter<SerializedActorRef>
{
    public override SerializedActorRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        if (element.ValueKind is not JsonValueKind.Object || !SnapshotJson.IsActorRef(element))
        {
            throw new InvalidSnapshotException("A serialized actor reference must be { \"$actor\": address }.");
        }

        return new SerializedActorRef(
            element.GetProperty(SnapshotJson.ActorRefKey).GetString()!,
            element.TryGetProperty("incarnation", out var incarnation) ? incarnation.GetString() : null);
    }

    public override void Write(Utf8JsonWriter writer, SerializedActorRef value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(SnapshotJson.ActorRefKey, value.Actor);
        if (value.Incarnation is { } incarnation)
        {
            writer.WriteString("incarnation", incarnation);
        }

        writer.WriteEndObject();
    }
}

/// <summary>Reads/writes <see cref="PersistedSnapshot.Children"/> as an object keyed by child id.</summary>
internal sealed class PersistedChildMapConverter : JsonConverter<IReadOnlyList<PersistedChild>>
{
    public override IReadOnlyList<PersistedChild> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartObject)
        {
            throw new InvalidSnapshotException("Persisted 'children' must be an object keyed by child id.");
        }

        var children = new List<PersistedChild>();
        while (reader.Read() && reader.TokenType is not JsonTokenType.EndObject)
        {
            var id = reader.GetString()!;
            reader.Read();
            var child = JsonSerializer.Deserialize<PersistedChild>(ref reader, options)
                        ?? throw new InvalidSnapshotException($"Persisted snapshot has invalid child '{id}'.");
            children.Add(child with { Id = id });
        }

        return children;
    }

    public override void Write(
        Utf8JsonWriter writer, IReadOnlyList<PersistedChild> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var child in value)
        {
            writer.WritePropertyName(child.Id);
            JsonSerializer.Serialize(writer, child, options);
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// One <c>children</c> entry: <c>{ address, src?, registryKey?, syncSnapshot?, snapshot? | remote:
/// true, incarnation? }</c>, plus the port's private <c>_incarnation</c> for an embedded child.
/// The id is the entry's key, filled in by <see cref="PersistedChildMapConverter"/>.
/// </summary>
internal sealed class PersistedChildConverter : JsonConverter<PersistedChild>
{
    private const string PrivateIncarnation = "_incarnation";

    public override PersistedChild Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var e = document.RootElement;
        if (e.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidSnapshotException("A persisted child entry must be an object.");
        }

        var remote = JsonField.Bool(e, "remote");
        return new PersistedChild
        {
            Id = "",
            Address = JsonField.String(e, "address") ?? "",
            Src = JsonField.String(e, "src"),
            RegistryKey = JsonField.String(e, "registryKey"),
            SyncSnapshot = JsonField.Bool(e, "syncSnapshot"),
            Snapshot = e.TryGetProperty("snapshot", out var snapshot) && snapshot.ValueKind is not JsonValueKind.Null
                ? snapshot.Deserialize<PersistedSnapshot>(options)
                : null,
            Remote = remote,
            Incarnation = JsonField.String(e, "incarnation") ?? JsonField.String(e, PrivateIncarnation)
        };
    }

    public override void Write(Utf8JsonWriter writer, PersistedChild value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("address", value.Address);
        if (value.Src is { } src)
        {
            writer.WriteString("src", src);
        }

        if (value.RegistryKey is { } registryKey)
        {
            writer.WriteString("registryKey", registryKey);
        }

        if (value.SyncSnapshot)
        {
            writer.WriteBoolean("syncSnapshot", true);
        }

        if (value.Remote)
        {
            writer.WriteBoolean("remote", true);
            if (value.Incarnation is { } incarnation)
            {
                writer.WriteString("incarnation", incarnation);
            }
        }
        else
        {
            if (value.Snapshot is { } snapshot)
            {
                writer.WritePropertyName("snapshot");
                JsonSerializer.Serialize(writer, snapshot, options);
            }

            if (value.Incarnation is { } incarnation)
            {
                writer.WriteString(PrivateIncarnation, incarnation);
            }
        }

        writer.WriteEndObject();
    }
}

/// <summary>One <c>timers</c> entry: <c>{ id?, seq?, delay, type, event, target, scheduledAt? }</c>.</summary>
internal sealed class PersistedTimerConverter : JsonConverter<PersistedTimer>
{
    public override PersistedTimer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var e = document.RootElement;
        if (e.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidSnapshotException("A persisted timer must be an object.");
        }

        var type = JsonField.String(e, "type");
        if (type is not ("@xstate.raise" or "@xstate.sendTo"))
        {
            throw new InvalidSnapshotException($"Persisted timer has invalid type '{type}'.");
        }

        if (!e.TryGetProperty("delay", out var delay) || delay.ValueKind is not JsonValueKind.Number)
        {
            throw new InvalidSnapshotException("Persisted timer is missing a numeric 'delay'.");
        }

        if (!e.TryGetProperty("event", out var evt) || evt.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidSnapshotException("Persisted timer is missing its 'event'.");
        }

        string? target = null;
        SerializedActorRef? targetActor = null;
        if (!e.TryGetProperty("target", out var t))
        {
            throw new InvalidSnapshotException("Persisted timer is missing its 'target'.");
        }

        switch (t.ValueKind)
        {
            case JsonValueKind.String:
                target = t.GetString() is "self" ? null : t.GetString();
                break;
            case JsonValueKind.Object when SnapshotJson.IsActorRef(t):
                targetActor = t.Deserialize<SerializedActorRef>(options);
                break;
            case JsonValueKind.Object when t.EnumerateObject().Count() == 1 &&
                                           JsonField.String(t, "type") == "parent":
                target = EffectTarget.Parent;
                break;
            default:
                throw new InvalidSnapshotException($"Persisted timer has invalid target {t.GetRawText()}.");
        }

        int? seq = null;
        if (e.TryGetProperty("seq", out var s) && s.ValueKind is not JsonValueKind.Null)
        {
            if (s.ValueKind is not JsonValueKind.Number || !s.TryGetInt32(out var n) || n < 0)
            {
                throw new InvalidSnapshotException($"Persisted timer has invalid seq {s.GetRawText()}.");
            }

            seq = n;
        }

        return new PersistedTimer
        {
            Id = JsonField.String(e, "id"),
            Seq = seq,
            DelayMs = delay.GetDouble(),
            Type = type,
            Event = evt.Deserialize<MachineEvent>(options)!,
            Target = target,
            TargetActor = targetActor,
            ScheduledAt = e.TryGetProperty("scheduledAt", out var at) && at.ValueKind is JsonValueKind.Number
                ? at.GetDouble()
                : null
        };
    }

    public override void Write(Utf8JsonWriter writer, PersistedTimer value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Id is { } id)
        {
            writer.WriteString("id", id);
        }

        if (value.Seq is { } seq)
        {
            writer.WriteNumber("seq", seq);
        }

        writer.WriteNumber("delay", value.DelayMs);
        writer.WriteString("type", value.Type);
        writer.WritePropertyName("event");
        JsonSerializer.Serialize(writer, value.Event, options);
        writer.WritePropertyName("target");
        if (value.TargetActor is { } actor)
        {
            JsonSerializer.Serialize(writer, actor, options);
        }
        else if (value.Target is null)
        {
            writer.WriteStringValue("self");
        }
        else if (value.Target == EffectTarget.Parent)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "parent");
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteStringValue(value.Target);
        }

        if (value.ScheduledAt is { } scheduledAt)
        {
            writer.WriteNumber("scheduledAt", scheduledAt);
        }

        writer.WriteEndObject();
    }
}

/// <summary>Reads/writes <see cref="PersistedSnapshot.HistoryValue"/> as v6 <c>{ [historyId]: [{ "id": … }] }</c>.</summary>
internal sealed class PersistedHistoryConverter : JsonConverter<IReadOnlyDictionary<string, IReadOnlyList<string>>>
{
    public override IReadOnlyDictionary<string, IReadOnlyList<string>> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var e = document.RootElement;
        if (e.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidSnapshotException("Persisted 'historyValue' must be an object.");
        }

        var history = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entry in e.EnumerateObject())
        {
            if (entry.Value.ValueKind is not JsonValueKind.Array)
            {
                throw new InvalidSnapshotException($"Persisted history '{entry.Name}' must be an array.");
            }

            var ids = new List<string>();
            foreach (var item in entry.Value.EnumerateArray())
            {
                ids.Add((item.ValueKind is JsonValueKind.Object ? JsonField.String(item, "id") : null)
                        ?? throw new InvalidSnapshotException(
                            $"Persisted history '{entry.Name}' entries must be {{ \"id\": … }}."));
            }

            history[entry.Name] = ids;
        }

        return history;
    }

    public override void Write(
        Utf8JsonWriter writer, IReadOnlyDictionary<string, IReadOnlyList<string>> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, ids) in value)
        {
            writer.WriteStartArray(key);
            foreach (var id in ids)
            {
                writer.WriteStartObject();
                writer.WriteString("id", id);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}

/// <summary>Strict field readers for the persisted-snapshot converters.</summary>
file static class JsonField
{
    public static string? String(JsonElement e, string name) =>
        !e.TryGetProperty(name, out var v) || v.ValueKind is JsonValueKind.Null ? null
        : v.ValueKind is JsonValueKind.String ? v.GetString()
        : throw new InvalidSnapshotException($"Persisted field '{name}' must be a string, not {v.GetRawText()}.");

    public static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            _ => throw new InvalidSnapshotException($"Persisted field '{name}' must be a boolean, not {v.GetRawText()}.")
        };
}
