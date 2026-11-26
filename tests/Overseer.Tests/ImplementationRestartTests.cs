using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Overseer.Implementation;
using Overseer.Sandboxes;
using static Overseer.Tests.FinalVerificationTests;
using static Overseer.Tests.ImplementationTests;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;
using static Overseer.Tests.TaskTests;

namespace Overseer.Tests;

/// <summary>
/// An implementation that a restart of serve, as of a redeploy or a crash, interrupts: it carries on by itself, in a fresh Sandbox
/// prepared from the Intent branch.
/// </summary>
public class ImplementationRestartTests(PostgresServer postgres)
{
    private static readonly CommandResult Passes = new(0, "");

    [Fact]
    public async Task After_a_restart_the_implementation_carries_on_in_a_fresh_Sandbox_from_its_first_Task_that_isnt_Done()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        Sandbox old;
        FakeSandboxProvider firstSandboxes;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/api", "acme/web"]))
        {
            firstSandboxes = first.Sandboxes;
            first.Sandboxes.ScriptAgentSessions(AgentRole.Designer, DoneAtOnce());
            address = await ImplementingAsync(first, ThreeTasks(), "Export data", "acme/api", "acme/web");
            await StatesAsync(first, address, "Done", "Running", "Pending");
            await SquadMessagesAsync(first, 2);
            old = Assert.Single(await first.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        }
        // Stopping serve stops the agent.
        Assert.Equal(2, SquadSessions(firstSandboxes.AgentSessionsStopped));

        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        // Nothing is pressed: the interrupted Task starts over, in a new session.
        var message = Assert.Single(await SquadMessagesAsync(second, 1));
        Assert.StartsWith("You are a Developer of Overseer", message.Message);
        Assert.Contains("Your Task is Task 2 of 3", message.Message);
        var fresh = Assert.Single(await second.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(old, fresh);
        Assert.Equal(fresh, message.Sandbox);
        Assert.True(second.Sandboxes.MayPushOf(fresh));
        // Prepared like the first: each repository put on the Intent branch.
        Assert.Equal(2, second.Sandboxes.CommandsRun.Count(run => run.Sandbox == fresh && run.Command.Contains("git checkout")));
        await StatesAsync(second, address, "Done", "Running", "Pending");
        // The Task that is Done doesn't run again.
        Assert.Equal([AgentRole.Developer], SquadSessionsStarted(second).Select(session => session.Role));
        var page = await GetAsync(second, address);
        Assert.Equal(("Ready", ("Green", "Working")), (ImplementationOn(page)!.Phase, ImplementationOn(page)!.Light));
        // The Sandbox from before the restart is destroyed.
        Assert.Equal([old], second.Sandboxes.DestructionsBegun);
        await using var database = second.OpenDatabase();
        Assert.Equal(
            [fresh.ResourceId],
            await database.Sandboxes.Where(sandbox => sandbox.DestroyedAt == null).Select(sandbox => sandbox.ResourceId)
                .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_interrupted_Task_has_all_its_verification_attempts_again_and_its_new_session_is_told_what_failed_last()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        FakeGitHubGateway gitHub;
        await using (var first = await StartWithManifestsAsync(connectionString, ["acme/web"]))
        {
            gitHub = first.GitHub;
            await SetOperatingLimitsAsync(first, verificationAttemptsPerTask: 2);
            first.Sandboxes.ScriptAgentSessions(
                AgentRole.Developer, new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done()), FakeAgentTurn.Endless()));
            first.Sandboxes.ScriptCommandsContaining("npm test", new CommandResult(1, "exports nothing"));
            address = await ImplementationTests.ImplementingAsync(first, "Export data", "acme/web");
            // Told of the one failed attempt, the agent works on.
            await SquadMessagesAsync(first, 2);
        }

        // One more failure than the interrupted session had left.
        await using var second = await RestartAsync(connectionString, gitHub, sandboxes =>
        {
            sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
            sandboxes.ScriptCommandsContaining("npm test", new CommandResult(1, "no header"), Passes);
        });

        await StatesAsync(second, address, "Done");
        var messages = SquadMessages(second);
        Assert.Contains("exports nothing", messages[0].Message);
        Assert.Contains("Failed attempts so far: 1, of the 2 allowed.", messages[1].Message);
        Assert.Equal(
            [
                "Run 1, attempt 1: failed. The test command of acme/web failed with exit code 1.",
                "Run 2, attempt 1: failed. The test command of acme/web failed with exit code 1.",
                "Run 2, attempt 2: passed.",
            ],
            VerificationTests.AttemptsOn(await GetAsync(second, address), 1).Select(attempt => attempt.Heading));
    }

    [Fact]
    public async Task An_old_Sandbox_that_cant_be_destroyed_after_a_restart_fails_the_implementation_and_no_other_is_created_until_it_can()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            address = await ImplementationTests.ImplementingAsync(first, "Export data", "acme/web");
            await SquadMessagesAsync(first, 1);
        }

        // Neither the sweep of the Sandboxes left over nor the implementation can destroy it.
        await using var second = await StartWithRepositoriesAsync(connectionString, [], sandboxes: sandboxes =>
        {
            sandboxes.FailNextDestruction("The resource group is locked.");
            sandboxes.FailNextDestruction("The resource group is locked.");
        });

        var failed = await FailedAsync(second, address);
        Assert.Equal(
            "The old Sandbox could not be destroyed, so no new one was created: The resource group is locked.", failed.Status);
        Assert.Equal(["Pending"], TasksOn(await GetAsync(second, address)).Select(task => task.State));
        Assert.Empty(await second.Sandboxes.ListAsync(TestContext.Current.CancellationToken));

        using var retried = await ImplementationTests.RetryAsync(second, address);

        var message = Assert.Single(await SquadMessagesAsync(second, 1));
        Assert.Equal([message.Sandbox], await second.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_Task_that_became_a_Failed_Task_just_as_serve_stopped_leaves_the_implementation_failed_without_a_new_Sandbox()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            address = await ImplementationTests.ImplementingAsync(first, "Export data", "acme/web");
            await SquadMessagesAsync(first, 1);
            // As serve leaves it when it stops between recording the Failed Task and recording the implementation failed.
            await using var database = first.OpenDatabase();
            await database.IntentTasks.ExecuteUpdateAsync(
                task => task.SetProperty(record => record.State, IntentTaskState.Failed), TestContext.Current.CancellationToken);
        }

        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        var page = await GetAsync(second, address);
        Assert.Equal(
            new ShownImplementation(
                "Failed", "Task 1 is a Failed Task: its work didn't pass verification.", ("Red", "Failed"), RetryAction: null),
            ImplementationOn(page));
        Assert.Equal(["Failed"], TasksOn(page).Select(task => task.State));
        await RefinementTests.NoSandboxLeftAsync(second);
        Assert.Empty(second.Sandboxes.CommandsRun);

        using var retried = await FailedTaskTests.RetryTaskAsync(second, address, 1);

        var message = Assert.Single(await SquadMessagesAsync(second, 1));
        Assert.True(second.Sandboxes.MayPushOf(message.Sandbox));
        await StatesAsync(second, address, "Running");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Task_pushed_but_not_yet_Done_when_serve_stopped_runs_again_and_the_Intent_goes_on_to_its_pull_request(bool fixUp)
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        FakeGitHubGateway gitHub;
        await using (var first = await StartWithManifestsAsync(connectionString, ["acme/web"]))
        {
            gitHub = first.GitHub;
            var working = new TaskCompletionSource();
            first.Sandboxes.ScriptAgentSessions(
                AgentRole.Developer, fixUp ? [DoneAtOnce(), DoneOnce(working.Task)] : [DoneOnce(working.Task)]);
            if (fixUp)
            {
                // The Task's own verification passes, and the final one after it fails.
                first.Sandboxes.ScriptCommandsContaining("npm test", Passes, new CommandResult(1, "1 test failed"), Passes);
            }
            VerificationTests.Changes(first, "acme/web", "src/orders.ts");
            address = await ImplementationTests.ImplementingAsync(first, "Export data", "acme/web");
            await SquadMessagesAsync(first, fixUp ? 2 : 1);
            var pushes = Pushes(first);
            // GitHub gets the work, and Overseer never hears that it did.
            first.Sandboxes.HoldCommandsContaining("git push", new TaskCompletionSource().Task);
            working.SetResult();
            await EventuallyAsync(() => Task.FromResult(Pushes(first) > pushes ? "" : null), "the Task's work to be pushed");
            gitHub.AddBranch("acme/web", BranchOf(address));
        }

        // The Intent branch holds the work now, so the Task run again changes nothing.
        await using var second = await RestartAsync(connectionString, gitHub, sandboxes =>
        {
            sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
            sandboxes.ScriptCommandsContaining(IntentBranchComparedIn("acme/web"), new CommandResult(0, "src/orders.ts\0"));
        });

        var page = await InReviewAsync(second, address);
        Assert.Equal(fixUp ? ["Done", "Done"] : ["Done"], TasksOn(page).Select(task => task.State));
        // Only the Task that wasn't Done ran again.
        var message = Assert.Single(SquadMessages(second));
        Assert.Contains(fixUp ? "Your Task is Task 2 of 2" : "Your Task is Task 1 of 1", message.Message);
        Assert.Equal("acme/web", Assert.Single(second.GitHub.PullRequests).Repository.ToString());
    }

    [Fact]
    public async Task A_restart_during_the_final_verification_runs_it_again_in_a_fresh_Sandbox_and_opens_one_pull_request_per_changed_repository()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        FakeGitHubGateway gitHub;
        Sandbox old;
        await using (var first = await StartWithManifestsAsync(connectionString, ["acme/api", "acme/web"]))
        {
            gitHub = first.GitHub;
            first.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
            // Only the final verification tests acme/api, which the Task doesn't touch.
            first.Sandboxes.HoldCommandsContaining(ManifestCommandIn("acme/api", "npm test"), new TaskCompletionSource().Task);
            address = await ImplementationTests.ImplementingAsync(first, "Export data", "acme/api", "acme/web");
            await InPhaseAsync(first, address, "FinalVerification");
            await EventuallyAsync(
                () => Task.FromResult(first.Sandboxes.CommandsRun.Any(
                    run => run.Command.Contains(ManifestCommandIn("acme/api", "npm test"))) ? "" : null),
                "the final verification to test acme/api");
            gitHub.AddBranch("acme/api", BranchOf(address));
            gitHub.AddBranch("acme/web", BranchOf(address));
            old = Assert.Single(await first.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        }

        await using var second = await RestartAsync(connectionString, gitHub, sandboxes =>
        {
            sandboxes.ScriptCommandsContaining(IntentBranchComparedIn("acme/api"), new CommandResult(0, "src/orders.ts\0"));
            sandboxes.ScriptCommandsContaining(IntentBranchComparedIn("acme/web"), new CommandResult(0, "src/orders.ts\0"));
        });

        var page = await InReviewAsync(second, address);
        Assert.Equal(["Done"], TasksOn(page).Select(task => task.State));
        Assert.Empty(SquadSessionsStarted(second));
        var fresh = Assert.Single(await second.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(old, fresh);
        // Both again, also the one that may have passed before the restart.
        Assert.Equal(
            2, second.Sandboxes.CommandsRun.Count(run => run.Sandbox == fresh && run.Command.Contains("\nnpm test")));
        Assert.Equal(
            ["acme/api", "acme/web"], second.GitHub.PullRequests.Select(open => open.Repository.ToString()));
    }

    [Fact]
    public async Task A_restart_while_pull_requests_are_opened_opens_the_ones_still_missing_without_a_Sandbox_one_per_changed_repository()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        FakeGitHubGateway gitHub;
        await using (var first = await StartWithManifestsAsync(connectionString, ["acme/api", "acme/docs", "acme/web"]))
        {
            gitHub = first.GitHub;
            first.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
            // GitHub opens the one in acme/docs, and Overseer never hears that it did.
            gitHub.HoldPullRequestAnswers("acme/docs", new TaskCompletionSource().Task);
            foreach (var repository in new[] { "acme/api", "acme/docs", "acme/web" })
            {
                first.Sandboxes.ScriptCommandsContaining(IntentBranchComparedIn(repository), new CommandResult(0, "src/orders.ts\0"));
            }
            address = await AwaitingApprovalAsync(first, "Export data", "acme/api", "acme/docs", "acme/web");
            foreach (var repository in new[] { "acme/api", "acme/docs", "acme/web" })
            {
                gitHub.AddBranch(repository, BranchOf(address));
            }
            using var approved = await ApproveAsync(first, address, start: true);
            await EventuallyAsync(() => Task.FromResult(first.GitHub.PullRequests.Count == 2 ? "" : null), "two pull requests");
            var shown = ImplementationOn(await GetAsync(first, address))!;
            Assert.Equal(
                ("OpeningPullRequests", "Opening a pull request for each changed repository.", ("Green", "Working")),
                (shown.Phase, shown.Status, shown.Light));
        }

        await using var second = await RestartAsync(connectionString, gitHub);

        await InReviewAsync(second, address);
        Assert.Equal(
            ["acme/api", "acme/docs", "acme/web"], second.GitHub.PullRequests.Select(open => open.Repository.ToString()));
        var page = await GetAsync(second, address);
        Assert.All(
            ["acme/api #1", "acme/docs #1", "acme/web #1"],
            link => Assert.Single(Regex.Matches(page, $">{link}</a>")));
        // Nothing but opening them was left to do.
        Assert.Empty(await second.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(second.Sandboxes.CommandsRun);
        await RefinementTests.NoSandboxLeftAsync(second);
    }

    /// <summary>Starts serve again on the same database, with GitHub as it was, which keeps all it has meanwhile.</summary>
    /// <param name="sandboxes">
    /// Scripts the new fake Sandbox Provider, which has none of the old one's Sandboxes nor scripts, before the server starts. Its
    /// clones of acme/api and acme/web have their Repository Manifest.
    /// </param>
    private static Task<RunningOverseer> RestartAsync(
        string connectionString, FakeGitHubGateway gitHub, Action<FakeSandboxProvider>? sandboxes = null) =>
        StartWithRepositoriesAsync(
            connectionString, [],
            sandboxes: provider =>
            {
                provider.AddRepositoryFile("acme/api", ".overseer.yml", Manifest);
                provider.AddRepositoryFile("acme/web", ".overseer.yml", Manifest);
                sandboxes?.Invoke(provider);
            },
            gitHub: after => after.CarryOver(gitHub));

    private static int SquadSessions(IReadOnlyList<AgentSessionStart> sessions) =>
        sessions.Count(session => session.Role is AgentRole.Designer or AgentRole.Developer);

    private static int Pushes(RunningOverseer overseer) =>
        overseer.Sandboxes.CommandsRun.Count(run => run.Command.Contains("git push"));

    private static string BranchOf(string address) => $"overseer/intent-{address["/intents/".Length..].Replace("-", "")}";

    /// <summary>A session whose turn ends as done once <paramref name="until"/> completes.</summary>
    private static FakeAgentSession DoneOnce(Task until) => new(new FakeAgentTurn([], new AgentOutcome.Done()) { EndsAfter = until });

    /// <summary>A session whose every turn ends as done at once.</summary>
    private static FakeAgentSession DoneAtOnce() => new(new FakeAgentTurn([], new AgentOutcome.Done()));
}
