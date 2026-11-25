using System.Collections.Concurrent;
using Overseer.GitHub;

namespace Overseer.Sandboxes;

/// <summary>Sandboxes that exist only in memory, for offline work on the Portal and orchestration, and for tests.</summary>
public sealed class FakeSandboxProvider(TimeProvider time) : ISandboxProvider
{
    private readonly ConcurrentDictionary<string, Sandbox> _sandboxes = new();
    private readonly ConcurrentDictionary<string, SandboxMachine> _machines = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _repositoriesInScope = new();
    private readonly ConcurrentDictionary<string, bool> _mayPush = new();
    private readonly ConcurrentDictionary<string, string> _gitHubTokens = new();
    private readonly ConcurrentDictionary<string, Sequence<CommandResult>> _commands = new();
    private readonly ConcurrentDictionary<AgentRole, Sequence<FakeAgentSession>> _agentSessions = new();
    private readonly ConcurrentQueue<string> _creationFailures = new();
    private readonly ConcurrentQueue<string> _destructionFailures = new();
    private readonly ConcurrentQueue<string> _listingFailures = new();
    private readonly ConcurrentQueue<AgentSessionStart> _agentSessionsStarted = new();
    private readonly ConcurrentQueue<AgentMessage> _messagesSent = new();
    private readonly ConcurrentQueue<AgentSessionStart> _agentSessionsStopped = new();
    private readonly ConcurrentDictionary<(string ResourceId, string Path), string> _files = new();
    private readonly ConcurrentDictionary<(string Repository, string Path), string> _repositoryFiles = new();
    private readonly ConcurrentQueue<SandboxCommand> _commandsRun = new();
    private readonly ConcurrentQueue<(string Fragment, Sequence<CommandResult> Results)> _commandsContaining = new();
    private readonly ConcurrentQueue<(string Fragment, Task Until)> _heldCommands = new();
    private readonly ConcurrentDictionary<string, string> _unreadableFiles = new();
    private readonly ConcurrentQueue<Sandbox> _destructionsBegun = new();
    private volatile Task _destructionsHeld = Task.CompletedTask;

    /// <summary>Makes the next creation fail with <paramref name="error"/>. Call it again to fail the one after as well.</summary>
    public void FailNextCreation(string error) => _creationFailures.Enqueue(error);

    /// <summary>
    /// Makes the next destruction fail with <paramref name="error"/>, leaving the Sandbox as it is. Call it again to fail
    /// the one after as well.
    /// </summary>
    public void FailNextDestruction(string error) => _destructionFailures.Enqueue(error);

    /// <summary>
    /// Makes every destruction from now on finish only once <paramref name="until"/> completes, like a VM being deleted, so that a
    /// test can act meanwhile.
    /// </summary>
    public void HoldDestructions(Task until) => _destructionsHeld = until;

    /// <summary>Makes the next <see cref="ListAsync"/> fail with <paramref name="error"/>.</summary>
    public void FailNextListing(string error) => _listingFailures.Enqueue(error);

    /// <summary>Every Sandbox a destruction was begun of, in the order they were begun, whether or not it succeeded or has ended.</summary>
    public IReadOnlyList<Sandbox> DestructionsBegun => [.. _destructionsBegun];

    /// <summary>
    /// Makes <paramref name="command"/> give <paramref name="results"/> in turn, across all Sandboxes, repeating the last one.
    /// Unscripted commands succeed without output.
    /// </summary>
    public void ScriptCommand(string command, params CommandResult[] results) => _commands[command] = new Sequence<CommandResult>(results);

    /// <summary>
    /// Makes every command that holds <paramref name="fragment"/>, and isn't scripted as a whole, give <paramref name="results"/> in
    /// turn, across all Sandboxes, repeating the last one: for a command whose exact text doesn't matter, such as one that wraps a
    /// Repository Manifest's. The latest script that matches applies.
    /// </summary>
    public void ScriptCommandsContaining(string fragment, params CommandResult[] results) =>
        _commandsContaining.Enqueue((fragment, new Sequence<CommandResult>(results)));

    /// <summary>
    /// Makes every command that holds <paramref name="fragment"/> finish only once <paramref name="until"/> completes, so that a test
    /// can act while it runs, as a long setup does.
    /// </summary>
    public void HoldCommandsContaining(string fragment, Task until) => _heldCommands.Enqueue((fragment, until));

    /// <summary>
    /// Gives <paramref name="repository"/> a file, at <paramref name="path"/> from its root: every Sandbox created from now on that
    /// holds the repository has it in its clone.
    /// </summary>
    public void AddRepositoryFile(string repository, string path, string content) => _repositoryFiles[(repository, path)] = content;

    /// <summary>Makes reading the file at <paramref name="path"/> in a repository's clone fail with <paramref name="error"/>.</summary>
    /// <param name="path">An absolute path in a Sandbox.</param>
    public void FailReading(string path, string error) => _unreadableFiles[path] = error;

    /// <summary>Every command run in a Sandbox, in the order they were run.</summary>
    public IReadOnlyList<SandboxCommand> CommandsRun => [.. _commandsRun];

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

    /// <summary>The agent sessions stopped so far, in the order they were stopped, each as it was started.</summary>
    public IReadOnlyList<AgentSessionStart> AgentSessionsStopped => [.. _agentSessionsStopped];

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

    /// <summary>Whether the Sandbox was created with push access, also after it has been destroyed.</summary>
    /// <exception cref="InvalidOperationException">This provider never created the Sandbox.</exception>
    public bool MayPushOf(Sandbox sandbox) =>
        _mayPush.TryGetValue(sandbox.ResourceId, out var mayPush)
            ? mayPush
            : throw new InvalidOperationException($"Sandbox {sandbox.ResourceId} was never created.");

    /// <summary>
    /// The GitHub token the Sandbox was last given (<see cref="SetGitHubTokenAsync"/>), also after it has been destroyed;
    /// <c>null</c> when it was given none, as the fake clones without one.
    /// </summary>
    public string? GitHubTokenOf(Sandbox sandbox) => _gitHubTokens.GetValueOrDefault(sandbox.ResourceId);

    public Task SetGitHubTokenAsync(Sandbox sandbox, string token, CancellationToken cancellationToken)
    {
        EnsureExists(sandbox);
        if (!_mayPush[sandbox.ResourceId])
        {
            throw new InvalidOperationException($"Sandbox {sandbox.ResourceId} has no push access, so it gets no GitHub token.");
        }
        _gitHubTokens[sandbox.ResourceId] = token;
        return Task.CompletedTask;
    }

    public Sandbox Name(SandboxOwner owner) => new($"fake-sandbox-{Guid.NewGuid():N}", owner);

    public async Task CreateAsync(
        Sandbox sandbox, SandboxMachine machine, IReadOnlyCollection<string> repositoriesInScope, bool mayPush, CancellationToken cancellationToken)
    {
        await Task.Delay(ProvisioningDelay, time, cancellationToken);
        if (_creationFailures.TryDequeue(out var error))
        {
            throw new SandboxCreationFailedException(error);
        }

        _machines[sandbox.ResourceId] = machine;
        _repositoriesInScope[sandbox.ResourceId] = [.. repositoriesInScope];
        _mayPush[sandbox.ResourceId] = mayPush;
        foreach (var ((repository, path), content) in _repositoryFiles)
        {
            if (repositoriesInScope.Contains(repository, StringComparer.OrdinalIgnoreCase))
            {
                _files[(sandbox.ResourceId, $"{Sandbox.CloneDirectory(RepositoryName.Parse(repository))}/{path}")] = content;
            }
        }
        _sandboxes[sandbox.ResourceId] = sandbox;
    }

    public async Task<CommandResult> RunCommandAsync(Sandbox sandbox, string command, CancellationToken cancellationToken)
    {
        EnsureExists(sandbox);
        _commandsRun.Enqueue(new SandboxCommand(sandbox, command));
        foreach (var (_, until) in _heldCommands.Where(held => command.Contains(held.Fragment, StringComparison.Ordinal)))
        {
            await until.WaitAsync(cancellationToken);
        }
        if (_commands.TryGetValue(command, out var results))
        {
            return results.Next();
        }
        return _commandsContaining.LastOrDefault(script => command.Contains(script.Fragment, StringComparison.Ordinal)).Results?.Next()
            ?? new CommandResult(0, "");
    }

    public Task<string?> ReadFileAsync(Sandbox sandbox, string path, CancellationToken cancellationToken)
    {
        EnsureExists(sandbox);
        if (_unreadableFiles.TryGetValue(path, out var error))
        {
            throw new InvalidOperationException(error);
        }
        return Task.FromResult(_files.GetValueOrDefault((sandbox.ResourceId, path)));
    }

    /// <summary>Writes a file in the Sandbox, replacing any at that path, as an agent or a command would.</summary>
    /// <exception cref="InvalidOperationException">The Sandbox doesn't exist.</exception>
    public void WriteFile(Sandbox sandbox, string path, string content)
    {
        EnsureExists(sandbox);
        _files[(sandbox.ResourceId, path)] = content;
    }

    public Task<string?> TakeFileAsync(Sandbox sandbox, string path, CancellationToken cancellationToken)
    {
        EnsureExists(sandbox);
        return Task.FromResult(_files.TryRemove((sandbox.ResourceId, path), out var content) ? content : null);
    }

    public Task<IAgentSession> StartAgentSessionAsync(
        Sandbox sandbox, AgentRole role, AgentHarnessConfiguration harness, IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        EnsureExists(sandbox);
        var script = _agentSessions.TryGetValue(role, out var sessions) ? sessions.Next() : new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done()));
        if (script.StartFailure is { } failure)
        {
            throw new InvalidOperationException(failure);
        }
        var start = new AgentSessionStart(sandbox, role, harness.Json, harness.ModelOf(role), new Dictionary<string, string>(environment));
        _agentSessionsStarted.Enqueue(start);
        return Task.FromResult<IAgentSession>(new ScriptedAgentSession(
            script, message => _messagesSent.Enqueue(new AgentMessage(sandbox, role, message)), (path, content) => WriteFile(sandbox, path, content),
            () => _agentSessionsStopped.Enqueue(start)));
    }

    public async Task DestroyAsync(Sandbox sandbox, CancellationToken cancellationToken)
    {
        _destructionsBegun.Enqueue(sandbox);
        await _destructionsHeld.WaitAsync(cancellationToken);
        if (_destructionFailures.TryDequeue(out var error))
        {
            throw new InvalidOperationException(error);
        }
        _sandboxes.TryRemove(sandbox.ResourceId, out _);
        foreach (var file in _files.Keys.Where(file => file.ResourceId == sandbox.ResourceId))
        {
            _files.TryRemove(file, out _);
        }
    }

    public Task<IReadOnlyList<Sandbox>> ListAsync(CancellationToken cancellationToken) =>
        _listingFailures.TryDequeue(out var error)
            ? throw new InvalidOperationException(error)
            : Task.FromResult<IReadOnlyList<Sandbox>>([.. _sandboxes.Values]);

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
    private sealed class ScriptedAgentSession(
        FakeAgentSession script, Action<string> received, Action<string, string> write, Action stopped) : IAgentSession
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
            foreach (var (path, content) in turn.Writes)
            {
                write(path, content);
            }
            var outcome = turn switch
            {
                { EndsNever: true } => _stopping.Task,
                { Failure: { } failure } => Task.FromException<AgentOutcome>(new AgentSessionFailedException(failure)),
                { EndsAfter: { } gate } => gate.ContinueWith(_ => turn.Outcome, TaskScheduler.Default),
                _ => Task.FromResult(turn.Outcome),
            };
            return Task.FromResult(new AgentTurn(turn.Updates.ToAsyncEnumerable(), outcome));
        }

        public ValueTask DisposeAsync()
        {
            if (!_stopped)
            {
                stopped();
            }
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

/// <summary>A shell command run in <paramref name="Sandbox"/>.</summary>
public sealed record SandboxCommand(Sandbox Sandbox, string Command);

/// <summary>A message sent to an agent session of <paramref name="Role"/> in <paramref name="Sandbox"/>.</summary>
public sealed record AgentMessage(Sandbox Sandbox, AgentRole Role, string Message);

/// <summary>A scripted agent session: its turns in order, the last one repeating.</summary>
public sealed record FakeAgentSession(params FakeAgentTurn[] Turns)
{
    /// <summary>When set, the session doesn't start: starting it fails with this error, and it plays no turns.</summary>
    public string? StartFailure { get; init; }
}

/// <summary>A scripted turn: the updates the agent streams and how the turn ends.</summary>
public sealed record FakeAgentTurn(IReadOnlyList<string> Updates, AgentOutcome Outcome)
{
    /// <summary>When set, the turn breaks off after its updates with this error, instead of ending with its outcome.</summary>
    public string? Failure { get; init; }

    /// <summary>Whether the turn goes on until the session is stopped, which breaks it off.</summary>
    public bool EndsNever { get; init; }

    /// <summary>When set, the turn ends with its outcome only once this completes, so that a test can act while the agent works.</summary>
    public Task? EndsAfter { get; init; }

    /// <summary>The files the agent writes in its Sandbox during the turn, by absolute path, such as what it hands in.</summary>
    public IReadOnlyDictionary<string, string> Writes { get; init; } = new Dictionary<string, string>();

    /// <summary>A turn that streams <paramref name="updates"/>, then goes on until the session is stopped.</summary>
    public static FakeAgentTurn Endless(params string[] updates) => new(updates, new AgentOutcome.Done()) { EndsNever = true };
}
