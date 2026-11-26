using Overseer.Sandboxes;

namespace Overseer.Tests;

/// <summary>Playing an agent whose turns end as done.</summary>
public static class AgentTurns
{
    /// <summary>A turn the agent ends as done, once <paramref name="after"/> completes when there is one.</summary>
    /// <param name="updates">What the turn streams before it ends.</param>
    public static FakeAgentTurn Done(Task? after = null, params AgentUpdate[] updates) =>
        new(updates, new AgentOutcome.Done()) { EndsAfter = after };

    /// <summary>A session whose turn ends as done once <paramref name="until"/> completes.</summary>
    public static FakeAgentSession DoneOnce(Task until) => new(Done(after: until));

    /// <summary>A session whose every turn ends as done at once.</summary>
    public static FakeAgentSession DoneAtOnce() => new(Done());
}
