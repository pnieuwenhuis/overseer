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

/// <summary>A whole turn of an agent session, for work that only needs how it ends.</summary>
public static class AgentSessionTurns
{
    /// <summary>Sends the agent a message and waits for the turn it starts to end, past the updates it streams meanwhile.</summary>
    /// <exception cref="AgentSessionFailedException">The turn broke off.</exception>
    public static async Task<AgentOutcome> TakeTurnAsync(this IAgentSession session, string message, CancellationToken cancellationToken)
    {
        var turn = await session.SendAsync(message, cancellationToken);
        await foreach (var _ in turn.Updates.WithCancellation(cancellationToken))
        {
        }
        return await turn.Outcome.WaitAsync(cancellationToken);
    }
}

/// <summary>The agent's response to one message.</summary>
public sealed class AgentTurn(IAsyncEnumerable<AgentUpdate> updates, Task<AgentOutcome> outcome)
{
    /// <summary>
    /// What the agent reports while it works, as it happens; the stream ends with the turn, with an
    /// <see cref="AgentSessionFailedException"/> when the turn broke off. Read it once.
    /// </summary>
    public IAsyncEnumerable<AgentUpdate> Updates { get; } = updates;

    /// <summary>How the turn ended; completes when it does.</summary>
    /// <exception cref="AgentSessionFailedException">The turn broke off without an outcome.</exception>
    public Task<AgentOutcome> Outcome { get; } = outcome;
}

/// <summary>One thing an agent reports while it works: more of what it says, or a tool it calls.</summary>
public abstract record AgentUpdate
{
    private AgentUpdate()
    {
    }

    /// <summary>
    /// More of what the agent says. It arrives in fragments, as the agent writes it: the fragments of a turn up to its next new tool
    /// call, or its end, are one message.
    /// </summary>
    /// <param name="Text">Markdown, which may start or end in the middle of a word.</param>
    public sealed record Message(string Text) : AgentUpdate;

    /// <summary>A tool the agent calls. A later one with the same ID in the same turn says more about that call.</summary>
    /// <param name="Id">What tells a turn's tool calls apart.</param>
    /// <param name="Title">The tool and what it is called on, as far as known yet, such as <c>bash: npm test</c>.</param>
    public sealed record ToolCall(string Id, string Title) : AgentUpdate;
}

/// <summary>
/// An agent session broke off in the middle of a turn, as when the agent's process exits or the connection to the Sandbox
/// drops. The message says what happened, to show the user.
/// </summary>
public sealed class AgentSessionFailedException(string message) : Exception(message);

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
