using System.Text.Json;

namespace XState;

public sealed partial class StateMachine<TContext>
{
    /// <summary>
    /// The JSON config this machine was imported from, when it was built by
    /// <see cref="Json.MachineConfig.FromJson(string, Json.MachineImplementations{JsonElement}?, Json.JsonConfigOptions?)"/>.
    /// <see cref="Json.MachineConfig.ToJson{T}"/> returns it verbatim, so an imported machine
    /// round-trips losslessly (xstate v6 <c>serializeMachine</c>'s <c>_json</c> path). Null for a
    /// machine built with the fluent builder — that one is exported best-effort from the model.
    /// </summary>
    public JsonElement? SourceJson { get; internal set; }
}
