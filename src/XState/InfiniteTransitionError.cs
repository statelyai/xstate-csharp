using System.Text.Json;

namespace XState;

/// <summary>
/// Thrown when one macrostep takes more microsteps than the machine's
/// <see cref="StateMachine{TContext}.MaxMicrosteps"/> (default 1000) without reaching a stable
/// state, usually because eventless transitions or raised events form a cycle
/// (xstate v6 <c>InfiniteTransitionError</c>).
/// </summary>
public sealed class InfiniteTransitionError : InvalidOperationException
{
    /// <summary>The machine id, or <c>"the machine"</c> when it has none.</summary>
    public string ActorId { get; }

    /// <summary>The event being processed.</summary>
    public MachineEvent Event { get; }

    /// <summary>The microstep bound that was exceeded.</summary>
    public int MaxIterations { get; }

    /// <summary>The last (up to 5) state values visited, oldest first.</summary>
    public IReadOnlyList<object> States { get; }

    public InfiniteTransitionError(string actorId, MachineEvent @event, int maxIterations, IEnumerable<object> states)
        : this(actorId.Length > 0 ? actorId : "the machine", @event, maxIterations, states.ToArray())
    {
    }

    private InfiniteTransitionError(string actorId, MachineEvent @event, int maxIterations, object[] states)
        : base(
            $"Infinite loop detected in actor \"{actorId}\" processing event \"{@event.Type}\": more than " +
            $"{maxIterations} microsteps without reaching a stable state. Last states: " +
            $"{string.Join(" -> ", states.Select(v => JsonSerializer.Serialize(v)))}. Check for a cycle of " +
            "eventless transitions or raised events, or raise the bound with MaxMicrosteps.")
    {
        ActorId = actorId;
        Event = @event;
        MaxIterations = maxIterations;
        States = states;
    }
}
