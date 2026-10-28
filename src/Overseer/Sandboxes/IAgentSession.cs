namespace Overseer.Sandboxes;

/// <summary>
/// One agent working in a Sandbox in one role, over as many turns as the conversation needs: each message to the
/// agent starts a turn, which ends with an outcome. Disposing it stops the agent, also in the middle of a turn.
/// </summary>
public interface IAgentSession : IAsyncDisposable
{
    /// <summary>
    /// Sends the agent a message (its work to start with, then the user's answers) and starts the agent's turn.
    /// Wait for a turn's outcome before sending the next message.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The session has been stopped.</exception>
    Task<AgentTurn> SendAsync(string message, CancellationToken cancellationToken);
}

/// <summary>The agent's response to one message.</summary>
public sealed class AgentTurn(IAsyncEnumerable<string> updates, Task<AgentOutcome> outcome)
{
    /// <summary>What the agent reports while it works, as it happens; the stream ends with the turn. Read it once.</summary>
    public IAsyncEnumerable<string> Updates { get; } = updates;

    /// <summary>How the turn ended; completes when it does.</summary>
    public Task<AgentOutcome> Outcome { get; } = outcome;
}

public enum AgentRole
{
    Analyst,
    Designer,
    Developer,
    Reviewer,
}

/// <summary>How an agent's turn ended.</summary>
public abstract record AgentOutcome
{
    private AgentOutcome()
    {
    }

    /// <summary>The agent finished its work.</summary>
    public sealed record Done : AgentOutcome;

    /// <summary>The agent asks the user something; send their answer to the same session to continue.</summary>
    public sealed record AskedQuestion(string Question) : AgentOutcome;
}
