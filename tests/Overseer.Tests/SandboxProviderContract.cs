using Overseer.Sandboxes;

namespace Overseer.Tests;

/// <summary>
/// What every Sandbox Provider must do. Derive from this once per implementation. Each test owns its Sandboxes
/// through a fresh owner ID and destroys them afterwards, so the suite can share a real subscription with others.
/// </summary>
public abstract class SandboxProviderContract : IAsyncDisposable
{
    private readonly List<Sandbox> _created = [];

    protected abstract ISandboxProvider Provider { get; }

    [Theory]
    [InlineData(SandboxOwnerKind.Intent)]
    [InlineData(SandboxOwnerKind.RepositorySetup)]
    public async Task A_created_Sandbox_is_listed_with_its_owner(SandboxOwnerKind kind)
    {
        var owner = new SandboxOwner(kind, Guid.NewGuid());

        var sandbox = await CreateAsync(owner);

        var listed = await Provider.ListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(new Sandbox(sandbox.ResourceId, owner), listed);
    }

    [Fact]
    public async Task Each_Sandbox_gets_its_own_resource_identifier()
    {
        var first = await CreateAsync(NewIntent());
        var second = await CreateAsync(NewIntent());

        Assert.NotEqual(first.ResourceId, second.ResourceId);
    }

    [Fact]
    public async Task A_destroyed_Sandbox_is_no_longer_listed()
    {
        var destroyed = await CreateAsync(NewIntent());
        var kept = await CreateAsync(NewIntent());

        await DestroyAsync(destroyed);

        var listed = await Provider.ListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(destroyed, listed);
        Assert.Contains(kept, listed);
    }

    [Fact]
    public async Task A_command_returns_its_exit_code_and_output()
    {
        var sandbox = await CreateAsync(NewIntent());
        GivenCommand("echo hello", new CommandResult(0, "hello\n"));
        GivenCommand("exit 3", new CommandResult(3, ""));

        var succeeded = await Provider.RunCommandAsync(sandbox, "echo hello", TestContext.Current.CancellationToken);
        var failed = await Provider.RunCommandAsync(sandbox, "exit 3", TestContext.Current.CancellationToken);

        Assert.Equal(0, succeeded.ExitCode);
        Assert.Equal("hello", succeeded.Output.Trim());
        Assert.Equal(3, failed.ExitCode);
    }

    [Fact]
    public async Task An_agent_session_streams_updates_and_ends_with_its_outcome()
    {
        var sandbox = await CreateAsync(NewIntent());
        const string prompt = "Reply with the word READY, then finish without changing anything.";
        GivenAgentSession(AgentRole.Developer, ["READY"], new AgentOutcome.Done());

        var session = await Provider.StartAgentSessionAsync(sandbox, AgentRole.Developer, prompt, TestContext.Current.CancellationToken);

        var updates = await session.Updates.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(updates, update => update.Contains("READY"));
        Assert.Equal(new AgentOutcome.Done(), await session.Outcome);
    }

    /// <summary>
    /// Makes a scriptable provider run the next <paramref name="role"/> session as a real agent would answer the prompt.
    /// A real provider needs nothing: the agent really answers.
    /// </summary>
    protected virtual void GivenAgentSession(AgentRole role, IReadOnlyList<string> updates, AgentOutcome outcome)
    {
    }

    /// <summary>
    /// Makes a scriptable provider answer <paramref name="command"/> the way a real machine does.
    /// A real provider needs nothing: the command really behaves that way.
    /// </summary>
    protected virtual void GivenCommand(string command, CommandResult result)
    {
    }

    private static SandboxOwner NewIntent() => new(SandboxOwnerKind.Intent, Guid.NewGuid());

    private async Task DestroyAsync(Sandbox sandbox)
    {
        await Provider.DestroyAsync(sandbox, TestContext.Current.CancellationToken);
        _created.Remove(sandbox);
    }

    private async Task<Sandbox> CreateAsync(SandboxOwner owner)
    {
        var sandbox = await Provider.CreateAsync(owner, ["pnieuwenhuis/overseer"], TestContext.Current.CancellationToken);
        _created.Add(sandbox);
        return sandbox;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sandbox in _created)
        {
            await Provider.DestroyAsync(sandbox, CancellationToken.None);
        }
        GC.SuppressFinalize(this);
    }
}

public sealed class FakeSandboxProviderContract : SandboxProviderContract
{
    private readonly FakeSandboxProvider _fake = new(TimeProvider.System);

    protected override ISandboxProvider Provider => _fake;

    protected override void GivenCommand(string command, CommandResult result) => _fake.ScriptCommand(command, result);

    protected override void GivenAgentSession(AgentRole role, IReadOnlyList<string> updates, AgentOutcome outcome) =>
        _fake.ScriptAgentSession(role, new FakeAgentSession(updates, outcome));
}
