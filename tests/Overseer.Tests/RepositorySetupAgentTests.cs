using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Overseer.GitHub;
using Overseer.Repositories;
using Overseer.Sandboxes;

using static Overseer.Tests.RepositorySetupPortal;

namespace Overseer.Tests;

/// <summary>The second part of a Repository Setup: an agent writes the starter files of the new repository, in a Sandbox.</summary>
public class RepositorySetupAgentTests(PostgresServer postgres)
{
    [Fact]
    public async Task After_the_repository_is_created_an_agent_works_on_it_in_a_Sandbox_owned_by_the_Repository_Setup()
    {
        await using var overseer = await StartWithTokenAsync(postgres);

        using var response = await StartSetupAsync(overseer, "acme", "web");

        var started = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.AgentSessionsStarted.SingleOrDefault()), "the agent session");
        var setup = Assert.Single(await overseer.Services.GetRequiredService<RepositorySetups>().ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new SandboxOwner(SandboxOwnerKind.RepositorySetup, setup.Id), started.Sandbox.Owner);
        Assert.Equal(["acme/web"], overseer.Sandboxes.RepositoriesInScopeOf(started.Sandbox));
        Assert.True(overseer.Sandboxes.MayPushOf(started.Sandbox));
    }

    [Fact]
    public async Task The_agent_is_told_the_purpose_and_tech_stack_and_to_push_a_README_agent_guidance_and_a_manifest_to_main()
    {
        await using var overseer = await StartWithTokenAsync(postgres);

        using var response = await StartSetupAsync(overseer, "acme", "web", "The public website of Acme.\nIt sells anvils.", "Astro and TypeScript.");

        var sent = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.SingleOrDefault()), "the agent's instructions");
        Assert.Equal(AgentRole.Developer, sent.Role);
        Assert.Contains("acme/web", sent.Message);
        Assert.Contains("The public website of Acme.\nIt sells anvils.", sent.Message);
        Assert.Contains("Astro and TypeScript.", sent.Message);
        Assert.Contains("README.md", sent.Message);
        Assert.Contains("AGENTS.md", sent.Message);
        Assert.Contains(".overseer.yml", sent.Message);
        Assert.Contains("push", sent.Message);
        Assert.Contains("main", sent.Message);
    }

    [Fact]
    public async Task The_agent_gets_the_current_agent_harness_configuration_and_model_provider_tokens()
    {
        await using var overseer = await StartWithTokenAsync(postgres);
        const string harness = """
            {
              "agent": {
                "analyst": { "model": "gateway/opus" },
                "designer": { "model": "gateway/sonnet" },
                "developer": { "model": "gateway/developer-model" },
                "reviewer": { "model": "gateway/opus" }
              },
              "provider": { "gateway": { "options": { "apiKey": "{env:GATEWAY_API_KEY}" } } }
            }
            """;
        using var savedHarness = await overseer.SubmitFormAsync("/settings", "agent-harness", new Dictionary<string, string> { ["AgentHarness"] = harness });
        using var savedToken = await overseer.SubmitFormAsync("/settings", "provider-token", new Dictionary<string, string>
        {
            ["ProviderTokenName"] = "gateway",
            ["ProviderToken"] = "gw-secret-123",
        });

        using var response = await StartSetupAsync(overseer, "acme", "web");

        var started = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.AgentSessionsStarted.SingleOrDefault()), "the agent session");
        Assert.Equal((harness, "gateway/developer-model"), (started.HarnessJson, started.Model));
        Assert.Equal("gw-secret-123", started.Environment["GATEWAY_API_KEY"]);
    }

    [Fact]
    public async Task The_Portal_shows_the_Sandbox_being_provisioned_and_then_the_agent_working()
    {
        var time = new FakeTimeProvider();
        await using var overseer = await StartWithTokenAsync(postgres, time: time);
        overseer.Sandboxes.ProvisioningDelay = TimeSpan.FromMinutes(4);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(FakeAgentTurn.Endless("Writing README.md")));

        using var response = await StartSetupAsync(overseer, "acme", "web");

        var provisioning = await SetupAsync(overseer, "acme/web");
        Assert.Equal(
            ("ProvisioningSandbox", "Provisioning a Sandbox for the agent. A new Sandbox takes a few minutes."),
            (provisioning?.Phase, provisioning?.Status));
        var working = await EventuallyAsync(async () =>
        {
            // Until the fake has started waiting, advancing the clock doesn't end its wait, so it is advanced on each look.
            time.Advance(TimeSpan.FromMinutes(4));
            return await SetupAsync(overseer, "acme/web") is { Phase: "AgentWorking" } setup ? setup : null;
        }, "the agent to start working");
        Assert.Equal("The agent is writing the starter files.", working.Status);
    }

    [Fact]
    public async Task Once_the_agent_has_pushed_the_manifest_to_main_the_Repository_Setup_is_done_and_its_Sandbox_destroyed()
    {
        await using var overseer = await StartWithTokenAsync(postgres);
        AgentPushesStarterFiles(overseer, "acme/web");

        using var response = await StartSetupAsync(overseer, "acme", "web");

        var setup = await FinishedAsync(overseer, "acme/web");
        Assert.Equal(("Done", "The starter files are on main.", null), (setup.Phase, setup.Status, setup.RetryAction));
        Assert.Empty(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        await using var database = overseer.OpenDatabase();
        var sandbox = Assert.Single(database.Sandboxes);
        Assert.NotNull(sandbox.DestroyedAt);
    }

    [Fact]
    public async Task When_the_agent_finishes_without_the_manifest_on_main_the_Repository_Setup_fails_and_offers_Retry()
    {
        await using var overseer = await StartWithTokenAsync(postgres);
        overseer.GitHub.AddFile("acme/web", "main", "README.md");

        using var response = await StartSetupAsync(overseer, "acme", "web");

        var setup = await FinishedAsync(overseer, "acme/web");
        Assert.Equal("Failed", setup.Phase);
        Assert.Equal("The agent finished, but .overseer.yml is not on main of acme/web.", setup.Status);
        Assert.NotNull(setup.RetryAction);
        Assert.Empty(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task When_the_Sandbox_cant_be_created_the_Repository_Setup_fails_with_the_providers_error()
    {
        await using var overseer = await StartWithTokenAsync(postgres);
        overseer.Sandboxes.FailNextCreation("Operation could not be completed as it results in exceeding approved standardDSv5Family Cores quota.");

        using var response = await StartSetupAsync(overseer, "acme", "web");

        var setup = await FinishedAsync(overseer, "acme/web");
        Assert.Equal("Failed", setup.Phase);
        Assert.Equal(
            "The Sandbox could not be created: Operation could not be completed as it results in exceeding approved standardDSv5Family Cores quota.",
            setup.Status);
        Assert.NotNull(setup.RetryAction);
        Assert.Empty(overseer.Sandboxes.AgentSessionsStarted);
    }

    [Fact]
    public async Task When_the_agent_session_fails_the_Repository_Setup_fails_with_its_error_and_its_Sandbox_is_destroyed()
    {
        await using var overseer = await StartWithTokenAsync(postgres);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            new FakeAgentTurn(["Writing README.md"], new AgentOutcome.Done()) { Failure = "opencode exited with code 1." }));

        using var response = await StartSetupAsync(overseer, "acme", "web");

        var setup = await FinishedAsync(overseer, "acme/web");
        Assert.Equal(("Failed", "The agent session failed: opencode exited with code 1."), (setup.Phase, setup.Status));
        Assert.NotNull(setup.RetryAction);
        Assert.Empty(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task When_the_agent_asks_a_question_the_Repository_Setup_fails_with_it_as_no_one_answers()
    {
        await using var overseer = await StartWithTokenAsync(postgres);
        AgentPushesStarterFiles(overseer, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            new FakeAgentTurn([], new AgentOutcome.AskedQuestion("Which Node version should it use?"))));

        using var response = await StartSetupAsync(overseer, "acme", "web");

        var setup = await FinishedAsync(overseer, "acme/web");
        Assert.Equal(
            ("Failed", "The agent asked a question, which no one answers during a Repository Setup: Which Node version should it use?"),
            (setup.Phase, setup.Status));
        Assert.Empty(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Retry_has_an_agent_write_the_starter_files_again_in_a_new_Sandbox_without_creating_the_repository_again()
    {
        await using var overseer = await StartWithTokenAsync(postgres);
        overseer.Sandboxes.FailNextCreation("Allocation failed.");
        using var started = await StartSetupAsync(overseer, "acme", "web");
        Assert.Equal("Failed", (await FinishedAsync(overseer, "acme/web")).Phase);
        AgentPushesStarterFiles(overseer, "acme/web");

        using var retried = await RetryAsync(overseer, "acme/web");

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal("/settings", retried.RequestMessage!.RequestUri!.AbsolutePath);
        var setup = await FinishedAsync(overseer, "acme/web");
        Assert.Equal(("Done", "The starter files are on main."), (setup.Phase, setup.Status));
        Assert.Equal([new RepositoryName("acme", "web")], overseer.GitHub.CreatedRepositories);
        Assert.Single(await overseer.Services.GetRequiredService<RepositorySetups>().ListAsync(TestContext.Current.CancellationToken));
        Assert.Single(overseer.Sandboxes.AgentSessionsStarted);
        Assert.Empty(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_Repository_Setup_interrupted_by_a_restart_is_shown_failed_leaves_no_Sandbox_behind_and_can_be_retried()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var first = await StartWithTokenAsync(postgres, connectionString))
        {
            first.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(FakeAgentTurn.Endless("Writing README.md")));
            using var started = await StartSetupAsync(first, "acme", "web");
            await EventuallyAsync(async () => await SetupAsync(first, "acme/web") is { Phase: "AgentWorking" } setup ? setup : null, "the agent to start working");
            // As if serve crashed: the Sandbox outlives it.
            first.Sandboxes.FailNextDestruction("Azure could not be reached.");
        }

        await using var second = await StartWithTokenAsync(postgres, connectionString);
        // GitHub still has the repository; each running Overseer gets a new fake GitHub.
        second.GitHub.AddRepository("acme/web");

        var interrupted = await SetupAsync(second, "acme/web");
        Assert.Equal(
            ("Failed", "Overseer restarted while the agent was writing the starter files. Retry to start the agent again."),
            (interrupted?.Phase, interrupted?.Status));
        await EventuallyAsync(async () =>
        {
            await using var database = second.OpenDatabase();
            return await database.Sandboxes.AnyAsync(sandbox => sandbox.DestroyedAt == null, TestContext.Current.CancellationToken) ? null : "";
        }, "the left-over Sandbox to be destroyed");

        AgentPushesStarterFiles(second, "acme/web");
        using var retried = await RetryAsync(second, "acme/web");
        var retriedSetup = await FinishedAsync(second, "acme/web");
        Assert.Equal(("Done", "The starter files are on main."), (retriedSetup.Phase, retriedSetup.Status));
    }

    /// <summary>
    /// Scripts the agent's work: the fake agent can't push, so GitHub is given the files it would. Its session ends done.
    /// </summary>
    private static void AgentPushesStarterFiles(RunningOverseer overseer, string repository)
    {
        foreach (var file in (string[])["README.md", "AGENTS.md", ".overseer.yml"])
        {
            overseer.GitHub.AddFile(repository, "main", file);
        }
    }
}
