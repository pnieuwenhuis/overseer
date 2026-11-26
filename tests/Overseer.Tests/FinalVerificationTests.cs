using Microsoft.Extensions.Time.Testing;
using Overseer.Sandboxes;
using static Overseer.Tests.AgentTurns;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;
using static Overseer.Tests.TaskTests;

namespace Overseer.Tests;

/// <summary>
/// Final verification: once every Task is Done, Overseer runs the full build and tests on the Intent branch, so that pull requests are
/// only opened for green code, and the Intent moves to In Review.
/// </summary>
public class FinalVerificationTests(PostgresServer postgres)
{
    internal const string Manifest = "setup: npm ci\nbuild: npm run build\ntest: npm test\n";

    [Fact]
    public async Task After_the_last_Task_every_Repository_in_scope_is_built_and_tested_and_the_Intent_moves_to_In_Review_with_an_orange_light()
    {
        var time = new FakeTimeProvider();
        await using var overseer = await StartWithManifestsAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/api", "acme/web"], time);
        await SetMaxActiveIntentsAsync(overseer, 1);
        var working = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer, new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done()) { EndsAfter = working.Task }));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/api", "acme/web");
        await SquadMessagesAsync(overseer, 1);
        var commands = overseer.Sandboxes.CommandsRun.Count;
        time.Advance(TimeSpan.FromMinutes(20));

        working.SetResult();

        var page = await InReviewAsync(overseer, address);
        Assert.Equal(("Orange", "Needs Attention"), LightOn(page));
        Assert.Equal(("Orange", "Needs Attention"), LightOn(await GetAsync(overseer, "/")));
        // The Task's verification built and tested acme/web, which it lists, and the final one every Repository in scope.
        Assert.Equal(
            ["acme/web", "acme/api", "acme/web"],
            overseer.Sandboxes.CommandsRun.Skip(commands).Where(run => run.Command.Contains("npm test"))
                .Select(run => run.Command.Split('\'')[1]["/home/overseer/repositories/".Length..]));
        Assert.Equal(3, overseer.Sandboxes.CommandsRun.Skip(commands).Count(run => run.Command.Contains("npm run build")));
        // The Sandbox stays for the review.
        Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        // Still Active: its Working Time goes on, and it holds its slot.
        time.Advance(TimeSpan.FromMinutes(15));
        Assert.Contains("35 min", ElementText(await GetAsync(overseer, address), "intent-working-time"));
        using var saved = await SaveAsync(overseer, "Import data", "acme/web");
        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Import data"));
        Assert.Contains("All Active Intent slots are taken, by \"Export data\".", Alert(await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_failing_final_verification_appends_a_fix_up_Developer_Task_with_the_output_which_runs_like_any_Task_and_is_verified_by_the_full_verification()
    {
        await using var overseer = await StartWithManifestsAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/api", "acme/web"]);
        var fixing = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneOnce(Task.CompletedTask), DoneOnce(fixing.Task));
        // Only the final verification tests acme/api, which the Task doesn't touch, so its first run is the final one's.
        FailsOnce(overseer, "acme/api", "npm test", "1 test failed: orders are gone");

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/api", "acme/web");

        var message = (await SquadMessagesAsync(overseer, 2))[1];
        var tasks = await StatesAsync(overseer, address, "Done", "Running");
        Assert.Equal(new ShownTask(2, "Fix what fails the final verification", "Developer", "acme/api", "Running"), tasks[1]);
        Assert.Equal(AgentRole.Developer, message.Role);
        Assert.Contains("Your Task is Task 2 of 2: Fix what fails the final verification", message.Message);
        Assert.Contains("The test command of acme/api failed with exit code 1.", message.Message);
        Assert.Contains("npm test", message.Message);
        Assert.Contains("1 test failed: orders are gone", message.Message);
        // Told how its work is verified.
        Assert.Contains("it runs those commands in every Repository in scope", message.Message);
        var page = await GetAsync(overseer, address);
        Assert.Equal("Implementing", ElementText(page, "intent-state"));
        Assert.Equal(("Ready", ("Green", "Working")), (ImplementationTests.ImplementationOn(page)!.Phase, ImplementationTests.ImplementationOn(page)!.Light));

        fixing.SetResult();

        page = await InReviewAsync(overseer, address);
        await StatesAsync(overseer, address, "Done", "Done");
        // Both repositories, though the Task touches one.
        Assert.Equal(
            [
                "The build command of acme/api", "The test command of acme/api",
                "The build command of acme/web", "The test command of acme/web",
            ],
            Assert.Single(VerificationTests.AttemptsOn(page, 2)).Steps.Select(step => step.Name));
        // The failure before it is no attempt of the Task's.
        Assert.Equal("Attempt 1: passed.", Assert.Single(VerificationTests.AttemptsOn(page, 2)).Heading);
    }

    [Fact]
    public async Task A_changed_repository_without_a_Repository_Manifest_fails_the_final_verification_and_an_unchanged_one_is_skipped()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api", "acme/docs", "acme/web");
        overseer.Sandboxes.AddRepositoryFile("acme/web", ".overseer.yml", Manifest);
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer, DoneOnce(Task.CompletedTask), new FakeAgentSession(FakeAgentTurn.Endless()));
        // acme/docs differs from its default branch, as after an earlier Task changed it; acme/api doesn't.
        HoldsMoreThanItsDefaultBranch(overseer, "acme/docs", "README.md");

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/api", "acme/docs", "acme/web");

        var message = (await SquadMessagesAsync(overseer, 2))[1].Message;
        var tasks = await StatesAsync(overseer, address, "Done", "Running");
        Assert.Equal("acme/docs", tasks[1].Repositories);
        Assert.Contains(
            "acme/docs is changed and has no Repository Manifest (.overseer.yml), so its work can't be verified.", message);
        Assert.DoesNotContain("acme/api is changed", message);
        // It may have to create one.
        Assert.Contains("`setup` (prepares a fresh clone, such as installing dependencies), `build` and `test`", message);
    }

    [Fact]
    public async Task Every_repository_is_built_and_tested_although_one_fails_and_the_fix_up_Task_touches_all_that_failed()
    {
        await using var overseer = await StartWithManifestsAsync(
            await postgres.CreateEmptyDatabaseAsync(), ["acme/api", "acme/docs", "acme/web"]);
        FailsOnce(overseer, "acme/api", "npm run build", "orders do not compile");
        // An invalid manifest would fail the preparation, so acme/docs gets its only while the Task is worked on.
        var working = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer,
            new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done())
            {
                EndsAfter = working.Task,
                Writes = new Dictionary<string, string> { ["/home/overseer/repositories/acme/docs/.overseer.yml"] = "build: make\n" },
            }),
            new FakeAgentSession(FakeAgentTurn.Endless()));

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/api", "acme/docs", "acme/web");
        await SquadMessagesAsync(overseer, 1);
        working.SetResult();

        var message = (await SquadMessagesAsync(overseer, 2))[1].Message;
        var tasks = await StatesAsync(overseer, address, "Done", "Running");
        Assert.Equal("acme/docs, acme/api", tasks[1].Repositories);
        Assert.Contains("The Repository Manifest of acme/docs (.overseer.yml) is invalid.", message);
        Assert.Contains("The build command of acme/api failed with exit code 1.", message);
        Assert.Contains("orders do not compile", message);
    }

    [Fact]
    public async Task A_fix_up_Task_that_never_passes_is_a_Failed_Task_after_the_allowed_attempts_and_no_second_one_is_added()
    {
        await using var overseer = await StartWithManifestsAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/api", "acme/web"]);
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 2);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneOnce(Task.CompletedTask));
        overseer.Sandboxes.ScriptCommandsContaining(
            ManifestCommandIn("acme/api", "npm test"), new CommandResult(1, "1 test failed: orders are gone"));

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/api", "acme/web");

        var failed = await ImplementationTests.FailedAsync(overseer, address);
        Assert.Equal(
            "Task 2 is a Failed Task: its work didn't pass verification in 2 attempts. "
            + "In the last one, attempt 2: The test command of acme/api failed with exit code 1.",
            failed.Status);
        Assert.Equal(("Red", "Failed"), failed.Light);
        Assert.Null(failed.RetryAction);
        await StatesAsync(overseer, address, "Done", "Failed");
        var page = await GetAsync(overseer, address);
        Assert.Equal("Implementing", ElementText(page, "intent-state"));
        // The initial failure doesn't count against the fix-up Task's attempts.
        Assert.Equal(2, VerificationTests.AttemptsOn(page, 2).Count);
        // The Task's first message and what failed after its first attempt.
        Assert.Equal(3, SquadMessages(overseer).Count);
        Assert.Contains("The test command of acme/api failed with exit code 1.", SquadMessages(overseer)[2].Message);
        Assert.Empty(overseer.GitHub.PullRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Failed_fix_up_Task_is_recovered_like_any_Failed_Task_and_is_still_verified_by_the_full_verification(bool edited)
    {
        await using var overseer = await StartWithManifestsAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/api", "acme/web"]);
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 1);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneOnce(Task.CompletedTask));
        // Fails the final verification and the fix-up Task's one attempt, and passes from then on.
        overseer.Sandboxes.ScriptCommandsContaining(
            ManifestCommandIn("acme/api", "npm test"),
            new CommandResult(1, "orders are gone"), new CommandResult(1, "orders are still gone"), new CommandResult(0, ""));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/api", "acme/web");
        await ImplementationTests.FailedAsync(overseer, address);
        await StatesAsync(overseer, address, "Done", "Failed");

        // Edited to touch only the repository that passes.
        using var retried = edited
            ? await FailedTaskTests.EditAndRetryAsync(
                overseer, $"{address}/tasks/2/edit",
                new FailedTaskTests.EditedFields("Bring the orders back", "Look at the export.", "Developer", ["acme/web"]))
            : await FailedTaskTests.RetryTaskAsync(overseer, address, 2);

        var page = await InReviewAsync(overseer, address);
        await StatesAsync(overseer, address, "Done", "Done");
        var message = SquadMessages(overseer)[^1].Message;
        Assert.Contains(
            edited ? "Your Task is Task 2 of 2: Bring the orders back" : "Your Task is Task 2 of 2: Fix what fails the final verification",
            message);
        Assert.Contains("# What failed last time", message);
        Assert.Contains("orders are still gone", message);
        Assert.Contains("it runs those commands in every Repository in scope", message);
        var attempts = VerificationTests.AttemptsOn(page, 2);
        Assert.Equal(
            ["Run 1, attempt 1: failed. The test command of acme/api failed with exit code 1.", "Run 2, attempt 1: passed."],
            attempts.Select(attempt => attempt.Heading));
        Assert.Equal(
            [
                "The build command of acme/api", "The test command of acme/api",
                "The build command of acme/web", "The test command of acme/web",
            ],
            attempts[1].Steps.Select(step => step.Name));
    }

    [Fact]
    public async Task The_full_verification_that_the_fix_up_Task_passes_is_the_final_one_and_the_pull_requests_follow()
    {
        await using var overseer = await StartWithManifestsAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/api", "acme/web"]);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneOnce(Task.CompletedTask));
        // Fails the final verification and passes as the fix-up Task's. Were anything verified again, a flaky test would fail it.
        overseer.Sandboxes.ScriptCommandsContaining(
            ManifestCommandIn("acme/api", "npm test"),
            new CommandResult(1, "first"), new CommandResult(0, ""), new CommandResult(1, "1 test failed: flaky"));

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/api", "acme/web");

        await InReviewAsync(overseer, address);
        await StatesAsync(overseer, address, "Done", "Done");
        Assert.Equal(
            2, overseer.Sandboxes.CommandsRun.Count(run => run.Command.Contains(ManifestCommandIn("acme/api", "npm test"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Once_the_fix_up_Task_has_passed_a_Retry_only_opens_the_pull_requests_also_after_a_restart(bool restarted)
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        var first = await StartWithManifestsAsync(connectionString, ["acme/web"]);
        var overseer = first;
        try
        {
            overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneOnce(Task.CompletedTask));
            // The Task's own verification passes, the final one fails, and the fix-up Task's passes.
            overseer.Sandboxes.ScriptCommandsContaining(
                ManifestCommandIn("acme/web", "npm test"), new CommandResult(0, ""), new CommandResult(1, "failed"), new CommandResult(0, ""));
            overseer.Sandboxes.ScriptCommandsContaining(IntentBranchComparedIn("acme/web"), new CommandResult(0, "src/orders.ts\0"));
            overseer.GitHub.RefusePullRequests("acme/web", "Server Error (HTTP 500)");
            var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
            var failed = await ImplementationTests.FailedAsync(overseer, address);
            Assert.StartsWith("The pull request of acme/web could not be opened", failed.Status);
            await StatesAsync(overseer, address, "Done", "Done");
            if (restarted)
            {
                await overseer.DisposeAsync();
                overseer = await StartWithManifestsAsync(connectionString, []);
                overseer.GitHub.AddRepository("acme/web");
            }
            overseer.GitHub.AllowPullRequests("acme/web");
            overseer.GitHub.AddBranch("acme/web", BranchOf(address));
            var commands = overseer.Sandboxes.CommandsRun.Count;
            var sessions = SquadSessionsStarted(overseer).Count;

            using var retried = await ImplementationTests.RetryAsync(overseer, address);

            await InReviewAsync(overseer, address);
            Assert.Single(overseer.GitHub.PullRequests);
            Assert.Equal(sessions, SquadSessionsStarted(overseer).Count);
            var since = overseer.Sandboxes.CommandsRun.Skip(commands).ToList();
            Assert.DoesNotContain(since, run => run.Command.Contains("\nnpm test"));
            // Only after a restart is there a fresh Sandbox to prepare; the one from before serves otherwise.
            Assert.Equal(restarted, since.Count > 0);
            Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await overseer.DisposeAsync();
            if (overseer != first)
            {
                await first.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_fix_up_Task_whose_changed_repositories_cant_be_told_isnt_Done_and_Retry_goes_on_to_the_pull_requests()
    {
        await using var overseer = await StartWithManifestsAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"]);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneOnce(Task.CompletedTask));
        overseer.Sandboxes.ScriptCommandsContaining(
            ManifestCommandIn("acme/web", "npm test"), new CommandResult(0, ""), new CommandResult(1, "failed"), new CommandResult(0, ""));
        // git can't say what the Intent branch changes, once.
        overseer.Sandboxes.ScriptCommandsContaining(
            IntentBranchComparedIn("acme/web"), new CommandResult(128, "fatal: bad object"), new CommandResult(0, ""));

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        var failed = await ImplementationTests.FailedAsync(overseer, address);
        Assert.Equal(
            "Comparing the Intent branch of acme/web with its default branch failed with exit code 128:\nfatal: bad object",
            failed.Status);
        await StatesAsync(overseer, address, "Done", "Pending");
        var sessions = SquadSessionsStarted(overseer).Count;

        using var retried = await ImplementationTests.RetryAsync(overseer, address);

        await InReviewAsync(overseer, address);
        await StatesAsync(overseer, address, "Done", "Done");
        Assert.Equal(sessions, SquadSessionsStarted(overseer).Count);
        // The Task's verification, the final one, and the fix-up Task's: nothing was verified again.
        Assert.Equal(3, overseer.Sandboxes.CommandsRun.Count(run => run.Command.Contains(ManifestCommandIn("acme/web", "npm test"))));
    }

    [Fact]
    public async Task A_final_verification_that_cant_be_run_adds_no_fix_up_Task_and_Retry_runs_it_again()
    {
        await using var overseer = await StartWithManifestsAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"]);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneOnce(Task.CompletedTask));
        // git can't say what the Intent branch changes, once.
        overseer.Sandboxes.ScriptCommandsContaining(
            IntentBranchComparedIn("acme/web"), new CommandResult(128, "fatal: bad object"), new CommandResult(0, ""));

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        var failed = await ImplementationTests.FailedAsync(overseer, address);
        Assert.Equal(
            "The final verification could not be run: Comparing the Intent branch of acme/web with its default branch failed "
            + "with exit code 128:\nfatal: bad object",
            failed.Status);
        await StatesAsync(overseer, address, "Done");

        using var retried = await ImplementationTests.RetryAsync(overseer, address);

        await InReviewAsync(overseer, address);
        await StatesAsync(overseer, address, "Done");
    }

    [Fact]
    public async Task A_fix_up_Task_run_again_is_verified_by_the_full_verification_again()
    {
        await using var overseer = await StartWithManifestsAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/api", "acme/web"]);
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer,
            DoneOnce(Task.CompletedTask),
            new FakeAgentSession { StartFailure = "opencode is not installed." },
            DoneOnce(Task.CompletedTask));
        // The failing repository is acme/web, so that acme/api is verified only because the verification is the full one. The first
        // Task's own verification passes, and the final one after it fails.
        overseer.Sandboxes.ScriptCommandsContaining(
            ManifestCommandIn("acme/web", "npm test"), new CommandResult(0, ""), new CommandResult(1, "1 test failed"), new CommandResult(0, ""));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/api", "acme/web");
        await ImplementationTests.FailedAsync(overseer, address);
        await StatesAsync(overseer, address, "Done", "Pending");

        using var retried = await ImplementationTests.RetryAsync(overseer, address);

        var page = await InReviewAsync(overseer, address);
        await StatesAsync(overseer, address, "Done", "Done");
        Assert.Equal(
            [
                "The build command of acme/api", "The test command of acme/api",
                "The build command of acme/web", "The test command of acme/web",
            ],
            Assert.Single(VerificationTests.AttemptsOn(page, 2)).Steps.Select(step => step.Name));
    }

    /// <summary>
    /// Has git find that the repository's clone holds these files differently from its default branch, each time it is asked.
    /// </summary>
    private static void HoldsMoreThanItsDefaultBranch(RunningOverseer overseer, string repository, params string[] files) =>
        overseer.Sandboxes.ScriptCommandsContaining(
            $"{repository}' || exit\nerrors=$(mktemp) || exit\n{{\nbase=$(git merge-base refs/remotes/origin/HEAD HEAD",
            new CommandResult(0, string.Concat(files.Select(file => file + "\0"))));

    /// <summary>What the command that asks what the repository's Intent branch changes of its default branch holds.</summary>
    internal static string IntentBranchComparedIn(string repository) =>
        $"{repository}' || exit\nerrors=$(mktemp) || exit\n{{\nbase=$(git merge-base refs/remotes/origin/HEAD refs/overseer/verified";

    /// <summary>Makes the command fail in this repository the next time it runs, with this output, and pass from then on.</summary>
    private static void FailsOnce(RunningOverseer overseer, string repository, string command, string output) =>
        overseer.Sandboxes.ScriptCommandsContaining(ManifestCommandIn(repository, command), new CommandResult(1, output), new CommandResult(0, ""));

    /// <summary>What the script of a manifest's command that starts with <paramref name="command"/> holds, in this repository.</summary>
    internal static string ManifestCommandIn(string repository, string command) =>
        $"{repository}' || exit\nlog=$(mktemp) || exit\n(\nset -e\n{command}";

    /// <summary>Overseer with these repositories configured, each with a Repository Manifest in every Sandbox that holds it.</summary>
    internal static async Task<RunningOverseer> StartWithManifestsAsync(string connectionString, string[] repositories, TimeProvider? time = null)
    {
        var overseer = await StartWithRepositoriesAsync(connectionString, repositories, time);
        foreach (var repository in repositories)
        {
            overseer.Sandboxes.AddRepositoryFile(repository, ".overseer.yml", Manifest);
        }
        return overseer;
    }

    /// <summary>Waits for the Intent's detail page to show it In Review, and gives that page.</summary>
    internal static Task<string> InReviewAsync(RunningOverseer overseer, string address) =>
        EventuallyAsync(
            async () => await GetAsync(overseer, address) is var page && ElementText(page, "intent-state") == "In Review" ? page : null,
            "the Intent to be In Review");
}
