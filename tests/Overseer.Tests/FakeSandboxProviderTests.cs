using Microsoft.Extensions.Time.Testing;
using Overseer.Sandboxes;

namespace Overseer.Tests;

/// <summary>Scripting the fake Sandbox Provider, as orchestration tests and offline work will.</summary>
public class FakeSandboxProviderTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeSandboxProvider _provider;

    public FakeSandboxProviderTests() => _provider = new FakeSandboxProvider(_time);

    [Fact]
    public async Task A_command_can_fail_twice_and_then_pass()
    {
        var sandbox = await CreateAsync();
        _provider.ScriptCommand("dotnet test",
            new CommandResult(1, "2 tests failed"),
            new CommandResult(1, "1 test failed"),
            new CommandResult(0, "All tests passed"));

        var results = new List<CommandResult>();
        for (var run = 0; run < 4; run++)
        {
            results.Add(await _provider.RunCommandAsync(sandbox, "dotnet test", TestContext.Current.CancellationToken));
        }

        Assert.Equal(
            [
                new CommandResult(1, "2 tests failed"),
                new CommandResult(1, "1 test failed"),
                new CommandResult(0, "All tests passed"),
                new CommandResult(0, "All tests passed"),
            ],
            results);
    }

    [Fact]
    public async Task An_agent_session_plays_its_turns_in_order()
    {
        var sandbox = await CreateAsync();
        _provider.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(
            new FakeAgentTurn(["Reading the README"], new AgentOutcome.AskedQuestion("Which database should it use?")),
            new FakeAgentTurn(["Writing the Spec"], new AgentOutcome.Done())));
        await using var session = await _provider.StartAgentSessionAsync(sandbox, AgentRole.Analyst, TestContext.Current.CancellationToken);

        var asking = await session.SendAsync("Refine the Intent", TestContext.Current.CancellationToken);
        Assert.Equal(["Reading the README"], await asking.Updates.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new AgentOutcome.AskedQuestion("Which database should it use?"), await asking.Outcome);

        var finishing = await session.SendAsync("PostgreSQL", TestContext.Current.CancellationToken);
        Assert.Equal(["Writing the Spec"], await finishing.Updates.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new AgentOutcome.Done(), await finishing.Outcome);
    }

    [Fact]
    public async Task Each_new_session_of_a_role_plays_the_next_script()
    {
        var sandbox = await CreateAsync();
        _provider.ScriptAgentSessions(AgentRole.Developer,
            new FakeAgentSession(new FakeAgentTurn(["Implemented the first Task"], new AgentOutcome.Done())),
            new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.AskedQuestion("Which port?"))));

        var first = await SendFirstMessageAsync(sandbox, AgentRole.Developer);
        var second = await SendFirstMessageAsync(sandbox, AgentRole.Developer);

        Assert.Equal(new AgentOutcome.Done(), await first.Outcome);
        Assert.Equal(new AgentOutcome.AskedQuestion("Which port?"), await second.Outcome);
    }

    [Fact]
    public async Task Agent_sessions_are_scripted_per_role()
    {
        var sandbox = await CreateAsync();
        _provider.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.AskedQuestion("Which port?"))));

        var reviewer = await SendFirstMessageAsync(sandbox, AgentRole.Reviewer);

        Assert.Equal(new AgentOutcome.Done(), await reviewer.Outcome);
    }

    [Fact]
    public async Task Creation_can_fail_with_an_error_message_and_succeed_on_retry()
    {
        _provider.FailNextCreation("QuotaExceeded: Operation could not be completed as it results in exceeding approved standardDSv5Family Cores quota.");

        var failure = await Assert.ThrowsAsync<SandboxCreationFailedException>(CreateAsync);

        Assert.StartsWith("QuotaExceeded:", failure.Message);
        Assert.Empty(await _provider.ListAsync(TestContext.Current.CancellationToken));
        var retried = await CreateAsync();
        Assert.Equal([retried], await _provider.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Provisioning_can_be_slow()
    {
        _provider.ProvisioningDelay = TimeSpan.FromMinutes(3);

        var creating = CreateAsync();

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(creating.IsCompleted);
        _time.Advance(TimeSpan.FromMinutes(1));
        var sandbox = await creating;
        Assert.Contains(sandbox, await _provider.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_destroyed_Sandbox_runs_nothing()
    {
        var sandbox = await CreateAsync();
        await _provider.DestroyAsync(sandbox, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.RunCommandAsync(sandbox, "dotnet test", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.StartAgentSessionAsync(sandbox, AgentRole.Developer, TestContext.Current.CancellationToken));
    }

    private async Task<AgentTurn> SendFirstMessageAsync(Sandbox sandbox, AgentRole role)
    {
        await using var session = await _provider.StartAgentSessionAsync(sandbox, role, TestContext.Current.CancellationToken);
        return await session.SendAsync("Start", TestContext.Current.CancellationToken);
    }

    private Task<Sandbox> CreateAsync() =>
        _provider.CreateAsync(new SandboxOwner(SandboxOwnerKind.Intent, Guid.NewGuid()), [], TestContext.Current.CancellationToken);
}
