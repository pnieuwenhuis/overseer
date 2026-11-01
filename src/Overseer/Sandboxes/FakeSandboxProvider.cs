using System.Collections.Concurrent;

namespace Overseer.Sandboxes;

/// <summary>Sandboxes that exist only in memory, for offline work on the Portal and orchestration, and for tests.</summary>
public sealed class FakeSandboxProvider(TimeProvider time) : ISandboxProvider
{
    private readonly ConcurrentDictionary<string, Sandbox> _sandboxes = new();
    private readonly ConcurrentDictionary<string, SandboxMachine> _machines = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _repositoriesInScope = new();
    private readonly ConcurrentDictionary<string, Sequence<CommandResult>> _commands = new();
    private readonly ConcurrentDictionary<AgentRole, Sequence<FakeAgentSession>> _agentSessions = new();
    private readonly ConcurrentQueue<string> _creationFailures = new();
    private readonly ConcurrentQueue<string> _destructionFailures = new();
    private readonly ConcurrentQueue<AgentSessionStart> _agentSessionsStarted = new();
    private readonly ConcurrentQueue<AgentMessage> _messagesSent = new();

    /// <summary>Makes the next creation fail with <paramref name="error"/>. Call it again to fail the one after as well.</summary>
    public void FailNextCreation(string error) => _creationFailures.Enqueue(error);

    /// <summary>
    /// Makes the next destruction fail with <paramref name="error"/>, leaving the Sandbox as it is. Call it again to fail
    /// the one after as well.
    /// </summary>
    public void FailNextDestruction(string error) => _destructionFailures.Enqueue(error);

    /// <summary>
    /// Makes <paramref name="command"/> give <paramref name="results"/> in turn, across all Sandboxes, repeating the last one.
    /// Unscripted commands succeed without output.
    /// </summary>
    public void ScriptCommand(string command, params CommandResult[] results) => _commands[command] = new Sequence<CommandResult>(results);

    /// <summary>
    /// Makes the next <paramref name="role"/> sessions play <paramref name="sessions"/> in turn, across all Sandboxes, repeating the last one.
    /// In an unscripted session, every turn is done without updates.
    /// </summary>
    public void ScriptAgentSessions(AgentRole role, params FakeAgentSession[] sessions) => _agentSessions[role] = new Sequence<FakeAgentSession>(sessions);

    /// <summary>How long creating a Sandbox takes, success or failure, like a VM booting. None by default.</summary>
    public TimeSpan ProvisioningDelay { get; set; } = TimeSpan.Zero;

    /// <summary>What each agent session was started with, in the order they started, also after they stopped.</summary>
    public IReadOnlyList<AgentSessionStart> AgentSessionsStarted => [.. _agentSessionsStarted];

    /// <summary>Every message sent to an agent session, in the order they were sent.</summary>
    public IReadOnlyList<AgentMessage> MessagesSent => [.. _messagesSent];

    /// <summary>The machine the Sandbox was created on, also after it has been destroyed.</summary>
    /// <exception cref="InvalidOperationException">This provider never created the Sandbox.</exception>
    public SandboxMachine MachineOf(Sandbox sandbox) =>
        _machines.TryGetValue(sandbox.ResourceId, out var machine)
            ? machine
            : throw new InvalidOperationException($"Sandbox {sandbox.ResourceId} was never created.");

    /// <summary>The Repositories in scope the Sandbox was created with, also after it has been destroyed.</summary>
    /// <exception cref="InvalidOperationException">This provider never created the Sandbox.</exception>
    public IReadOnlyList<string> RepositoriesInScopeOf(Sandbox sandbox) =>
        _repositoriesInScope.TryGetValue(sandbox.ResourceId, out var repositories)
            ? repositories
            : throw new InvalidOperationException($"Sandbox {sandbox.ResourceId} was never created.");

    public async Task<Sandbox> CreateAsync(
        SandboxOwner owner, SandboxMachine machine, IReadOnlyCollection<string> repositoriesInScope, CancellationToken cancellationToken)
    {
        await Task.Delay(ProvisioningDelay, time, cancellationToken);
        if (_creationFailures.TryDequeue(out var error))
        {
            throw new SandboxCreationFailedException(error);
        }

        var sandbox = new Sandbox($"fake-sandbox-{Guid.NewGuid():N}", owner);
        _machines[sandbox.ResourceId] = machine;
        _repositoriesInScope[sandbox.ResourceId] = [.. repositoriesInScope];
        _sandboxes[sandbox.ResourceId] = sandbox;
        return sandbox;
    }

    public Task<CommandResult> RunCommandAsync(Sandbox sandbox, string command, CancellationToken cancellationToken)
    {
        EnsureExists(sandbox);
        return Task.FromResult(_commands.TryGetValue(command, out var results) ? results.Next() : new CommandResult(0, ""));
    }

    public Task<IAgentSession> StartAgentSessionAsync(
        Sandbox sandbox, AgentRole role, AgentHarnessConfiguration harness, IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        EnsureExists(sandbox);
        _agentSessionsStarted.Enqueue(new AgentSessionStart(sandbox, role, harness.Json, harness.ModelOf(role), new Dictionary<string, string>(environment)));
        var script = _agentSessions.TryGetValue(role, out var sessions) ? sessions.Next() : new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done()));
        return Task.FromResult<IAgentSession>(new ScriptedAgentSession(script, message => _messagesSent.Enqueue(new AgentMessage(sandbox, role, message))));
    }

    public Task DestroyAsync(Sandbox sandbox, CancellationToken cancellationToken)
    {
        if (_destructionFailures.TryDequeue(out var error))
        {
            throw new InvalidOperationException(error);
        }
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

    /// <summary>Plays one turn per message, whatever the message says.</summary>
    private sealed class ScriptedAgentSession(FakeAgentSession script, Action<string> received) : IAgentSession
    {
        private readonly Sequence<FakeAgentTurn> _turns = new(script.Turns);
        private bool _stopped;

        // Ends the turns that never end by themselves, once the session stops.
        private readonly TaskCompletionSource<AgentOutcome> _stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AgentTurn> SendAsync(string message, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            received(message);
            var turn = _turns.Next();
            var outcome = turn switch
            {
                { EndsNever: true } => _stopping.Task,
                { Failure: { } failure } => Task.FromException<AgentOutcome>(new AgentSessionFailedException(failure)),
                _ => Task.FromResult(turn.Outcome),
            };
            return Task.FromResult(new AgentTurn(turn.Updates.ToAsyncEnumerable(), outcome));
        }

        public ValueTask DisposeAsync()
        {
            _stopped = true;
            _stopping.TrySetException(new AgentSessionFailedException("The agent session was stopped."));
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>An agent session the fake started, with what a real one would hand the agent.</summary>
/// <param name="HarnessJson">The <c>opencode.json</c> the agent runs with.</param>
/// <param name="Model">The model of the role's agent in that configuration.</param>
/// <param name="Environment">The environment variables the agent runs with, such as model provider tokens.</param>
public sealed record AgentSessionStart(
    Sandbox Sandbox, AgentRole Role, string HarnessJson, string Model, IReadOnlyDictionary<string, string> Environment);

/// <summary>A message sent to an agent session of <paramref name="Role"/> in <paramref name="Sandbox"/>.</summary>
public sealed record AgentMessage(Sandbox Sandbox, AgentRole Role, string Message);

/// <summary>A scripted agent session: its turns in order, the last one repeating.</summary>
public sealed record FakeAgentSession(params FakeAgentTurn[] Turns);

/// <summary>A scripted turn: the updates the agent streams and how the turn ends.</summary>
public sealed record FakeAgentTurn(IReadOnlyList<string> Updates, AgentOutcome Outcome)
{
    /// <summary>When set, the turn breaks off after its updates with this error, instead of ending with its outcome.</summary>
    public string? Failure { get; init; }

    /// <summary>Whether the turn goes on until the session is stopped, which breaks it off.</summary>
    public bool EndsNever { get; init; }

    /// <summary>A turn that streams <paramref name="updates"/>, then goes on until the session is stopped.</summary>
    public static FakeAgentTurn Endless(params string[] updates) => new(updates, new AgentOutcome.Done()) { EndsNever = true };
}
