using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Overseer.Sandboxes;
using static Overseer.Tests.ImplementationTests;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;
using static Overseer.Tests.SpecTests;

namespace Overseer.Tests;

/// <summary>
/// The Tasks of an Implementing Intent: once its Sandbox is ready, the Squad carries them out one at a time, which the Intent's
/// detail page follows.
/// </summary>
public class TaskTests(PostgresServer postgres)
{
    [Fact]
    public async Task The_Intents_Tasks_are_created_from_the_Approved_Spec_in_its_order_all_Pending()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api", "acme/web");
        // The Sandbox is never ready, so no Task starts.
        overseer.Sandboxes.HoldCommandsContaining("git checkout", new TaskCompletionSource().Task);

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        Assert.Equal(
            [
                new ShownTask(1, "Design the Export button", "Designer", "acme/web", "Pending"),
                new ShownTask(2, "Add the export endpoint", "Developer", "acme/api", "Pending"),
                new ShownTask(3, "Wire up the button", "Developer", "acme/api, acme/web", "Pending"),
            ],
            await TasksAsync(overseer, address, tasks => tasks.Count > 0, "the Tasks"));
    }

    [Fact]
    public async Task The_Squad_carries_out_the_Tasks_one_at_a_time_in_order_each_in_a_new_session_of_its_role_which_stops_as_the_Task_ends()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api", "acme/web");
        var design = new TaskCompletionSource();
        var endpoint = new TaskCompletionSource();
        var button = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Designer, DoneOnce(design.Task));
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneOnce(endpoint.Task), DoneOnce(button.Task));

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        await StatesAsync(overseer, address, "Running", "Pending", "Pending");
        // A Task shows Running just before its session starts.
        await SquadMessagesAsync(overseer, 1);
        Assert.Equal([AgentRole.Designer], SquadSessionsStarted(overseer).Select(session => session.Role));
        Assert.Empty(SquadSessionsStopped(overseer));
        Assert.Equal(("Green", "Working"), LightOn(await GetAsync(overseer, address)));
        Assert.Equal(("Green", "Working"), LightOn(await GetAsync(overseer, "/")));

        design.SetResult();

        await StatesAsync(overseer, address, "Done", "Running", "Pending");
        await SquadMessagesAsync(overseer, 2);
        Assert.Equal([AgentRole.Designer, AgentRole.Developer], SquadSessionsStarted(overseer).Select(session => session.Role));
        Assert.Equal(SquadSessionsStarted(overseer).Take(1), SquadSessionsStopped(overseer));

        endpoint.SetResult();

        await StatesAsync(overseer, address, "Done", "Done", "Running");
        await SquadMessagesAsync(overseer, 3);
        Assert.Equal(
            [AgentRole.Designer, AgentRole.Developer, AgentRole.Developer], SquadSessionsStarted(overseer).Select(session => session.Role));
        Assert.Equal(SquadSessionsStarted(overseer).Take(2), SquadSessionsStopped(overseer));

        button.SetResult();

        await StatesAsync(overseer, address, "Done", "Done", "Done");
        Assert.Equal(SquadSessionsStarted(overseer), SquadSessionsStopped(overseer));
        // Each got one message, all in the one Sandbox the implementation prepared.
        var sandbox = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal([sandbox, sandbox, sandbox], SquadMessages(overseer).Select(message => message.Sandbox));
        // After the last Task the Intent stays Implementing, its Sandbox kept.
        var page = await GetAsync(overseer, address);
        Assert.Equal("Implementing", ElementText(page, "intent-state"));
        Assert.Equal(("Ready", ("Green", "Working")), (ImplementationOn(page)!.Phase, ImplementationOn(page)!.Light));
    }

    [Theory]
    [InlineData("doesn't start", "The Developer's session for Task 2 failed: opencode is not installed.")]
    [InlineData("breaks off", "The Developer's session for Task 2 failed: The agent process exited with code 1.")]
    [InlineData("asks", "The Developer asked a question during Task 2, which can't be answered yet: Which port should it listen on?")]
    public async Task A_session_that_fails_turns_the_Intent_red_with_the_reason_and_Retry_runs_that_Task_again_in_a_new_session_in_the_same_Sandbox(
        string failure, string error)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api", "acme/web");
        var failing = failure switch
        {
            "doesn't start" => new FakeAgentSession { StartFailure = "opencode is not installed." },
            "breaks off" => new FakeAgentSession(
                new FakeAgentTurn(["Reading the code"], new AgentOutcome.Done()) { Failure = "The agent process exited with code 1." }),
            _ => new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.AskedQuestion("Which port should it listen on?"))),
        };
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Designer, DoneOnce(Task.CompletedTask));
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, failing, new FakeAgentSession(FakeAgentTurn.Endless()));

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        var failed = await FailedAsync(overseer, address);
        Assert.Equal(error, failed.Status);
        Assert.Equal($"intents/{address["/intents/".Length..]}/implementation/retry", failed.RetryAction);
        Assert.Equal("Implementing", ElementText(await GetAsync(overseer, address), "intent-state"));
        // The Task waits to run again, and no Task after it has started.
        await StatesAsync(overseer, address, "Done", "Pending", "Pending");
        Assert.Equal(SquadSessionsStarted(overseer), SquadSessionsStopped(overseer));
        var sandbox = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        var sessions = SquadSessionsStarted(overseer).Count;
        var messages = SquadMessages(overseer).Count;
        var commands = overseer.Sandboxes.CommandsRun.Count;

        using var retried = await ImplementationTests.RetryAsync(overseer, address);

        // Back on the detail page, so reloading it doesn't retry again.
        Assert.Equal(address, retried.FinalPath());
        var again = (await SquadMessagesAsync(overseer, messages + 1))[^1];
        Assert.Equal(AgentRole.Developer, again.Role);
        Assert.Contains("Your Task is Task 2 of 3", again.Message);
        Assert.Equal(sessions + 1, SquadSessionsStarted(overseer).Count);
        await StatesAsync(overseer, address, "Done", "Running", "Pending");
        var page = await GetAsync(overseer, address);
        Assert.Equal(("Ready", ("Green", "Working")), (ImplementationOn(page)!.Phase, ImplementationOn(page)!.Light));
        Assert.Null(ImplementationOn(page)!.RetryAction);
        // In the Sandbox the failed session worked in, which isn't prepared again.
        Assert.Equal(sandbox, again.Sandbox);
        Assert.Equal([sandbox], await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(commands, overseer.Sandboxes.CommandsRun.Count);
    }

    [Fact]
    public async Task After_a_session_failed_the_page_still_shows_the_Sandbox_prepared()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession { StartFailure = "opencode is not installed." });

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        await FailedAsync(overseer, address);
        var page = await GetAsync(overseer, address);
        Assert.Equal(4, Regex.Matches(page, "class=\"step-done\"").Count);
        Assert.DoesNotContain("step-stopped", page);
    }

    [Fact]
    public async Task Retry_after_a_failed_session_prepares_a_fresh_Sandbox_when_the_prepared_one_is_gone_and_no_Task_that_is_done_runs_again()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api", "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Designer, DoneOnce(Task.CompletedTask));
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer, new FakeAgentSession { StartFailure = "The Sandbox does not answer." }, new FakeAgentSession(FakeAgentTurn.Endless()));
        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");
        await FailedAsync(overseer, address);
        var gone = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        // Deleted behind Overseer's back, as an evicted spot instance is.
        await overseer.Sandboxes.DestroyAsync(gone, TestContext.Current.CancellationToken);
        var commands = overseer.Sandboxes.CommandsRun.Count;

        using var retried = await ImplementationTests.RetryAsync(overseer, address);

        var again = (await SquadMessagesAsync(overseer, 2))[1];
        Assert.Contains("Your Task is Task 2 of 3", again.Message);
        var fresh = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(gone, fresh);
        Assert.Equal(fresh, again.Sandbox);
        Assert.True(overseer.Sandboxes.MayPushOf(fresh));
        // Prepared like the first: each repository put on the Intent branch.
        Assert.Equal(2, overseer.Sandboxes.CommandsRun.Skip(commands).Count(run => run.Sandbox == fresh && run.Command.Contains("git checkout")));
        await StatesAsync(overseer, address, "Done", "Running", "Pending");
        Assert.Equal([AgentRole.Designer, AgentRole.Developer], SquadSessionsStarted(overseer).Select(session => session.Role));
        await using var database = overseer.OpenDatabase();
        Assert.Equal(
            [fresh.ResourceId],
            await database.Sandboxes.Where(sandbox => sandbox.DestroyedAt == null).Select(sandbox => sandbox.ResourceId)
                .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_Retry_that_cant_tell_whether_the_Sandbox_is_still_there_creates_no_other_and_the_next_Retry_goes_on_in_it()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer, new FakeAgentSession { StartFailure = "opencode is not installed." }, new FakeAgentSession(FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await FailedAsync(overseer, address);
        var sandbox = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        overseer.Sandboxes.FailNextListing("Azure could not be reached.");

        using var retried = await ImplementationTests.RetryAsync(overseer, address);

        await EventuallyAsync(
            async () => ImplementationOn(await GetAsync(overseer, address)) is { Phase: "Failed", Status: var status }
                && status == "Overseer could not check whether the Sandbox is still there: Azure could not be reached." ? status : null,
            "the Retry to fail");
        Assert.Equal([sandbox], await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));

        using var again = await ImplementationTests.RetryAsync(overseer, address);

        Assert.Equal(sandbox, Assert.Single(await SquadMessagesAsync(overseer, 1)).Sandbox);
    }

    [Fact]
    public async Task A_restart_while_a_Task_runs_leaves_it_Pending_and_Retry_runs_it_again_in_a_fresh_Sandbox_without_the_Tasks_already_done()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        Sandbox old;
        FakeSandboxProvider firstSandboxes;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/api", "acme/web"]))
        {
            firstSandboxes = first.Sandboxes;
            first.Sandboxes.ScriptAgentSessions(AgentRole.Designer, DoneOnce(Task.CompletedTask));
            address = await ImplementingAsync(first, ThreeTasks(), "Export data", "acme/api", "acme/web");
            await StatesAsync(first, address, "Done", "Running", "Pending");
            await SquadMessagesAsync(first, 2);
            old = Assert.Single(await first.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        }
        // Stopping serve stops the agent.
        Assert.Equal(2, firstSandboxes.AgentSessionsStopped.Count(session => session.Role is AgentRole.Designer or AgentRole.Developer));

        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        // From the moment the Portal answers.
        var page = await GetAsync(second, address);
        Assert.Equal(["Done", "Pending", "Pending"], TasksOn(page).Select(task => task.State));
        Assert.Equal(("Failed", ("Red", "Failed")), (ImplementationOn(page)!.Phase, ImplementationOn(page)!.Light));

        using var retried = await ImplementationTests.RetryAsync(second, address);

        var message = Assert.Single(await SquadMessagesAsync(second, 1));
        Assert.Contains("Your Task is Task 2 of 3", message.Message);
        Assert.NotEqual(old, message.Sandbox);
        await StatesAsync(second, address, "Done", "Running", "Pending");
        Assert.Equal([AgentRole.Developer], SquadSessionsStarted(second).Select(session => session.Role));
    }

    [Fact]
    public async Task A_restart_after_a_session_failed_keeps_its_error_and_Retry_prepares_a_fresh_Sandbox()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        Sandbox old;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            first.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession { StartFailure = "opencode is not installed." });
            address = await ImplementationTests.ImplementingAsync(first, "Export data", "acme/web");
            await FailedAsync(first, address);
            old = Assert.Single(await first.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        }

        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        var page = await GetAsync(second, address);
        Assert.Equal("The Developer's session for Task 1 failed: opencode is not installed.", ImplementationOn(page)!.Status);
        // Its Sandbox went with the restart, so the page no longer shows it prepared.
        Assert.DoesNotContain("step-done", page);

        using var retried = await ImplementationTests.RetryAsync(second, address);

        var message = Assert.Single(await SquadMessagesAsync(second, 1));
        Assert.NotEqual(old, message.Sandbox);
        Assert.Contains(second.Sandboxes.CommandsRun, run => run.Sandbox == message.Sandbox && run.Command.Contains("git checkout"));
    }

    [Theory]
    [InlineData("breaks off")]
    [InlineData("asks")]
    public async Task What_a_failed_session_says_never_puts_the_GitHub_token_in_the_error_or_the_database(string failure)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var said = $"git said: Bearer {Token} was refused. Now what?";
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(failure == "asks"
            ? new FakeAgentTurn([], new AgentOutcome.AskedQuestion(said))
            : new FakeAgentTurn([], new AgentOutcome.Done()) { Failure = said }));

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        var failed = await FailedAsync(overseer, address);
        Assert.EndsWith("git said: Bearer *** was refused. Now what?", failed.Status);
        Assert.DoesNotContain(await overseer.DumpDatabaseAsync(), table => table.Contains(Token));
    }

    [Fact]
    public async Task The_first_message_of_a_Tasks_session_holds_the_Approved_Spec_and_the_Task_with_the_repositories_it_touches()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api", "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Designer, DoneOnce(Task.CompletedTask));
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneOnce(Task.CompletedTask), new FakeAgentSession(FakeAgentTurn.Endless()));

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        var message = (await SquadMessagesAsync(overseer, 3))[2].Message.ReplaceLineEndings("\n");
        // The whole Approved Spec.
        Assert.Contains("## Summary\n\nOrders can be exported as **CSV**.", message);
        Assert.Contains("## Approach\n\nAdd an export endpoint and a button on the orders page.", message);
        Assert.Contains("1. (Analyst's Choice) The export is *CSV*.\n\n2. It holds every order.", message);
        Assert.Contains("## Acceptance criteria\n\n- The orders page offers Export\n- The file opens in a spreadsheet", message);
        Assert.Contains("## Dependencies\n\nNone.", message);
        Assert.Contains("### Task 1: Design the Export button", message);
        Assert.Contains("### Task 2: Add the export endpoint", message);
        // And which of its Tasks is this session's.
        Assert.Contains(
            """
            Your Task is Task 3 of 3: Wire up the button

            Repositories it touches: acme/api, acme/web

            The button downloads the export.
            """.ReplaceLineEndings("\n"),
            message);
        // Where to find the code.
        Assert.Contains("- acme/api, in /home/overseer/repositories/acme/api", message);
        Assert.Contains("- acme/web, in /home/overseer/repositories/acme/web", message);
        Assert.Contains($"overseer/intent-{address["/intents/".Length..].Replace("-", "")}", message);
    }

    [Theory]
    [InlineData(AgentRole.Designer, "You are the Designer of Overseer", "placeholder data")]
    [InlineData(AgentRole.Developer, "You are a Developer of Overseer", "Always include unit tests and integration tests for your own work")]
    public async Task The_first_message_of_a_Tasks_session_holds_its_roles_instructions_which_leave_pushing_to_Overseer_and_rule_out_a_question(
        AgentRole role, string who, string duty)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api", "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Designer, DoneOnce(Task.CompletedTask));

        await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        var messages = await SquadMessagesAsync(overseer, 2);
        var message = Assert.Single(messages, message => message.Role == role).Message;
        Assert.StartsWith(who, message);
        Assert.Contains(duty, message);
        Assert.Contains("Leave pushing to Overseer: never run `git push`", message);
        Assert.Contains("Don't end your turn with a question", message);
    }

    /// <summary>Waits for the Squad's agents to have been sent this many messages.</summary>
    private static Task<IReadOnlyList<AgentMessage>> SquadMessagesAsync(RunningOverseer overseer, int count) =>
        EventuallyAsync(
            () => Task.FromResult(SquadMessages(overseer) is var messages && messages.Count >= count ? messages : null),
            $"the Squad to be sent {count} messages");

    /// <summary>A session whose turn ends as done once <paramref name="until"/> completes.</summary>
    private static FakeAgentSession DoneOnce(Task until) => new(new FakeAgentTurn([], new AgentOutcome.Done()) { EndsAfter = until });

    /// <summary>The sessions the Squad's agents got so far, in the order they started.</summary>
    private static IReadOnlyList<AgentSessionStart> SquadSessionsStarted(RunningOverseer overseer) =>
        [.. overseer.Sandboxes.AgentSessionsStarted.Where(session => IsOfTheSquad(session.Role))];

    private static IReadOnlyList<AgentSessionStart> SquadSessionsStopped(RunningOverseer overseer) =>
        [.. overseer.Sandboxes.AgentSessionsStopped.Where(session => IsOfTheSquad(session.Role))];

    private static IReadOnlyList<AgentMessage> SquadMessages(RunningOverseer overseer) =>
        [.. overseer.Sandboxes.MessagesSent.Where(message => IsOfTheSquad(message.Role))];

    private static bool IsOfTheSquad(AgentRole role) => role is AgentRole.Designer or AgentRole.Developer;

    /// <summary>Waits for the Intent's detail page to show its Tasks in these states, in order.</summary>
    private static Task<IReadOnlyList<ShownTask>> StatesAsync(RunningOverseer overseer, string address, params string[] states) =>
        TasksAsync(overseer, address, tasks => tasks.Select(task => task.State).SequenceEqual(states), $"the Tasks to be {string.Join(", ", states)}");

    /// <summary>A valid Spec with three Tasks, the first for the Designer, for an Intent with acme/api and acme/web in scope.</summary>
    internal static JsonObject ThreeTasks()
    {
        var spec = Spec();
        spec["tasks"] = new JsonArray(
            TaskJson("Design the Export button", "Place it **above** the table.", ["acme/web"], role: "Designer"),
            TaskJson("Add the export endpoint", "`GET /orders/export` returns every order as CSV.", ["acme/api"]),
            TaskJson("Wire up the button", "The button downloads the export.", ["acme/api", "acme/web"]));
        return spec;
    }

    /// <summary>Has an Intent with this Spec approved and started, and gives its address.</summary>
    private static async Task<string> ImplementingAsync(RunningOverseer overseer, JsonObject spec, string text, params string[] repositories)
    {
        var address = await AwaitingApprovalAsync(overseer, spec, text, repositories);
        using var approved = await ApproveAsync(overseer, address, start: true);
        return address;
    }

    /// <summary>Waits for the Intent's detail page to show its Tasks as <paramref name="expected"/> wants them.</summary>
    private static Task<IReadOnlyList<ShownTask>> TasksAsync(
        RunningOverseer overseer, string address, Func<IReadOnlyList<ShownTask>, bool> expected, string what) =>
        EventuallyAsync(async () => TasksOn(await GetAsync(overseer, address)) is var tasks && expected(tasks) ? tasks : null, what);

    /// <summary>The Intent's Tasks as its detail page shows them, in its order.</summary>
    internal static IReadOnlyList<ShownTask> TasksOn(string page) =>
        [.. Regex.Matches(page, "<li class=\"intent-task\" data-task=\"(\\d+)\"[^>]*>(.*?)</li>", RegexOptions.Singleline).Select(task => new ShownTask(
            int.Parse(task.Groups[1].Value),
            ElementText(task.Groups[2].Value, "intent-task-title"),
            ElementText(task.Groups[2].Value, "intent-task-role"),
            ElementText(task.Groups[2].Value, "intent-task-repositories"),
            ElementText(task.Groups[2].Value, "intent-task-state")))];

    /// <summary>A Task as the Intent's detail page shows it.</summary>
    internal sealed record ShownTask(int Number, string Title, string Role, string Repositories, string State);
}
