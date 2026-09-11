using System.Text.Json;

namespace XState.Json;

/// <summary>
/// Imports an XState v6 JSON machine config (the shape described by <c>machine.schema.json</c>).
/// <code>
/// var machine = MachineConfig.FromJson("""
/// {
///   "id": "toggle",
///   "initial": "inactive",
///   "states": {
///     "inactive": { "on": { "TOGGLE": "active" } },
///     "active": { "on": { "TOGGLE": "inactive" } }
///   }
/// }
/// """);
/// </code>
/// Guards, actions, invoked actors and named delays are referenced by name and supplied via
/// <see cref="MachineImplementations{TContext}"/>; any name without an implementation is
/// reported (all of them at once) as an <see cref="InvalidOperationException"/>.
/// <para>
/// The config is held to the v6 schema: an unknown key throws a <see cref="JsonException"/>
/// (relax it with <see cref="JsonConfigOptions.IgnoreUnknownKeys"/>), and a key the schema defines
/// but this port cannot honour throws a <see cref="NotSupportedException"/>.
/// </para>
/// </summary>
public static class MachineConfig
{
    /// <summary>
    /// Imports a config whose context is the raw <c>context</c> JSON. If the actor is started
    /// with input, the input is used as the context instead (when it is a <see cref="JsonElement"/>).
    /// </summary>
    public static StateMachine<JsonElement> FromJson(
        string json,
        MachineImplementations<JsonElement>? implementations = null,
        JsonConfigOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        var factory = implementations?.ContextFactory;
        return new JsonMachineReader<JsonElement>(implementations, options).Read(
            json,
            context => factory is null
                ? input => input is JsonElement element ? element : context
                : input => factory(input ?? context));
    }

    /// <summary>
    /// Imports a config into a machine with a typed context.
    /// <paramref name="implementations"/> must supply <see cref="MachineImplementations{TContext}.ContextFactory"/>,
    /// which receives the actor's input, or — when there is none — the config's <c>context</c>
    /// value as a <see cref="JsonElement"/>.
    /// </summary>
    public static StateMachine<TContext> FromJson<TContext>(
        string json,
        MachineImplementations<TContext> implementations,
        JsonConfigOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(implementations);

        var factory = implementations.ContextFactory
                      ?? throw new InvalidOperationException(
                          $"FromJson<{typeof(TContext).Name}> requires implementations.Context(...) to build " +
                          "the machine context from the config's `context` JSON.");

        return new JsonMachineReader<TContext>(implementations, options).Read(
            json,
            context => input => factory(input ?? context));
    }

    /// <summary>
    /// The machine's JSON definition (xstate v6 <c>serializeMachine</c>). A machine imported by
    /// <see cref="FromJson(string, MachineImplementations{JsonElement}?, JsonConfigOptions?)"/>
    /// returns its own source config verbatim — the round trip is lossless. A machine built with
    /// the fluent builder is walked best-effort: structure, names and static values survive;
    /// everything that is a delegate (an inline guard or action, an output or input resolver, a
    /// choice function) becomes a <c>{ "@code": "&lt;opaque&gt;" }</c> marker, exactly as v6 marks
    /// functions.
    /// </summary>
    public static JsonElement ToJson<TContext>(StateMachine<TContext> machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return machine.SourceJson ?? MachineJsonWriter.Write(machine);
    }

    /// <summary>The machine's JSON definition as text; see <see cref="ToJson{TContext}"/>.</summary>
    public static string ToJsonString<TContext>(StateMachine<TContext> machine, bool indented = false) =>
        JsonSerializer.Serialize(ToJson(machine), new JsonSerializerOptions { WriteIndented = indented });
}
