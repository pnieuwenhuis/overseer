using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Overseer.Sandboxes;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;
using static Overseer.Tests.TaskTests;

namespace Overseer.Tests;

/// <summary>
/// Recover a Failed Task: the human has it run again, with Retry, or changes it first, with Edit &amp; retry, from the Intent's
/// detail page.
/// </summary>
public class FailedTaskTests(PostgresServer postgres)
{
    private const string Manifest = "setup: npm ci\nbuild: npm run build\ntest: npm test\n";

    private static readonly CommandResult Passes = new(0, "");

    [Fact]
    public async Task The_last_failed_attempt_is_recorded_only_together_with_the_Task_becoming_a_Failed_Task_so_that_no_crash_parts_them()
    {
        await using var overseer = await StartAsync("acme/web");
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 1);
        var testing = new TaskCompletionSource();
        overseer.Sandboxes.HoldCommandsContaining("npm test", testing.Task);
        overseer.Sandboxes.ScriptCommandsContaining("npm test", new CommandResult(1, "exports nothing"));
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await EventuallyAsync(
            () => Task.FromResult(overseer.Sandboxes.CommandsRun.Any(run => run.Command.Contains("npm test")) ? "" : null),
            "the Task's tests to run");
        await using (var holder = overseer.OpenDatabase())
        {
            await using var transaction = await holder.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            // Holds up changes to the Task, but not the recording of an attempt on it.
            await holder.Database.ExecuteSqlAsync(
                $"""SELECT 1 FROM "IntentTasks" FOR NO KEY UPDATE""", TestContext.Current.CancellationToken);
            testing.SetResult();
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            waiting.CancelAfter(TimeSpan.FromSeconds(30));
            await DatabaseRaces.UntilWaitingOnLocksAsync(overseer, 1, waiting.Token);

            // What serve would find after stopping here.
            var page = await GetAsync(overseer, address);
            Assert.Equal(["Running"], TasksOn(page).Select(task => task.State));
            Assert.Empty(VerificationTests.AttemptsOn(page, 1));

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await ImplementationTests.FailedAsync(overseer, address);
        var failed = await GetAsync(overseer, address);
        Assert.Equal(["Failed"], TasksOn(failed).Select(task => task.State));
        Assert.Single(VerificationTests.AttemptsOn(failed, 1));
    }

    [Fact]
    public async Task Retry_runs_a_Failed_Task_again_in_a_new_session_in_the_same_Sandbox_told_what_failed_last_and_turns_the_light_green()
    {
        await using var overseer = await StartAsync("acme/web");
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 2);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce(), new FakeAgentSession(FakeAgentTurn.Endless()));
        overseer.Sandboxes.ScriptCommandsContaining(
            "npm test", new CommandResult(1, "exports nothing"), new CommandResult(1, "no header row"), Passes);
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await ImplementationTests.FailedAsync(overseer, address);
        var sandbox = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        var commands = overseer.Sandboxes.CommandsRun.Count;

        using var retried = await RetryTaskAsync(overseer, address, 1);

        // Back on the detail page, so reloading it doesn't retry again.
        Assert.Equal(address, retried.FinalPath());
        var again = (await SquadMessagesAsync(overseer, 3))[^1];
        Assert.Equal(2, SquadSessionsStarted(overseer).Count);
        Assert.Contains("Your Task is Task 1 of 1", again.Message);
        Assert.Contains("The test command of acme/web failed with exit code 1.", again.Message);
        Assert.Contains("npm test", again.Message);
        Assert.Contains("no header row", again.Message);
        Assert.DoesNotContain("exports nothing", again.Message);
        await StatesAsync(overseer, address, "Running");
        var page = await GetAsync(overseer, address);
        Assert.Equal(("Green", "Working"), LightOn(page));
        Assert.Equal(("Green", "Working"), LightOn(await GetAsync(overseer, "/")));
        Assert.Equal("Ready", ImplementationTests.ImplementationOn(page)!.Phase);
        // In the Sandbox the failed attempts left their work in, which nothing touches before the agent goes on.
        Assert.Equal(sandbox, again.Sandbox);
        Assert.Equal([sandbox], await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(commands, overseer.Sandboxes.CommandsRun.Count);
    }

    [Fact]
    public async Task Edit_and_retry_saves_the_changed_Task_and_runs_it_so_in_a_session_of_its_new_role_told_what_failed_last()
    {
        await using var overseer = await StartAsync("acme/api", "acme/web");
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 1);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        overseer.Sandboxes.ScriptCommandsContaining("npm test", new CommandResult(1, "exports nothing"), Passes);
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/api", "acme/web");
        await ImplementationTests.FailedAsync(overseer, address);
        var sandbox = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        var editAddress = RecoveryOn(await GetAsync(overseer, address), 1)!.EditAddress!;
        Assert.Equal($"{address}/tasks/1/edit", editAddress);
        // The editor holds the Task as it is.
        Assert.Equal(
            new EditedFields(
                "Add the export endpoint", "`GET /orders/export` returns every order as CSV.", "Developer", ["acme/web"]),
            await EditorAsync(overseer, editAddress), EditedFields.Comparer);

        using var saved = await EditAndRetryAsync(
            overseer, editAddress,
            new EditedFields("Design the export", "Show it with **placeholder** rows.\r\nNo filter.", "Designer", ["acme/api"]));

        Assert.Equal(address, saved.FinalPath());
        var again = (await SquadMessagesAsync(overseer, 2))[^1];
        Assert.Equal(AgentRole.Designer, again.Role);
        Assert.Equal(sandbox, again.Sandbox);
        Assert.Contains(
            "Your Task is Task 1 of 1: Design the export\n\nRepositories it touches: acme/api\n\n"
            + "Show it with **placeholder** rows.\nNo filter.",
            again.Message.ReplaceLineEndings("\n"));
        Assert.Contains("The test command of acme/web failed with exit code 1.", again.Message);
        Assert.Contains("exports nothing", again.Message);
        // The agent of the endless session never ends its turn.
        await StatesAsync(overseer, address, "Running");
        var page = await GetAsync(overseer, address);
        Assert.Equal(new ShownTask(1, "Design the export", "Designer", "acme/api", "Running"), Assert.Single(TasksOn(page)));
        Assert.Equal(("Green", "Working"), LightOn(page));
    }

    [Fact]
    public async Task Only_a_Failed_Task_offers_Retry_and_Edit_and_retry()
    {
        await using var overseer = await StartAsync("acme/api", "acme/web");
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 1);
        var designing = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Designer,
            new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done()) { EndsAfter = designing.Task }.WithScreenshot()));
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        // Task 1 builds acme/web alone, and Task 2 acme/api, which fails.
        overseer.Sandboxes.ScriptCommandsContaining(
            "acme/api' || exit\nlog=$(mktemp) || exit\n(\nset -e\nnpm test", new CommandResult(1, "1 test failed"));
        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");
        await StatesAsync(overseer, address, "Running", "Pending", "Pending");
        var page = await GetAsync(overseer, address);
        Assert.All([1, 2, 3], task => Assert.Null(RecoveryOn(page, task)));

        designing.SetResult();

        await ImplementationTests.FailedAsync(overseer, address);
        await StatesAsync(overseer, address, "Done", "Failed", "Pending");
        page = await GetAsync(overseer, address);
        Assert.Null(RecoveryOn(page, 1));
        Assert.Null(RecoveryOn(page, 3));
        Assert.Equal(
            new Recovery($"{address}/tasks/2/retry", "1", $"{address}/tasks/2/edit"), RecoveryOn(page, 2));
        // The implementation as a whole still offers none.
        Assert.Null(ImplementationTests.ImplementationOn(page)!.RetryAction);
    }

    [Fact]
    public async Task A_session_that_breaks_off_makes_no_Failed_Task_so_its_Task_offers_neither()
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession { StartFailure = "opencode is not installed." });

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        await ImplementationTests.FailedAsync(overseer, address);
        Assert.Null(RecoveryOn(await GetAsync(overseer, address), 1));
    }

    [Fact]
    public async Task A_retried_Task_has_all_its_attempts_again_and_the_earlier_ones_stay_while_the_Approved_Spec_stays_as_approved()
    {
        await using var overseer = await StartAsync("acme/web");
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 2);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        overseer.Sandboxes.ScriptCommandsContaining(
            "npm test", new CommandResult(1, "first"), new CommandResult(1, "second"), new CommandResult(1, "third"), Passes);
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await ImplementationTests.FailedAsync(overseer, address);
        var approved = ApprovedSpecOn(await GetAsync(overseer, address));

        using var saved = await EditAndRetryAsync(
            overseer, $"{address}/tasks/1/edit",
            new EditedFields("Export as a file", "Download the orders.", "Developer", ["acme/web"]));

        await StatesAsync(overseer, address, "Done");
        var page = await GetAsync(overseer, address);
        Assert.Equal(
            [
                "Run 1, attempt 1: failed. The test command of acme/web failed with exit code 1.",
                "Run 1, attempt 2: failed. The test command of acme/web failed with exit code 1.",
                "Run 2, attempt 1: failed. The test command of acme/web failed with exit code 1.",
                "Run 2, attempt 2: passed.",
            ],
            VerificationTests.AttemptsOn(page, 1).Select(attempt => attempt.Heading));
        Assert.Equal("Export as a file", Assert.Single(TasksOn(page)).Title);
        Assert.Equal(approved, ApprovedSpecOn(page));
        Assert.Contains("Add the export endpoint", approved);
        // The agent is told the Approved Spec as it is, and the Task as edited.
        var message = SquadMessages(overseer).Last(message => message.Message.StartsWith("You are", StringComparison.Ordinal)).Message;
        Assert.Contains("### Task 1: Add the export endpoint", message);
        Assert.Contains("Your Task is Task 1 of 1: Export as a file", message);
        // Done, it can't be edited or retried any more.
        Assert.Null(RecoveryOn(page, 1));
    }

    [Fact]
    public async Task A_Task_that_fails_again_after_a_Retry_is_a_Failed_Task_that_can_be_retried_again()
    {
        await using var overseer = await StartAsync("acme/web");
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 2);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        overseer.Sandboxes.ScriptCommandsContaining(
            "npm test", new CommandResult(1, "first"), new CommandResult(1, "second"), new CommandResult(1, "third"),
            new CommandResult(1, "fourth"), Passes);
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await ImplementationTests.FailedAsync(overseer, address);

        using var retried = await RetryTaskAsync(overseer, address, 1);

        await FailedInRunAsync(overseer, address, "2");

        using var again = await RetryTaskAsync(overseer, address, 1);

        await StatesAsync(overseer, address, "Done");
        // The third session is told what the second one's last attempt failed with.
        var message = SquadMessages(overseer).Last(message => message.Message.StartsWith("You are", StringComparison.Ordinal)).Message;
        Assert.Contains("fourth", message);
        Assert.DoesNotContain("second", message);
        Assert.Equal(3, SquadSessionsStarted(overseer).Count);
    }

    [Fact]
    public async Task After_a_restart_a_Failed_Task_stays_one_and_Retry_runs_it_in_a_fresh_Sandbox_told_what_failed_last()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        Sandbox old;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            first.Sandboxes.AddRepositoryFile("acme/web", ".overseer.yml", Manifest);
            await SetOperatingLimitsAsync(first, verificationAttemptsPerTask: 1);
            first.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
            first.Sandboxes.ScriptCommandsContaining("npm test", new CommandResult(1, "exports nothing"));
            address = await ImplementationTests.ImplementingAsync(first, "Export data", "acme/web");
            await ImplementationTests.FailedAsync(first, address);
            old = Assert.Single(await first.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        }

        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        await StatesAsync(second, address, "Failed");

        using var retried = await RetryTaskAsync(second, address, 1);

        var message = Assert.Single(await SquadMessagesAsync(second, 1));
        Assert.NotEqual(old, message.Sandbox);
        Assert.Contains("exports nothing", message.Message);
        Assert.True(second.Sandboxes.MayPushOf(message.Sandbox));
        await StatesAsync(second, address, "Running");
        Assert.Equal(("Green", "Working"), LightOn(await GetAsync(second, address)));
    }

    [Fact]
    public async Task Retry_pressed_twice_at_once_runs_the_Task_once()
    {
        await using var overseer = await StartAsync("acme/web");
        var address = await FailedTaskAsync(overseer);
        var tab = await overseer.OpenAsync(address);
        var shown = RecoveryOn(await GetAsync(overseer, address), 1)!;

        var (first, second) = await DatabaseRaces.RaceAsync(
            overseer, $"""LOCK TABLE "Implementations" IN EXCLUSIVE MODE""",
            () => PressRetryAsync(tab, shown), () => PressRetryAsync(tab, shown));

        using (first)
        using (second)
        {
            Assert.Equal(address, first.FinalPath());
            Assert.Equal(address, second.FinalPath());
        }
        await SquadMessagesAsync(overseer, 2);
        await StatesAsync(overseer, address, "Running");
        Assert.Equal(2, SquadSessionsStarted(overseer).Count);
    }

    [Fact]
    public async Task The_Retry_of_a_page_opened_before_the_Task_failed_again_does_nothing()
    {
        await using var overseer = await StartAsync("acme/web");
        var address = await FailedTaskAsync(overseer, DoneAtOnce());
        var stale = await overseer.OpenAsync(address);
        var shown = RecoveryOn(await GetAsync(overseer, address), 1)!;
        using var retried = await RetryTaskAsync(overseer, address, 1);
        await FailedInRunAsync(overseer, address, "2");
        var sessions = SquadSessionsStarted(overseer).Count;

        using var again = await PressRetryAsync(stale, shown);

        Assert.Equal(address, again.FinalPath());
        await AssertStillFailedAsync(overseer, address, sessions);
    }

    [Theory]
    [InlineData(1, "Done")]
    [InlineData(3, "Pending")]
    [InlineData(4, null)]
    public async Task Retry_and_Edit_and_retry_posted_for_a_Task_that_is_no_Failed_Task_change_nothing(int task, string? state)
    {
        await using var overseer = await StartAsync("acme/api", "acme/web");
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 1);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Designer, DesignerHandsIn.DesignsAtOnce());
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        overseer.Sandboxes.ScriptCommandsContaining(
            "acme/api' || exit\nlog=$(mktemp) || exit\n(\nset -e\nnpm test", new CommandResult(1, "1 test failed"));
        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");
        await ImplementationTests.FailedAsync(overseer, address);
        var tasks = await StatesAsync(overseer, address, "Done", "Failed", "Pending");
        var tab = await overseer.OpenAsync(address);
        var editAddress = $"{address}/tasks/{task}/edit";
        var refusal = state is null ? "The Intent has no Task 4." : $"Task {task} is {state}, and only a Failed Task can be edited and retried.";

        var opened = await GetAsync(overseer, editAddress);

        Assert.Equal(refusal, Alert(opened));
        Assert.DoesNotContain("name=\"Title\"", opened);

        using var retried = await tab.PostAsync($"{address}/tasks/{task}/retry", [new("Run", "1")]);
        // As the editor of a Task that was a Failed Task when it was opened.
        using var saved = await (await overseer.OpenAsync(editAddress)).SubmitFormAsync(
            "edit-task", [.. Fields(new EditedFields("Changed", "Changed.", "Developer", ["acme/web"])), new("Run", "1")]);

        Assert.Contains(refusal, WebUtility.HtmlDecode(await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(tasks, TasksOn(await GetAsync(overseer, address)));
        await AssertStillFailedAsync(overseer, address, sessions: 2);
    }

    [Fact]
    public async Task A_Task_of_an_implementation_that_failed_for_another_reason_cant_be_retried_by_itself()
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer, new FakeAgentSession { StartFailure = "opencode is not installed." },
            new FakeAgentSession(FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await ImplementationTests.FailedAsync(overseer, address);
        var tab = await overseer.OpenAsync(address);

        using var retried = await tab.PostAsync($"{address}/tasks/1/retry", [new("Run", "0")]);

        await StatesAsync(overseer, address, "Pending");
        Assert.Equal("Failed", ImplementationTests.ImplementationOn(await GetAsync(overseer, address))!.Phase);
        Assert.Empty(SquadSessionsStarted(overseer));
    }

    [Theory]
    [InlineData("", "Do it.", "Developer", "acme/web", "Task 1's title is empty.")]
    [InlineData("Do it", " \r\n ", "Developer", "acme/web", "Task 1's description is empty: write it in Markdown.")]
    [InlineData("Do it", "Do it.", "Analyst", "acme/web", "Task 1's role is Analyst: choose Designer or Developer.")]
    [InlineData(
        "Do it", "Do it.", "Developer", null,
        "Task 1 touches no repository: tick at least one of the Repositories in scope, acme/web.")]
    [InlineData(
        "Do it", "Do it.", "Developer", "acme/api",
        "Task 1 touches acme/api, which isn't a Repository in scope: those are acme/web.")]
    public async Task An_invalid_edit_is_refused_with_the_reason_keeping_what_was_entered_and_changes_nothing(
        string title, string description, string role, string? repository, string reason)
    {
        // acme/api is configured, but not in this Intent's scope.
        await using var overseer = await StartAsync("acme/api", "acme/web");
        var address = await FailedTaskAsync(overseer);
        var tasks = TasksOn(await GetAsync(overseer, address));

        using var saved = await EditAndRetryAsync(
            overseer, $"{address}/tasks/1/edit",
            new EditedFields(title, description, role, repository is null ? [] : [repository]));

        Assert.Equal($"{address}/tasks/1/edit", saved.FinalPath());
        var page = await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains($"<li>{reason}</li>", WebUtility.HtmlDecode(page));
        Assert.Equal(title, EditorOn(page).Title);
        Assert.Equal(tasks, TasksOn(await GetAsync(overseer, address)));
        await AssertStillFailedAsync(overseer, address, sessions: 1);
    }

    [Fact]
    public async Task The_editor_of_a_page_opened_before_the_Task_failed_again_saves_nothing_over_what_was_saved_since()
    {
        await using var overseer = await StartAsync("acme/web");
        var address = await FailedTaskAsync(overseer, DoneAtOnce());
        var editAddress = $"{address}/tasks/1/edit";
        var stale = await overseer.OpenAsync(editAddress);
        var staleRun = LastRunOn(await GetAsync(overseer, editAddress));
        using var first = await EditAndRetryAsync(overseer, editAddress, new EditedFields("First edit", "One.", "Developer", ["acme/web"]));
        await FailedInRunAsync(overseer, address, "2");
        var sessions = SquadSessionsStarted(overseer).Count;

        using var second = await stale.SubmitFormAsync(
            "edit-task", [.. Fields(new EditedFields("Second edit", "Two.", "Developer", ["acme/web"])), staleRun]);

        var page = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(
            "<li>Task 1 has run and failed again since this page was opened. Open Edit & retry again from the Intent's page, "
            + "to change the Task as it is now.</li>",
            WebUtility.HtmlDecode(page));
        Assert.Equal("Second edit", EditorOn(page).Title);
        // Saving it once more is refused too.
        Assert.Equal(staleRun, LastRunOn(page));
        Assert.Equal("First edit", Assert.Single(TasksOn(await GetAsync(overseer, address))).Title);
        await AssertStillFailedAsync(overseer, address, sessions);
    }

    [Fact]
    public async Task Two_edits_saved_at_once_save_one_and_run_the_Task_once()
    {
        await using var overseer = await StartAsync("acme/web");
        var address = await FailedTaskAsync(overseer);
        var editAddress = $"{address}/tasks/1/edit";
        var tab = await overseer.OpenAsync(editAddress);
        var run = LastRunOn(await GetAsync(overseer, editAddress));
        Task<HttpResponseMessage> SaveAsAsync(string title) =>
            tab.SubmitFormAsync("edit-task", [.. Fields(new EditedFields(title, "Do it.", "Developer", ["acme/web"])), run]);

        var (first, second) = await DatabaseRaces.RaceAsync(
            overseer, $"""LOCK TABLE "Implementations" IN EXCLUSIVE MODE""",
            () => SaveAsAsync("First edit"), () => SaveAsAsync("Second edit"));

        using (first)
        using (second)
        {
            // Either can win: it goes back to the Intent, and the other stays on the editor, saying it saved nothing.
            var (saved, refused, title) = first.FinalPath() == address ? (first, second, "First edit") : (second, first, "Second edit");
            Assert.Equal(address, saved.FinalPath());
            Assert.Equal(editAddress, refused.FinalPath());
            Assert.Contains("The Task was not saved:", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(title, Assert.Single(await StatesAsync(overseer, address, "Running")).Title);
        }
        await SquadMessagesAsync(overseer, 2);
        Assert.Equal(2, SquadSessionsStarted(overseer).Count);
    }

    /// <summary>
    /// Has the one Task of an Intent with acme/web in scope fail verification at its one attempt, and gives the Intent's address.
    /// </summary>
    /// <param name="later">The sessions on the Task after the first; by default one that keeps working.</param>
    private static async Task<string> FailedTaskAsync(RunningOverseer overseer, FakeAgentSession? later = null)
    {
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 1);
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer, DoneAtOnce(), later ?? new FakeAgentSession(FakeAgentTurn.Endless()));
        overseer.Sandboxes.ScriptCommandsContaining("npm test", new CommandResult(1, "1 test failed"));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await ImplementationTests.FailedAsync(overseer, address);
        return address;
    }

    /// <summary>Asserts that the Intent is still stopped at its Failed Task, with no session started beyond these.</summary>
    private static async Task AssertStillFailedAsync(RunningOverseer overseer, string address, int sessions)
    {
        var page = await GetAsync(overseer, address);
        Assert.Contains(TasksOn(page), task => task.State == "Failed");
        Assert.Equal(
            ("Failed", ("Red", "Failed")),
            (ImplementationTests.ImplementationOn(page)!.Phase, ImplementationTests.ImplementationOn(page)!.Light));
        Assert.Equal(sessions, SquadSessionsStarted(overseer).Count);
    }

    /// <summary>Waits for the Task to be a Failed Task once more, in the run with this number.</summary>
    private static Task<Recovery> FailedInRunAsync(RunningOverseer overseer, string address, string run) =>
        EventuallyAsync(
            async () => RecoveryOn(await GetAsync(overseer, address), 1) is { } offered && offered.Run == run ? offered : null,
            $"the Task to fail in run {run}");

    /// <summary>The Intent's Approved Spec as its detail page shows it.</summary>
    private static string ApprovedSpecOn(string page)
    {
        var spec = Regex.Match(page, "<ol class=\"spec-tasks\">.*?</ol>", RegexOptions.Singleline);
        Assert.True(spec.Success, $"The page shows no Spec: {page}");
        return ElementText(page, "spec-task-title") + "\n" + spec.Value;
    }

    /// <summary>The Task as the editor at this address holds it.</summary>
    private static async Task<EditedFields> EditorAsync(RunningOverseer overseer, string editAddress) =>
        EditorOn(await GetAsync(overseer, editAddress));

    private static EditedFields EditorOn(string page) => new(
        WebUtility.HtmlDecode(Regex.Match(page, "<input[^>]*name=\"Title\"[^>]*value=\"([^\"]*)\"").Groups[1].Value),
        WebUtility.HtmlDecode(
            Regex.Match(page, "<textarea[^>]*name=\"Description\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline).Groups[1].Value),
        Regex.Match(page, "<option value=\"([^\"]*)\" selected").Groups[1].Value,
        [.. Regex.Matches(page, "<input[^>]*name=\"Repositories\"[^>]*>")
            .Where(checkbox => Regex.IsMatch(checkbox.Value, @"\schecked\b"))
            .Select(checkbox => WebUtility.HtmlDecode(Regex.Match(checkbox.Value, "value=\"([^\"]*)\"").Groups[1].Value))]);

    /// <summary>What a browser sends for the editor holding this, apart from its hidden fields.</summary>
    private static IEnumerable<KeyValuePair<string, string>> Fields(EditedFields task) =>
    [
        new("Title", task.Title),
        new("Description", task.Description),
        new("Role", task.Role),
        .. task.Repositories.Select(repository => KeyValuePair.Create("Repositories", repository)),
    ];

    /// <summary>The editor's hidden field that says which failure the Task was edited after.</summary>
    private static KeyValuePair<string, string> LastRunOn(string editor) =>
        new("Run", Regex.Match(editor, "name=\"Run\" value=\"([^\"]*)\"").Groups[1].Value);

    /// <summary>Opens the Task's editor, changes the Task to this and presses its button.</summary>
    internal static async Task<HttpResponseMessage> EditAndRetryAsync(RunningOverseer overseer, string editAddress, EditedFields task)
    {
        var tab = await overseer.OpenAsync(editAddress);
        var editor = await GetAsync(overseer, editAddress);
        return await tab.SubmitFormAsync("edit-task", [.. Fields(task), LastRunOn(editor)]);
    }

    internal sealed record EditedFields(string Title, string Description, string Role, string[] Repositories)
    {
        /// <summary>Compares the repositories by their names, not by the array that holds them.</summary>
        public static readonly IEqualityComparer<EditedFields> Comparer = EqualityComparer<EditedFields>.Create(
            (one, other) => one!.Title == other!.Title && one.Description == other.Description && one.Role == other.Role
                && one.Repositories.SequenceEqual(other.Repositories));
    }

    /// <summary>What the Task offers on the Intent's detail page to recover it; <c>null</c> when it offers nothing.</summary>
    private static Recovery? RecoveryOn(string page, int task)
    {
        var shown = Regex.Match(page, $"<li class=\"intent-task\" data-task=\"{task}\"[^>]*>(.*?)</li>", RegexOptions.Singleline);
        Assert.True(shown.Success, $"The page shows no Task {task}: {page}");
        var retry = Regex.Match(shown.Value, "<form[^>]*action=\"([^\"]*retry)\".*?</form>", RegexOptions.Singleline);
        var edit = Regex.Match(shown.Value, "<a[^>]*href=\"([^\"]*/edit)\"[^>]*>");
        if (!retry.Success && !edit.Success)
        {
            return null;
        }
        return new Recovery(
            retry.Success ? "/" + WebUtility.HtmlDecode(retry.Groups[1].Value) : null,
            Regex.Match(retry.Value, "name=\"Run\" value=\"([^\"]*)\"").Groups[1].Value,
            edit.Success ? "/" + WebUtility.HtmlDecode(edit.Groups[1].Value) : null);
    }

    /// <summary>Presses the Retry button of the Task on the Intent's detail page, as a browser would.</summary>
    internal static async Task<HttpResponseMessage> RetryTaskAsync(RunningOverseer overseer, string address, int task)
    {
        var tab = await overseer.OpenAsync(address);
        var offered = RecoveryOn(await GetAsync(overseer, address), task);
        Assert.NotNull(offered?.RetryAction);
        return await PressRetryAsync(tab, offered);
    }

    /// <summary>Presses a Retry button as a tab showed it, however long ago that was.</summary>
    private static Task<HttpResponseMessage> PressRetryAsync(PortalTab tab, Recovery shown) =>
        tab.PostAsync(shown.RetryAction!, [new("Run", shown.Run)]);

    /// <summary>What a Failed Task offers on the Intent's detail page.</summary>
    /// <param name="Run">What its Retry button sends along to say which failure the page showed.</param>
    private sealed record Recovery(string? RetryAction, string Run, string? EditAddress);

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

    /// <summary>A session whose every turn ends as done at once.</summary>
    private static FakeAgentSession DoneAtOnce() => new(new FakeAgentTurn([], new AgentOutcome.Done()));
}
