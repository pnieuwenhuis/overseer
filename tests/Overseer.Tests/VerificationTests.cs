using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using Overseer.Sandboxes;
using static Overseer.Tests.AgentTurns;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;
using static Overseer.Tests.TaskTests;

namespace Overseer.Tests;

/// <summary>
/// Verify, then push: when an agent says its Task is done, Overseer builds and tests the work itself, and only verified work is
/// pushed to the Intent branch and makes the Task Done.
/// </summary>
public class VerificationTests(PostgresServer postgres)
{
    private const string Manifest = "setup: npm ci\nbuild: npm run build\ntest: npm test\n";

    private static readonly CommandResult Passes = new(0, "");

    [Fact]
    public async Task Tests_that_fail_twice_and_then_pass_go_back_to_the_same_session_each_time_with_the_command_and_its_output_and_the_Task_is_Done()
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        overseer.Sandboxes.ScriptCommandsContaining(
            "npm test", new CommandResult(1, "1 test failed: exports nothing"), new CommandResult(1, "1 test failed: no header"), Passes);

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        var task = Assert.Single(await StatesAsync(overseer, address, "Done"));
        Assert.Equal(1, task.Number);
        // One session, told twice what failed.
        Assert.Single(SquadSessionsStarted(overseer));
        var messages = SquadMessages(overseer);
        Assert.Equal(3, messages.Count);
        Assert.Contains("The test command of acme/web failed with exit code 1.", messages[1].Message);
        Assert.Contains("npm test", messages[1].Message);
        Assert.Contains("1 test failed: exports nothing", messages[1].Message);
        Assert.Contains("1 test failed: no header", messages[2].Message);
        Assert.DoesNotContain("exports nothing", messages[2].Message);
        // Built, then tested, each time, and once more in the final verification.
        await FinalVerificationTests.InReviewAsync(overseer, address);
        Assert.Equal(
            ["build", "test", "build", "test", "build", "test", "build", "test"],
            CommandsIn(overseer, "acme/web").Select(command => command.Contains("npm run build") ? "build" : command.Contains("npm test") ? "test" : null)
                .OfType<string>());
        Assert.Equal(SquadSessionsStarted(overseer), SquadSessionsStopped(overseer));
    }

    [Fact]
    public async Task A_Task_that_never_passes_is_a_Failed_Task_after_3_attempts_which_turns_the_Intent_red_pushes_nothing_and_runs_no_further_Task()
    {
        await using var overseer = await StartAsync("acme/api", "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Designer, DesignerHandsIn.DesignsAtOnce());
        // The agent changed the repository, so that there would be something to push.
        Changes(overseer, "acme/web", "src/orders.ts");
        overseer.Sandboxes.ScriptCommandsContaining("npm test", new CommandResult(1, "1 test failed"));

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        var failed = await ImplementationTests.FailedAsync(overseer, address);
        Assert.Equal(
            "Task 1 is a Failed Task: its work didn't pass verification in 3 attempts. "
            + "In the last one, attempt 3: The test command of acme/web failed with exit code 1.",
            failed.Status);
        Assert.Equal(("Red", "Failed"), failed.Light);
        Assert.Equal(("Red", "Failed"), LightOn(await GetAsync(overseer, "/")));
        Assert.Equal("Implementing", ElementText(await GetAsync(overseer, address), "intent-state"));
        await StatesAsync(overseer, address, "Failed", "Pending", "Pending");
        Assert.Equal(3, CommandsIn(overseer, "acme/web").Count(command => command.Contains("npm test")));
        // The first message, then what failed after each attempt but the last.
        Assert.Equal(3, SquadMessages(overseer).Count);
        Assert.Equal([AgentRole.Designer], SquadSessionsStarted(overseer).Select(session => session.Role));
        Assert.Equal(SquadSessionsStarted(overseer), SquadSessionsStopped(overseer));
        Assert.DoesNotContain(overseer.Sandboxes.CommandsRun, run => run.Command.Contains("git commit") || run.Command.Contains("git push"));
        // Its work stays in the Sandbox.
        Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_implementations_Retry_is_not_offered_on_a_Failed_Task_and_does_nothing_if_posted_anyway()
    {
        await using var overseer = await StartAsync("acme/web");
        var verifying = new TaskCompletionSource();
        overseer.Sandboxes.HoldCommandsContaining("npm test", verifying.Task);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        overseer.Sandboxes.ScriptCommandsContaining("npm test", new CommandResult(1, "1 test failed"));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var opened = await overseer.OpenAsync(address);
        verifying.SetResult();

        var failed = await ImplementationTests.FailedAsync(overseer, address);

        Assert.Null(failed.RetryAction);
        var commands = overseer.Sandboxes.CommandsRun.Count;

        // As from a page opened while the implementation had failed for another reason.
        using var retried = await opened.PostAsync($"{address}/implementation/retry");

        Assert.Equal("Failed", ImplementationTests.ImplementationOn(await GetAsync(overseer, address))!.Phase);
        await StatesAsync(overseer, address, "Failed");
        Assert.Equal(commands, overseer.Sandboxes.CommandsRun.Count);
        Assert.Single(SquadSessionsStarted(overseer));
    }

    [Theory]
    [InlineData(1, "1 attempt")]
    [InlineData(5, "5 attempts")]
    public async Task The_number_of_verification_attempts_is_the_one_in_Settings(int allowed, string said)
    {
        await using var overseer = await StartAsync("acme/web");
        await SetVerificationAttemptsAsync(overseer, allowed);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        overseer.Sandboxes.ScriptCommandsContaining("npm run build", new CommandResult(2, "does not compile"));

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        var failed = await ImplementationTests.FailedAsync(overseer, address);
        Assert.Contains($"in {said}. In the last one, attempt {allowed}: The build command of acme/web failed with exit code 2.", failed.Status);
        Assert.Equal(allowed, CommandsIn(overseer, "acme/web").Count(command => command.Contains("npm run build")));
        Assert.Equal(allowed, SquadMessages(overseer).Count);
        // The first command that fails is the last one run.
        Assert.DoesNotContain(overseer.Sandboxes.CommandsRun, run => run.Command.Contains("npm test"));
    }

    [Fact]
    public async Task The_detail_page_shows_each_attempt_on_a_Task_with_the_commands_run_and_their_output_also_after_a_restart()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        IReadOnlyList<ShownAttempt> shown;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            first.Sandboxes.AddRepositoryFile("acme/web", ".overseer.yml", Manifest);
            first.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
            first.Sandboxes.ScriptCommandsContaining("npm run build", new CommandResult(0, "built <ok>"));
            first.Sandboxes.ScriptCommandsContaining("npm test", new CommandResult(1, "1 test failed"), new CommandResult(0, "12 tests passed"));
            address = await ImplementationTests.ImplementingAsync(first, "Export data", "acme/web");
            await StatesAsync(first, address, "Done");

            shown = AttemptsOn(await GetAsync(first, address), 1);
        }

        Assert.Equal(
            ["Attempt 1: failed. The test command of acme/web failed with exit code 1.", "Attempt 2: passed."],
            shown.Select(attempt => attempt.Heading));
        Assert.Equal(
            [
                new ShownStep("The build command of acme/web", "npm run build", "built <ok>"),
                new ShownStep("The test command of acme/web", "npm test", "1 test failed"),
            ],
            shown[0].Steps);
        Assert.Equal("12 tests passed", shown[1].Steps[^1].Output);
        await using var second = await StartWithRepositoriesAsync(connectionString, []);
        Assert.Equivalent(shown, AttemptsOn(await GetAsync(second, address), 1), strict: true);
    }

    [Fact]
    public async Task Each_new_session_on_a_Task_has_all_its_attempts_again_and_the_earlier_ones_stay()
    {
        await using var overseer = await StartAsync("acme/web");
        await SetVerificationAttemptsAsync(overseer, 2);
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer,
            // Breaks off while fixing what the first attempt found.
            new FakeAgentSession(
                new FakeAgentTurn([], new AgentOutcome.Done()),
                new FakeAgentTurn([], new AgentOutcome.Done()) { Failure = "The agent process exited with code 1." }),
            DoneAtOnce());
        overseer.Sandboxes.ScriptCommandsContaining(
            "npm test", new CommandResult(1, "first"), new CommandResult(1, "second"), new CommandResult(0, "third"));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var failed = await ImplementationTests.FailedAsync(overseer, address);
        Assert.Equal("The Developer's session for Task 1 failed: The agent process exited with code 1.", failed.Status);
        // Not a Failed Task: its session broke.
        await StatesAsync(overseer, address, "Pending");

        using var retried = await ImplementationTests.RetryAsync(overseer, address);

        await StatesAsync(overseer, address, "Done");
        Assert.Equal(
            [
                "Run 1, attempt 1: failed. The test command of acme/web failed with exit code 1.",
                "Run 2, attempt 1: failed. The test command of acme/web failed with exit code 1.",
                "Run 2, attempt 2: passed.",
            ],
            AttemptsOn(await GetAsync(overseer, address), 1).Select(attempt => attempt.Heading));
    }

    [Fact]
    public async Task What_setup_and_each_verification_write_is_set_aside_before_an_agent_goes_on_so_that_its_never_committed()
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        Changes(overseer, "acme/web", "src/orders.ts");
        overseer.Sandboxes.ScriptCommandsContaining("npm test", new CommandResult(1, "1 test failed"), Passes);

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        await FinalVerificationTests.InReviewAsync(overseer, address);
        string[] verification = ["look at the work", "npm run build", "npm test", "set aside"];
        Assert.Equal(
            [
                "npm ci", "set aside",
                // The Task's two attempts, the one that failed and the one that passed.
                .. verification, .. verification, "commit", "git push",
                // The final verification.
                .. verification,
            ],
            CommandsIn(overseer, "acme/web")
                .Select(command =>
                    command.Contains("git commit ") ? "commit"
                    : command.Contains("# The agent's work, as it is now.") ? "look at the work"
                    : command.Contains("# What Overseer's commands wrote.") ? "set aside"
                    : new[] { "npm ci", "npm run build", "npm test", "git push" }.FirstOrDefault(command.Contains))
                .OfType<string>());
    }

    [Fact]
    public async Task What_is_verified_is_every_repository_the_Task_lists_and_any_other_it_changed()
    {
        await using var overseer = await StartAsync("acme/api", "acme/docs", "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        // The Task lists acme/web alone, and changed acme/api too.
        Changes(overseer, "acme/api", "src/Orders.cs");

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/api", "acme/docs", "acme/web");

        await StatesAsync(overseer, address, "Done");
        Assert.Equal(
            [
                "The build command of acme/api", "The test command of acme/api",
                "The build command of acme/web", "The test command of acme/web",
            ],
            Assert.Single(AttemptsOn(await GetAsync(overseer, address), 1)).Steps.Select(step => step.Name));
    }

    [Fact]
    public async Task Verified_work_is_committed_with_the_Tasks_title_and_pushed_in_every_repository_it_changed_and_only_then_is_the_Task_Done_and_the_next_started()
    {
        await using var overseer = await StartAsync("acme/api", "acme/docs", "acme/web");
        var pushing = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Designer, DesignerHandsIn.DesignsAtOnce());
        Changes(overseer, "acme/api", "src/Orders.cs");
        Changes(overseer, "acme/web", "src/orders.ts");
        overseer.Sandboxes.HoldCommandsContaining("git push", pushing.Task);

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/docs", "acme/web");

        await EventuallyAsync(
            () => Task.FromResult(overseer.Sandboxes.CommandsRun.Any(run => run.Command.Contains("git push")) ? "" : null), "a push");
        // Verified, but not Done until GitHub has the work.
        await StatesAsync(overseer, address, "Running", "Pending", "Pending");
        Assert.Single(SquadSessionsStarted(overseer));
        Assert.Equal(SquadSessionsStarted(overseer), SquadSessionsStopped(overseer));

        pushing.SetResult();

        await StatesAsync(overseer, address, "Done", "Running", "Pending");
        var branch = BranchOf(address);
        Assert.All(["acme/api", "acme/web"], repository =>
        {
            var commands = CommandsIn(overseer, repository);
            var commit = Assert.Single(commands, command => command.Contains("git commit"));
            Assert.Contains("--message 'Design the Export button'", commit);
            var push = Assert.Single(commands, command => command.Contains("git push"));
            Assert.Contains($"origin 'refs/heads/{branch}:refs/heads/{branch}'", push);
            // Only once it is verified.
            Assert.True(commands.ToList().IndexOf(commit) > commands.ToList().FindIndex(command => command.Contains("npm test")));
            Assert.True(commands.ToList().IndexOf(push) > commands.ToList().IndexOf(commit));
        });
        // acme/docs holds no change.
        Assert.DoesNotContain(CommandsIn(overseer, "acme/docs"), command => command.Contains("git commit") || command.Contains("git push"));
        // Nothing is committed or pushed before everything has passed.
        var run = overseer.Sandboxes.CommandsRun.Select(run => run.Command).ToList();
        Assert.True(run.FindIndex(command => command.Contains("git commit")) > run.FindLastIndex(command => command.Contains("npm test")));
    }

    [Fact]
    public async Task A_Task_that_changed_nothing_pushes_nothing()
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        await StatesAsync(overseer, address, "Done");
        Assert.DoesNotContain(overseer.Sandboxes.CommandsRun, run => run.Command.Contains("git commit") || run.Command.Contains("git push"));
    }

    [Theory]
    [InlineData(".overseer.yml")]
    [InlineData("docker/compose.yml")]
    public async Task When_the_Task_changed_a_manifest_or_its_Compose_file_setup_and_the_Compose_services_are_brought_up_to_date_before_the_build(
        string changed)
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.AddRepositoryFile("acme/web", ".overseer.yml", Manifest + "compose: ./docker/compose.yml\n");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        Changes(overseer, "acme/web", "src/orders.ts", changed);

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        await StatesAsync(overseer, address, "Done");
        var steps = Assert.Single(AttemptsOn(await GetAsync(overseer, address), 1)).Steps;
        Assert.Equal(
            [
                "The setup command of acme/web", "Starting the Compose services of acme/web", "The build command of acme/web",
                "The test command of acme/web",
            ],
            steps.Select(step => step.Name));
        Assert.StartsWith("docker compose --file './docker/compose.yml' up --detach --wait", steps[1].Command);
    }

    [Fact]
    public async Task Setup_and_the_Compose_services_are_left_alone_when_neither_the_manifest_nor_its_Compose_file_changed()
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.AddRepositoryFile("acme/web", ".overseer.yml", Manifest + "compose: compose.yml\n");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        Changes(overseer, "acme/web", "src/compose.yml", "docs/.overseer.yml");

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        await StatesAsync(overseer, address, "Done");
        // Once each, as the Sandbox was prepared.
        Assert.Single(CommandsIn(overseer, "acme/web"), command => command.Contains("npm ci"));
        Assert.Single(CommandsIn(overseer, "acme/web"), command => command.Contains("docker compose"));
    }

    [Fact]
    public async Task A_Compose_file_that_a_Task_adds_is_started_in_a_project_that_replaces_no_other_repositorys_services()
    {
        await using var overseer = await StartAsync("acme/api", "acme/web");
        overseer.Sandboxes.AddRepositoryFile("acme/api", ".overseer.yml", Manifest + "compose: compose.yml\n");
        overseer.Sandboxes.ScriptCommandsContaining("config --format json", new CommandResult(0, """{ "name": "shop" }"""));
        // The Task gives acme/web a Compose file, whose project Compose would name like the one of acme/api.
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done())
        {
            Writes = new Dictionary<string, string>
            {
                ["/home/overseer/repositories/acme/web/.overseer.yml"] = Manifest + "compose: compose.yml\n",
            },
        }));
        Changes(overseer, "acme/web", ".overseer.yml", "compose.yml");

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/api", "acme/web");

        await StatesAsync(overseer, address, "Done");
        Assert.Contains(
            "--project-name 'shop-acme-web' up",
            Assert.Single(AttemptsOn(await GetAsync(overseer, address), 1)).Steps
                .Single(step => step.Name == "Starting the Compose services of acme/web").Command);
        // acme/api, which the Task didn't change, runs on: its services were started once, as the Sandbox was prepared.
        Assert.Single(CommandsIn(overseer, "acme/api"), command => command.Contains(" up --detach"));
    }

    [Theory]
    [InlineData("npm ci", "The setup command of acme/web failed with exit code 1.")]
    [InlineData("docker compose", "Starting the Compose services of acme/web failed with exit code 1.")]
    public async Task A_setup_or_Compose_start_that_fails_again_is_a_failed_verification(string command, string failure)
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.AddRepositoryFile("acme/web", ".overseer.yml", Manifest + "compose: compose.yml\n");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        Changes(overseer, "acme/web", ".overseer.yml");
        // Fine as the Sandbox is prepared, broken by the Task, then fixed.
        overseer.Sandboxes.ScriptCommandsContaining(command, Passes, new CommandResult(1, "broken"), Passes);

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        await StatesAsync(overseer, address, "Done");
        var attempts = AttemptsOn(await GetAsync(overseer, address), 1);
        Assert.Equal([$"Attempt 1: failed. {failure}", "Attempt 2: passed."], attempts.Select(attempt => attempt.Heading));
        Assert.Equal("broken", attempts[0].Steps[^1].Output);
        Assert.Contains(failure, SquadMessages(overseer)[1].Message);
    }

    [Fact]
    public async Task A_repository_without_a_Repository_Manifest_is_skipped_and_its_work_is_pushed()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        Changes(overseer, "acme/web", "README.md");

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        await StatesAsync(overseer, address, "Done");
        Assert.Empty(Assert.Single(AttemptsOn(await GetAsync(overseer, address), 1)).Steps);
        Assert.Single(CommandsIn(overseer, "acme/web"), command => command.Contains("git push"));
    }

    [Fact]
    public async Task The_manifest_is_read_as_it_is_after_the_turn_and_an_invalid_one_is_a_failed_verification()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        const string path = "/home/overseer/repositories/acme/web/.overseer.yml";
        // The Task creates the manifest, wrong at first.
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            new FakeAgentTurn([], new AgentOutcome.Done()) { Writes = new Dictionary<string, string> { [path] = "build: make\n" } },
            new FakeAgentTurn([], new AgentOutcome.Done()) { Writes = new Dictionary<string, string> { [path] = Manifest } }));
        Changes(overseer, "acme/web", ".overseer.yml");

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        await StatesAsync(overseer, address, "Done");
        var attempts = AttemptsOn(await GetAsync(overseer, address), 1);
        Assert.Equal(
            ["Attempt 1: failed. The Repository Manifest of acme/web (.overseer.yml) is invalid.", "Attempt 2: passed."],
            attempts.Select(attempt => attempt.Heading));
        Assert.Contains("- `test` is missing.", Assert.Single(attempts[0].Steps).Output);
        Assert.Contains("- `test` is missing.", SquadMessages(overseer)[1].Message);
        Assert.Equal(
            ["The setup command of acme/web", "The build command of acme/web", "The test command of acme/web"],
            attempts[1].Steps.Select(step => step.Name));
    }

    [Theory]
    [InlineData("npm run build", "The build command of acme/web didn't finish within 30 minutes.")]
    [InlineData("npm test", "The test command of acme/web didn't finish within 30 minutes.")]
    public async Task A_command_that_exceeds_its_time_limit_is_a_failed_verification(string command, string failure)
    {
        var time = new FakeTimeProvider();
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        overseer.Sandboxes.AddRepositoryFile("acme/web", ".overseer.yml", Manifest);
        await SetVerificationAttemptsAsync(overseer, 1);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        overseer.Sandboxes.HoldCommandsContaining(command, new TaskCompletionSource().Task);
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await EventuallyAsync(
            () => Task.FromResult(overseer.Sandboxes.CommandsRun.Any(run => run.Command.Contains(command)) ? "" : null), "the command to run");

        time.Advance(TimeSpan.FromMinutes(31));

        var failed = await ImplementationTests.FailedAsync(overseer, address);
        Assert.EndsWith($"In the last one, attempt 1: {failure}", failed.Status);
        await StatesAsync(overseer, address, "Failed");
    }

    [Fact]
    public async Task A_Sandbox_that_no_longer_answers_is_no_attempt_it_fails_the_implementation_and_Retry_runs_the_Task_again()
    {
        await using var overseer = await StartAsync("acme/web");
        var working = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer,
            new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done()) { EndsAfter = working.Task }),
            new FakeAgentSession(FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await SquadMessagesAsync(overseer, 1);
        // Gone while the agent works.
        await overseer.Sandboxes.DestroyAsync(SquadMessages(overseer)[0].Sandbox, TestContext.Current.CancellationToken);

        working.SetResult();

        var failed = await ImplementationTests.FailedAsync(overseer, address);
        Assert.StartsWith("Task 1 could not be verified: ", failed.Status);
        Assert.Contains("does not exist", failed.Status);
        await StatesAsync(overseer, address, "Pending");
        Assert.Empty(AttemptsOn(await GetAsync(overseer, address), 1));
        using var retried = await ImplementationTests.RetryAsync(overseer, address);
        await SquadMessagesAsync(overseer, 2);
    }

    [Fact]
    public async Task A_push_that_fails_turns_the_Intent_red_with_GitHubs_error_and_Retry_only_pushes_again()
    {
        await using var overseer = await StartAsync("acme/api", "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Designer, DesignerHandsIn.DesignsAtOnce());
        Changes(overseer, "acme/api", "src/Orders.cs");
        Changes(overseer, "acme/web", "src/orders.ts");
        const string refused = "remote: Permission to acme/web.git denied to octocat.";
        // acme/api, which comes first, is pushed; acme/web is refused once.
        overseer.Sandboxes.ScriptCommandsContaining("acme/web' || exit\ngit push", new CommandResult(128, refused), Passes);

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        var failed = await ImplementationTests.FailedAsync(overseer, address);
        var branch = BranchOf(address);
        Assert.Equal($"Pushing the Intent branch {branch} of acme/web failed with exit code 128:\n{refused}", failed.Status);
        Assert.Equal(("Red", "Failed"), failed.Light);
        // Not Done, and no Task after it.
        await StatesAsync(overseer, address, "Pending", "Pending", "Pending");
        Assert.Contains("Verified, not pushed yet", await GetAsync(overseer, address));
        var run = overseer.Sandboxes.CommandsRun.Count;
        var sandbox = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));

        using var retried = await ImplementationTests.RetryAsync(overseer, address);

        await StatesAsync(overseer, address, "Done", "Running", "Pending");
        // Task 1 is pushed again, both repositories, with nothing else run for it, and no new session on it.
        var again = overseer.Sandboxes.CommandsRun.Skip(run).Take(2).ToList();
        Assert.All(again, command => Assert.Contains("git push", command.Command));
        Assert.All(again, command => Assert.Equal(sandbox, command.Sandbox));
        Assert.Single(SquadSessionsStarted(overseer), session => session.Role == AgentRole.Designer);
        Assert.Single(AttemptsOn(await GetAsync(overseer, address), 1));
        Assert.DoesNotContain("Verified, not pushed yet", await GetAsync(overseer, address));
    }

    [Fact]
    public async Task A_rejected_push_puts_the_Task_back_to_Pending_for_a_new_agent_that_is_told_why_and_what_it_then_does_is_verified_and_pushed()
    {
        await using var overseer = await StartAsync("acme/web");
        var integrating = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer,
            DoneAtOnce(),
            new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done()) { EndsAfter = integrating.Task }));
        Changes(overseer, "acme/web", "src/orders.ts");
        const string rejection = "! [rejected]        overseer -> overseer (fetch first)\nerror: failed to push some refs";
        overseer.Sandboxes.ScriptCommandsContaining("git push", new CommandResult(1, rejection), Passes);
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var failed = await ImplementationTests.FailedAsync(overseer, address);
        var branch = BranchOf(address);
        Assert.Equal($"Pushing the Intent branch {branch} of acme/web failed with exit code 1:\n{rejection}", failed.Status);
        // Not a Failed Task: it is back to Pending, for an agent to deal with.
        await StatesAsync(overseer, address, "Pending");
        var page = await GetAsync(overseer, address);
        Assert.Equal("Push rejected: an agent has to deal with it", ElementText(page, "intent-task-push"));
        var sandbox = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("# The push was rejected", Assert.Single(SquadMessages(overseer)).Message);

        using var retried = await ImplementationTests.RetryAsync(overseer, address);

        var message = (await SquadMessagesAsync(overseer, 2))[1];
        // In the same Sandbox, which holds the work.
        Assert.Equal(sandbox, message.Sandbox);
        Assert.Contains("# The push was rejected", message.Message);
        Assert.Contains(rejection.ReplaceLineEndings("\n"), message.Message.ReplaceLineEndings("\n"));
        Assert.Contains($"git fetch origin", message.Message);
        Assert.Contains($"merge `origin/{branch}`", message.Message);
        Assert.Contains("never force push", message.Message);
        // Nothing is pushed again before the agent is done.
        Assert.Single(CommandsIn(overseer, "acme/web"), command => command.Contains("git push"));

        integrating.SetResult();

        await FinalVerificationTests.InReviewAsync(overseer, address);
        await StatesAsync(overseer, address, "Done");
        Assert.Equal(
            ["Run 1, attempt 1: passed.", "Run 2, attempt 1: passed."],
            AttemptsOn(await GetAsync(overseer, address), 1).Select(attempt => attempt.Heading));
        Assert.Equal(2, CommandsIn(overseer, "acme/web").Count(command => command.Contains("git push")));
        Assert.DoesNotContain("--force", string.Concat(CommandsIn(overseer, "acme/web").Where(command => command.Contains("git push"))));
        Assert.DoesNotContain("Push rejected", await GetAsync(overseer, address));
    }

    [Fact]
    public async Task A_session_after_a_rejected_push_that_breaks_off_leaves_the_next_one_told_of_it_too()
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer,
            DoneAtOnce(),
            new FakeAgentSession { StartFailure = "opencode is not installed." },
            new FakeAgentSession(FakeAgentTurn.Endless()));
        Changes(overseer, "acme/web", "src/orders.ts");
        overseer.Sandboxes.ScriptCommandsContaining("git push", new CommandResult(1, "pre-push hook declined"));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await ImplementationTests.FailedAsync(overseer, address);
        using var retried = await ImplementationTests.RetryAsync(overseer, address);
        await EventuallyAsync(
            async () => ImplementationTests.ImplementationOn(await GetAsync(overseer, address)) is { Phase: "Failed", Status: var status }
                && status.Contains("opencode is not installed") ? status : null,
            "the session to fail");

        using var again = await ImplementationTests.RetryAsync(overseer, address);

        Assert.Contains("pre-push hook declined", (await SquadMessagesAsync(overseer, 2))[1].Message);
    }

    [Theory]
    [InlineData(false, 128)]
    [InlineData(true, 128)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public async Task Verified_work_that_was_never_pushed_is_lost_with_its_Sandbox_and_Retry_carries_the_Task_out_again(
        bool restarted, int pushExitCode)
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]);
        var overseer = first;
        try
        {
            overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
            Changes(overseer, "acme/web", "src/orders.ts");
            overseer.Sandboxes.ScriptCommandsContaining("git push", new CommandResult(pushExitCode, "refused"));
            var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
            await ImplementationTests.FailedAsync(overseer, address);
            var old = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
            if (restarted)
            {
                await overseer.DisposeAsync();
                overseer = await StartWithRepositoriesAsync(connectionString, []);
                // From the moment the Portal answers.
                Assert.DoesNotContain("Verified, not pushed yet", await GetAsync(overseer, address));
            }
            else
            {
                // Deleted behind Overseer's back.
                await overseer.Sandboxes.DestroyAsync(old, TestContext.Current.CancellationToken);
            }
            var sessions = SquadSessionsStarted(overseer).Count;

            using var retried = await ImplementationTests.RetryAsync(overseer, address);

            var message = await EventuallyAsync(
                () => Task.FromResult(SquadMessages(overseer).LastOrDefault(message => message.Sandbox != old)), "the Task to be carried out again");
            Assert.Contains("Your Task is Task 1 of 1", message.Message);
            // What was rejected went with the Sandbox that held it.
            Assert.DoesNotContain("# The push was rejected", message.Message);
            Assert.True(SquadSessionsStarted(overseer).Count > sessions);
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
    public async Task A_clone_in_scope_that_isnt_on_the_Intent_branch_is_a_failed_verification_which_tells_the_agent_and_nothing_is_built()
    {
        await using var overseer = await StartAsync("acme/api", "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        Changes(overseer, "acme/web", "src/orders.ts");
        // acme/api, which the Task neither lists nor changed, is left on main, until the agent puts it back.
        overseer.Sandboxes.ScriptCommandsContaining(BranchAskedIn("acme/api"), new CommandResult(0, "main\n"), Passes);

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/api", "acme/web");

        await StatesAsync(overseer, address, "Done");
        var branch = BranchOf(address);
        var failure = $"acme/api isn't on the Intent branch {branch}, but on main. Check out the Intent branch again, bringing "
            + "along what belongs on it, without forcing it to another commit.";
        var attempts = AttemptsOn(await GetAsync(overseer, address), 1);
        Assert.Equal([$"Attempt 1: failed. {failure}", "Attempt 2: passed."], attempts.Select(attempt => attempt.Heading));
        // Nothing is built or tested on the wrong branch.
        Assert.Equal(["The Intent branch of acme/api"], attempts[0].Steps.Select(step => step.Name));
        Assert.Contains(failure, SquadMessages(overseer)[1].Message);
    }

    /// <summary>What the command that asks which branch the repository's clone is on holds.</summary>
    private static string BranchAskedIn(string repository) =>
        $"{repository}' || exit\nerrors=$(mktemp) || exit\n{{\nhead=$(git symbolic-ref";

    [Fact]
    public async Task Work_that_cant_be_committed_fails_the_implementation_with_what_git_said_and_nothing_is_pushed()
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        Changes(overseer, "acme/web", "src/orders.ts");
        overseer.Sandboxes.ScriptCommandsContaining("git commit", new CommandResult(128, "fatal: unable to write new index file"));

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        var failed = await ImplementationTests.FailedAsync(overseer, address);
        Assert.Equal("Committing the work in acme/web failed with exit code 128:\nfatal: unable to write new index file", failed.Status);
        await StatesAsync(overseer, address, "Pending");
        Assert.DoesNotContain(overseer.Sandboxes.CommandsRun, run => run.Command.Contains("git push"));
    }

    [Theory]
    [InlineData("npm test")]
    [InlineData("git push")]
    public async Task What_verification_and_pushing_print_never_puts_the_GitHub_token_on_the_page_in_the_database_or_back_to_the_agent(
        string command)
    {
        await using var overseer = await StartAsync("acme/web");
        await SetVerificationAttemptsAsync(overseer, 2);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        Changes(overseer, "acme/web", "src/orders.ts");
        overseer.Sandboxes.ScriptCommandsContaining(command, new CommandResult(1, $"Authorization: Bearer {Token} refused"));

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        await ImplementationTests.FailedAsync(overseer, address);
        var page = await GetAsync(overseer, address);
        Assert.Contains("Bearer *** refused", page);
        Assert.DoesNotContain(Token, page);
        Assert.DoesNotContain(await overseer.DumpDatabaseAsync(), table => table.Contains(Token));
        Assert.DoesNotContain(SquadMessages(overseer), message => message.Message.Contains(Token));
    }

    private const string NewToken = "github_pat_11REPLACEMENT9876543210";

    [Fact]
    public async Task A_GitHub_token_replaced_in_Settings_reaches_the_prepared_Sandbox_before_the_push_is_retried()
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        Changes(overseer, "acme/web", "src/orders.ts");
        overseer.Sandboxes.ScriptCommandsContaining(
            "git push", new CommandResult(128, "fatal: Authentication failed"), Passes);
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await ImplementationTests.FailedAsync(overseer, address);
        var sandbox = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        await ReplaceTokenAsync(overseer);
        var pushing = new TaskCompletionSource();
        overseer.Sandboxes.HoldCommandsContaining("git push", pushing.Task);

        using var retried = await ImplementationTests.RetryAsync(overseer, address);

        await EventuallyAsync(
            () => Task.FromResult(CommandsIn(overseer, "acme/web").Count(command => command.Contains("git push")) == 2 ? "" : null),
            "the push to be retried");
        Assert.Equal(NewToken, overseer.Sandboxes.GitHubTokenOf(sandbox));
        pushing.SetResult();
        await FinalVerificationTests.InReviewAsync(overseer, address);
    }

    [Fact]
    public async Task A_GitHub_token_replaced_while_an_agent_works_reaches_the_Sandbox_before_the_push_and_neither_token_is_ever_shown()
    {
        await using var overseer = await StartAsync("acme/web");
        await SetVerificationAttemptsAsync(overseer, 2);
        var working = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer, new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done()) { EndsAfter = working.Task }));
        Changes(overseer, "acme/web", "src/orders.ts");
        // As a log in the Sandbox holds the token it had before, besides the one it has now.
        overseer.Sandboxes.ScriptCommandsContaining("git push", new CommandResult(128, $"tried {Token} and {NewToken}"));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var sandbox = (await SquadMessagesAsync(overseer, 1))[0].Sandbox;
        // The session starts with the token in use.
        Assert.Equal(Token, overseer.Sandboxes.GitHubTokenOf(sandbox));
        await ReplaceTokenAsync(overseer);

        working.SetResult();

        var failed = await ImplementationTests.FailedAsync(overseer, address);
        Assert.Equal(NewToken, overseer.Sandboxes.GitHubTokenOf(sandbox));
        Assert.EndsWith("tried *** and ***", failed.Status);
        var database = await overseer.DumpDatabaseAsync();
        Assert.DoesNotContain(database, table => table.Contains(Token) || table.Contains(NewToken));
    }

    [Fact]
    public async Task A_Retry_without_a_GitHub_token_says_to_set_one()
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        Changes(overseer, "acme/web", "src/orders.ts");
        overseer.Sandboxes.ScriptCommandsContaining("git push", new CommandResult(128, "refused"));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await ImplementationTests.FailedAsync(overseer, address);
        using var removed = await overseer.SubmitFormAsync("/settings", "github-token-remove", new Dictionary<string, string>());

        using var retried = await ImplementationTests.RetryAsync(overseer, address);

        await EventuallyAsync(
            async () => ImplementationTests.ImplementationOn(await GetAsync(overseer, address)) is
            {
                Phase: "Failed",
                Status: "The Sandbox could not be given the GitHub token: No GitHub token is set: set one in Settings.",
            }
                ? ""
                : null,
            "the Retry to say there is no token");
        Assert.Single(CommandsIn(overseer, "acme/web"), command => command.Contains("git push"));
    }

    /// <summary>Saves another GitHub token on the Settings page, which GitHub accepts.</summary>
    private static async Task ReplaceTokenAsync(RunningOverseer overseer)
    {
        overseer.GitHub.AcceptToken(NewToken, "octocat");
        using var saved = await overseer.SubmitFormAsync(
            "/settings", "github-token", new Dictionary<string, string> { ["GitHubToken"] = NewToken });
    }

    /// <summary>Saves this number of verification attempts per Task on the Settings page.</summary>
    private static Task SetVerificationAttemptsAsync(RunningOverseer overseer, int attempts) =>
        SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: attempts);

    /// <summary>
    /// Has git find these files of the repository changed since its last verified work, each time it is asked. The fake Sandbox
    /// Provider has no git, so without this nothing is ever changed.
    /// </summary>
    internal static void Changes(RunningOverseer overseer, string repository, params string[] files) =>
        overseer.Sandboxes.ScriptCommandsContaining(ChangesAskedIn(repository), Changed(files));

    /// <summary>What a command that asks the repository's changed files holds.</summary>
    private static string ChangesAskedIn(string repository) =>
        $"{repository}' || exit\nerrors=$(mktemp) || exit\n{{\n(\nset -e\n# The agent's work, as it is now.";

    /// <summary>What git answers when these files changed.</summary>
    private static CommandResult Changed(params string[] files) => new(0, string.Concat(files.Select(file => file + "\0")));

    /// <summary>The verification attempts on the Task as the Intent's detail page shows them, in its order.</summary>
    internal static IReadOnlyList<ShownAttempt> AttemptsOn(string page, int task) =>
        [.. Regex.Matches(page, $"<details class=\"task-attempt\" data-task=\"{task}\"[^>]*>(.*?)</details>", RegexOptions.Singleline)
            .Select(attempt => new ShownAttempt(
                Collapsed(ElementText(attempt.Groups[1].Value, "task-attempt-heading")),
                [.. Regex.Matches(attempt.Groups[1].Value, "<div class=\"verification-step\"[^>]*>(.*?)</div>", RegexOptions.Singleline)
                    .Select(step => new ShownStep(
                        ElementText(step.Groups[1].Value, "verification-step-name"),
                        Shown(step.Groups[1].Value, "verification-command"),
                        Shown(step.Groups[1].Value, "verification-output")))]))];

    /// <summary>The text of the element of the given class; empty when there is none.</summary>
    private static string Shown(string html, string cssClass) =>
        html.Contains($"class=\"{cssClass}\"", StringComparison.Ordinal) ? ElementText(html, cssClass) : "";

    private static string Collapsed(string text) => Regex.Replace(text, @"\s+", " ");

    internal sealed record ShownAttempt(string Heading, IReadOnlyList<ShownStep> Steps);

    internal sealed record ShownStep(string Name, string Command, string Output);

    /// <summary>Overseer with these repositories configured, each with a Repository Manifest in every Sandbox that holds it.</summary>
    private async Task<RunningOverseer> StartAsync(params string[] repositories)
    {
        var overseer = await StartWithRepositoriesAsync(postgres, repositories);
        foreach (var repository in repositories)
        {
            overseer.Sandboxes.AddRepositoryFile(repository, ".overseer.yml", Manifest);
        }
        return overseer;
    }

    /// <summary>The commands run in the repository's directory in a Sandbox, in order.</summary>
    private static IReadOnlyList<string> CommandsIn(RunningOverseer overseer, string repository) =>
        [.. overseer.Sandboxes.CommandsRun
            .Where(run => run.Command.StartsWith($"cd '/home/overseer/repositories/{repository}' ", StringComparison.Ordinal))
            .Select(run => run.Command)];
}
