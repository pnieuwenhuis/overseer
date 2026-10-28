using System.Collections.Concurrent;

namespace Overseer.Sandboxes;

/// <summary>Sandboxes that exist only in memory, for offline work on the Portal and orchestration, and for tests.</summary>
public sealed class FakeSandboxProvider(TimeProvider time) : ISandboxProvider
{
    private readonly ConcurrentDictionary<string, Sandbox> _sandboxes = new();
    private readonly ConcurrentDictionary<string, Sequence<CommandResult>> _commands = new();
    private readonly ConcurrentDictionary<AgentRole, Sequence<FakeAgentSession>> _agentSessions = new();
    private readonly ConcurrentQueue<string> _creationFailures = new();

    /// <summary>Makes the next creation fail with <paramref name="error"/>. Call it again to fail the one after as well.</summary>
    public void FailNextCreation(string error) => _creationFailures.Enqueue(error);

    /// <summary>
    /// Makes <paramref name="command"/> give <paramref name="results"/> in turn, across all Sandboxes, repeating the last one.
    /// Unscripted commands succeed without output.
    /// </summary>
    public void ScriptCommand(string command, params CommandResult[] results) => _commands[command] = new Sequence<CommandResult>(results);

    /// <summary>
    /// Makes the next <paramref name="role"/> sessions play <paramref name="sessions"/> in turn, across all Sandboxes, repeating the last one.
    /// Unscripted sessions are done without updates.
    /// </summary>
    public void ScriptAgentSession(AgentRole role, params FakeAgentSession[] sessions) => _agentSessions[role] = new Sequence<FakeAgentSession>(sessions);

    /// <summary>How long creating a Sandbox takes, success or failure, like a VM booting. None by default.</summary>
    public TimeSpan ProvisioningDelay { get; set; } = TimeSpan.Zero;

    public async Task<Sandbox> CreateAsync(SandboxOwner owner, IReadOnlyCollection<string> repositoriesInScope, CancellationToken cancellationToken)
    {
        await Task.Delay(ProvisioningDelay, time, cancellationToken);
        if (_creationFailures.TryDequeue(out var error))
        {
            throw new SandboxCreationFailedException(error);
        }

        var sandbox = new Sandbox($"fake-sandbox-{Guid.NewGuid():N}", owner);
        _sandboxes[sandbox.ResourceId] = sandbox;
        return sandbox;
    }

    public Task<CommandResult> RunCommandAsync(Sandbox sandbox, string command, CancellationToken cancellationToken)
    {
        EnsureExists(sandbox);
        return Task.FromResult(_commands.TryGetValue(command, out var results) ? results.Next() : new CommandResult(0, ""));
    }

    public Task<IAgentSession> StartAgentSessionAsync(Sandbox sandbox, AgentRole role, string prompt, CancellationToken cancellationToken)
    {
        EnsureExists(sandbox);
        var script = _agentSessions.TryGetValue(role, out var sessions) ? sessions.Next() : new FakeAgentSession([], new AgentOutcome.Done());
        return Task.FromResult<IAgentSession>(new ScriptedAgentSession(script));
    }

    public Task DestroyAsync(Sandbox sandbox, CancellationToken cancellationToken)
    {
        _sandboxes.TryRemove(sandbox.ResourceId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Sandbox>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Sandbox>>([.. _sandboxes.Values]);

    private void EnsureExists(Sandbox sandbox)
    {
        if (!_sandboxes.ContainsKey(sandbox.ResourceId))
        {
            throw new InvalidOperationException($"Sandbox {sandbox.ResourceId} does not exist: it was never created or has been destroyed.");
        }
    }

    /// <summary>Scripted values played in order; once they run out, the last one repeats.</summary>
    private sealed class Sequence<T>
    {
        private readonly IReadOnlyList<T> _values;
        private int _played;

        public Sequence(IReadOnlyList<T> values)
        {
            ArgumentOutOfRangeException.ThrowIfZero(values.Count, nameof(values));
            _values = values;
        }

        public T Next()
        {
            lock (_values)
            {
                return _values[Math.Min(_played++, _values.Count - 1)];
            }
        }
    }

    private sealed class ScriptedAgentSession(FakeAgentSession script) : IAgentSession
    {
        public IAsyncEnumerable<string> Updates { get; } = script.Updates.ToAsyncEnumerable();

        public Task<AgentOutcome> Outcome { get; } = Task.FromResult(script.Outcome);
    }
}

/// <summary>A scripted agent session: the updates it streams and how it ends.</summary>
public sealed record FakeAgentSession(IReadOnlyList<string> Updates, AgentOutcome Outcome);
