namespace Overseer.Sandboxes;

/// <summary>One agent working in a Sandbox on one prompt, from start to its outcome.</summary>
public interface IAgentSession
{
    /// <summary>What the agent reports while it works, as it happens; the stream ends with the session. Read it once.</summary>
    IAsyncEnumerable<string> Updates { get; }

    /// <summary>How the session ended; completes when it does.</summary>
    Task<AgentOutcome> Outcome { get; }
}

public enum AgentRole
{
    Analyst,
    Designer,
    Developer,
    Reviewer,
}

/// <summary>How an agent session ended.</summary>
public abstract record AgentOutcome
{
    private AgentOutcome()
    {
    }

    /// <summary>The agent finished its work.</summary>
    public sealed record Done : AgentOutcome;

    /// <summary>The agent stopped to ask the user something; the work continues in a new session once they answer.</summary>
    public sealed record AskedQuestion(string Question) : AgentOutcome;
}
