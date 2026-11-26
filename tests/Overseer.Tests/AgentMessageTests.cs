using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Overseer.Implementation;
using Overseer.Sandboxes;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;
using static Overseer.Tests.TaskTests;

namespace Overseer.Tests;

/// <summary>
/// Messaging the running agent: what the human writes on a running Task's page reaches the same session as its next turn, and the
/// page says whether it is still waiting, was delivered, or never can be.
/// </summary>
public class AgentMessageTests(PostgresServer postgres)
{
    [Fact]
    public async Task The_page_of_a_running_Task_offers_a_message_box_and_no_other_Tasks_page_does()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api", "acme/web");
        var working = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Designer, new FakeAgentSession(Done(after: working.Task)));
        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");
        await StatesAsync(overseer, address, "Running", "Pending", "Pending");

        await BoxAsync(overseer, address + "/tasks/1");
        Assert.False(OffersBox(await GetAsync(overseer, address + "/tasks/2")));

        working.SetResult();

        // Task 2's agent keeps working.
        await StatesAsync(overseer, address, "Done", "Running", "Pending");
        Assert.False(OffersBox(await GetAsync(overseer, address + "/tasks/1")));
        await BoxAsync(overseer, address + "/tasks/2");
        Assert.False(OffersBox(await GetAsync(overseer, address + "/tasks/3")));
    }

    [Fact]
    public async Task A_message_is_delivered_to_the_same_session_when_its_turn_ends_and_verification_waits_for_the_turn_it_starts()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        AddManifest(overseer);
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            Done(first.Task, Says("Adding it.")), Done(second.Task, Says("Renamed it."))));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var task = address + "/tasks/1";
        await BoxAsync(overseer, task);

        using var sent = await SendAsync(overseer, task, session: 1, "Call it <Download>,\r\nnot Export.\r\n");

        // Back on the Task's page, so reloading it doesn't send again.
        Assert.Equal(task, sent.FinalPath());
        // Waiting, as the agent's turn goes on: Overseer doesn't interrupt it.
        const string message = "Call it <Download>,\nnot Export.";
        Assert.Equal([Said("Adding it."), Waiting(message)], StreamOn(await GetAsync(overseer, task)));
        Assert.Single(SquadMessages(overseer));

        first.SetResult();

        var messages = await SquadMessagesAsync(overseer, 2);
        Assert.Contains(message, messages[1].Message);
        Assert.Equal(messages[0].Sandbox, messages[1].Sandbox);
        Assert.Single(SquadSessionsStarted(overseer));
        await StreamAsync(overseer, task, Said("Adding it."), Delivered(message), Said("Renamed it."));
        // Nothing is verified while the turn the message started goes on.
        Assert.DoesNotContain(overseer.Sandboxes.CommandsRun, command => command.Command.Contains("npm test"));
        Assert.Empty(VerificationTests.AttemptsOn(await GetAsync(overseer, address), 1));

        second.SetResult();

        await FinalVerificationTests.InReviewAsync(overseer, address);
        Assert.Equal(
            ["Attempt 1: passed."],
            VerificationTests.AttemptsOn(await GetAsync(overseer, address), 1).Select(attempt => attempt.Heading));
        Assert.Equal(2, SquadMessages(overseer).Count);
        // Kept with the Task.
        Assert.Equal([Said("Adding it."), Delivered(message), Said("Renamed it.")], StreamOn(await GetAsync(overseer, task)));
    }

    [Fact]
    public async Task Several_messages_sent_during_one_turn_are_each_delivered_in_a_turn_of_their_own_in_order_before_verification()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        AddManifest(overseer);
        var working = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            Done(working.Task, Says("Adding it.")), Done(updates: Says("One.")), Done(updates: Says("Two.")), Done(updates: Says("Three."))));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var task = address + "/tasks/1";
        await BoxAsync(overseer, task);
        foreach (var message in new[] { "Rename it.", "Move it up.", "No, down." })
        {
            using var sent = await SendAsync(overseer, task, session: 1, message);
        }
        await StreamAsync(overseer, task, Said("Adding it."), Waiting("Rename it."), Waiting("Move it up."), Waiting("No, down."));

        working.SetResult();

        await FinalVerificationTests.InReviewAsync(overseer, address);
        var messages = SquadMessages(overseer);
        Assert.Equal(4, messages.Count);
        Assert.Contains("Rename it.", messages[1].Message);
        Assert.Contains("Move it up.", messages[2].Message);
        Assert.Contains("No, down.", messages[3].Message);
        Assert.Equal(
            [
                Said("Adding it."), Delivered("Rename it."), Said("One."), Delivered("Move it up."), Said("Two."), Delivered("No, down."),
                Said("Three."),
            ],
            StreamOn(await GetAsync(overseer, task)));
        Assert.Equal(
            ["Attempt 1: passed."],
            VerificationTests.AttemptsOn(await GetAsync(overseer, address), 1).Select(attempt => attempt.Heading));
    }

    [Fact]
    public async Task A_message_sent_during_verification_is_delivered_before_the_Task_is_Done_and_the_work_is_verified_again()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        AddManifest(overseer);
        var testing = new TaskCompletionSource();
        var steered = new TaskCompletionSource();
        overseer.Sandboxes.HoldCommandsContaining("npm test", testing.Task);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Done(), Done(steered.Task)));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var task = address + "/tasks/1";
        await TestingAsync(overseer);

        // The message box is there while the work is verified too.
        await BoxAsync(overseer, task);
        using var sent = await SendAsync(overseer, task, session: 1, "Call it Download.");
        Assert.Equal([Waiting("Call it Download.")], StreamOn(await GetAsync(overseer, task)));
        testing.SetResult();

        var messages = await SquadMessagesAsync(overseer, 2);
        Assert.Contains("Call it Download.", messages[1].Message);
        await StreamAsync(overseer, task, Delivered("Call it Download."));
        var page = await GetAsync(overseer, address);
        Assert.Equal(["Running"], TasksOn(page).Select(shown => shown.State));
        Assert.Equal(["Attempt 1: passed."], VerificationTests.AttemptsOn(page, 1).Select(attempt => attempt.Heading));
        Assert.DoesNotContain(overseer.Sandboxes.CommandsRun, command => command.Command.Contains("git push"));

        steered.SetResult();

        await FinalVerificationTests.InReviewAsync(overseer, address);
        Assert.Equal(
            ["Attempt 1: passed.", "Attempt 2: passed."],
            VerificationTests.AttemptsOn(await GetAsync(overseer, address), 1).Select(attempt => attempt.Heading));
        Assert.Single(SquadSessionsStarted(overseer));
    }

    [Fact]
    public async Task A_verification_that_passed_before_a_message_had_the_work_verified_again_uses_none_of_the_allowed_attempts()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        AddManifest(overseer);
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 2);
        var testing = new TaskCompletionSource();
        overseer.Sandboxes.HoldCommandsContaining("npm test", testing.Task);
        overseer.Sandboxes.ScriptCommandsContaining("npm test", Passes, Fails, Passes);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Done()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await TestingAsync(overseer);
        var task = address + "/tasks/1";
        await BoxAsync(overseer, task);
        using var sent = await SendAsync(overseer, task, session: 1, "Call it Download.");

        testing.SetResult();

        // What the message led to fails, and the agent still has an attempt to fix it.
        await FinalVerificationTests.InReviewAsync(overseer, address);
        Assert.Equal(
            ["Attempt 1: passed.", "Attempt 2: failed. The test command of acme/web failed with exit code 1.", "Attempt 3: passed."],
            VerificationTests.AttemptsOn(await GetAsync(overseer, address), 1).Select(attempt => attempt.Heading));
        var messages = SquadMessages(overseer);
        Assert.Equal(3, messages.Count);
        Assert.Contains("attempt 2 failed. Failed attempts so far: 1, of the 2 allowed.", messages[2].Message);
    }

    [Fact]
    public async Task After_a_verification_that_passed_the_page_the_agent_and_the_Failed_Task_name_the_attempts_alike()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        AddManifest(overseer);
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 2);
        var testing = new TaskCompletionSource();
        overseer.Sandboxes.HoldCommandsContaining("npm test", testing.Task);
        overseer.Sandboxes.ScriptCommandsContaining("npm test", Passes, Fails);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Done()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await TestingAsync(overseer);
        var task = address + "/tasks/1";
        await BoxAsync(overseer, task);
        using var sent = await SendAsync(overseer, task, session: 1, "Call it Download.");

        testing.SetResult();

        var failed = await ImplementationTests.FailedAsync(overseer, address);
        const string failure = "The test command of acme/web failed with exit code 1.";
        Assert.Equal(
            ["Attempt 1: passed.", $"Attempt 2: failed. {failure}", $"Attempt 3: failed. {failure}"],
            VerificationTests.AttemptsOn(await GetAsync(overseer, address), 1).Select(attempt => attempt.Heading));
        Assert.Contains("attempt 2 failed. Failed attempts so far: 1, of the 2 allowed.", SquadMessages(overseer)[2].Message);
        Assert.Equal(
            $"Task 1 is a Failed Task: its work didn't pass verification in 2 attempts. In the last one, attempt 3: {failure}",
            failed.Status);
    }

    [Fact]
    public async Task A_message_sent_during_a_verification_that_fails_follows_the_turn_that_is_told_what_failed()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        AddManifest(overseer);
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 2);
        var testing = new TaskCompletionSource();
        overseer.Sandboxes.HoldCommandsContaining("npm test", testing.Task);
        overseer.Sandboxes.ScriptCommandsContaining("npm test", Fails, Passes);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Done()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await TestingAsync(overseer);
        var task = address + "/tasks/1";
        await BoxAsync(overseer, task);
        using var sent = await SendAsync(overseer, task, session: 1, "Call it Download.");

        testing.SetResult();

        await FinalVerificationTests.InReviewAsync(overseer, address);
        var messages = SquadMessages(overseer);
        Assert.Equal(3, messages.Count);
        Assert.Contains("attempt 1 failed. Failed attempts so far: 1, of the 2 allowed.", messages[1].Message);
        Assert.Contains("Call it Download.", messages[2].Message);
        // The two turns after the failure led to one verification, the second of the two attempts.
        Assert.Equal(
            ["Attempt 1: failed. The test command of acme/web failed with exit code 1.", "Attempt 2: passed."],
            VerificationTests.AttemptsOn(await GetAsync(overseer, address), 1).Select(attempt => attempt.Heading));
    }

    [Fact]
    public async Task A_message_still_waiting_as_the_session_breaks_off_is_shown_as_never_delivered_and_Retry_doesnt_send_it()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var working = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer,
            new FakeAgentSession(Done(working.Task), new FakeAgentTurn([], new AgentOutcome.Done()) { Failure = "It exited." }),
            new FakeAgentSession(FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var task = address + "/tasks/1";
        await BoxAsync(overseer, task);
        using var first = await SendAsync(overseer, task, session: 1, "Rename it.");
        using var second = await SendAsync(overseer, task, session: 1, "Move it up.");
        // Waiting messages aren't reported there.
        Assert.Empty(UndeliveredOn(await GetAsync(overseer, address)));

        working.SetResult();

        await ImplementationTests.FailedAsync(overseer, address);
        var page = await GetAsync(overseer, task);
        Assert.Equal([Delivered("Rename it."), NotDelivered("Move it up.")], StreamOn(page));
        Assert.Contains("Not delivered: the agent session ended first", page);
        Assert.False(OffersBox(page));
        // Noticed on the Intent's page too.
        const string notice = "A message you sent was not delivered. Read it on the Task's page.";
        Assert.Equal([(1, notice)], UndeliveredOn(await GetAsync(overseer, address)));

        using var retried = await ImplementationTests.RetryAsync(overseer, address);

        await SquadMessagesAsync(overseer, 3);
        page = await BoxAsync(overseer, task);
        Assert.Equal([Delivered("Rename it."), NotDelivered("Move it up.")], StreamOn(page));
        Assert.DoesNotContain("Move it up.", SquadMessages(overseer)[2].Message);
        // Also while another session works on the Task, which takes messages of its own.
        using var third = await SendAsync(overseer, task, session: 2, "Keep it.");
        Assert.Equal([(1, notice)], UndeliveredOn(await GetAsync(overseer, address)));
    }

    [Fact]
    public async Task A_message_that_cant_be_sent_as_the_agent_died_during_verification_is_shown_as_never_delivered()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        AddManifest(overseer);
        var testing = new TaskCompletionSource();
        overseer.Sandboxes.HoldCommandsContaining("npm test", testing.Task);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            Done(), new FakeAgentTurn([], new AgentOutcome.Done()) { SendFailure = "The agent process exited with code 1." }));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await TestingAsync(overseer);
        var task = address + "/tasks/1";
        await BoxAsync(overseer, task);
        using var sent = await SendAsync(overseer, task, session: 1, "Call it Download.");

        testing.SetResult();

        var failed = await ImplementationTests.FailedAsync(overseer, address);
        Assert.Equal("The Developer's session for Task 1 failed: The agent process exited with code 1.", failed.Status);
        Assert.Equal([NotDelivered("Call it Download.")], StreamOn(await GetAsync(overseer, task)));
        Assert.Single(SquadMessages(overseer));
    }

    [Fact]
    public async Task A_message_waiting_as_the_last_allowed_attempt_fails_is_shown_as_never_delivered()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        AddManifest(overseer);
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 1);
        var testing = new TaskCompletionSource();
        overseer.Sandboxes.HoldCommandsContaining("npm test", testing.Task);
        overseer.Sandboxes.ScriptCommandsContaining("npm test", Fails);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Done()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await TestingAsync(overseer);
        var task = address + "/tasks/1";
        await BoxAsync(overseer, task);
        using var sent = await SendAsync(overseer, task, session: 1, "Call it Download.");

        testing.SetResult();

        await ImplementationTests.FailedAsync(overseer, address);
        Assert.Equal(["Failed"], TasksOn(await GetAsync(overseer, address)).Select(shown => shown.State));
        Assert.Equal([NotDelivered("Call it Download.")], StreamOn(await GetAsync(overseer, task)));
        Assert.Single(SquadMessages(overseer));
    }

    [Fact]
    public async Task A_message_waiting_as_Overseer_restarts_is_shown_as_never_delivered()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string task;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            task = await ImplementationTests.ImplementingAsync(first, "Export data", "acme/web") + "/tasks/1";
            await BoxAsync(first, task);
            using var sent = await SendAsync(first, task, session: 1, "Rename it.");
            using var another = await SendAsync(first, task, session: 1, "Move it up.");
        }
        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        // The session that carries the Task on, which gets none of them.
        var message = Assert.Single(await TaskTests.SquadMessagesAsync(second, 1));
        var page = await BoxAsync(second, task);

        Assert.DoesNotContain("Rename it.", message.Message);
        Assert.Equal([NotDelivered("Rename it."), NotDelivered("Move it up.")], StreamOn(page));
        Assert.Equal(
            [(1, "2 messages you sent were not delivered. Read them on the Task's page.")],
            UndeliveredOn(await GetAsync(second, task[..task.IndexOf("/tasks/", StringComparison.Ordinal)])));
    }

    [Fact]
    public async Task A_message_sent_once_its_session_has_ended_is_refused_saying_why_with_what_was_written()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var working = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Done(working.Task)));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var task = address + "/tasks/1";
        await BoxAsync(overseer, task);
        var tab = await overseer.OpenAsync(task);
        working.SetResult();
        await FinalVerificationTests.InReviewAsync(overseer, address);

        using var sent = await tab.SubmitFormAsync("message-agent", MessageFields(1, "Call it <Download>."));

        var page = await sent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            "Your message was not sent: The agent session it was written to takes no more messages: it has ended, or its work has "
            + "passed verification.",
            Alert(page));
        // Not lost, though the page offers no box to send it from.
        Assert.Equal("Call it <Download>.", ElementText(page, "agent-message-unsent"));
        Assert.False(OffersBox(page));
        Assert.Empty(StreamOn(page));
        Assert.Single(SquadMessages(overseer));
    }

    [Fact]
    public async Task A_message_written_to_a_session_that_another_has_followed_is_refused_and_left_in_the_box()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer,
            new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done()) { Failure = "It exited." }),
            new FakeAgentSession(FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var task = address + "/tasks/1";
        await ImplementationTests.FailedAsync(overseer, address);
        using var retried = await ImplementationTests.RetryAsync(overseer, address);
        await SquadMessagesAsync(overseer, 2);
        await BoxAsync(overseer, task);

        // Sent from a page opened while the first session worked.
        using var sent = await SendAsync(overseer, task, session: 1, "Rename it.");

        var page = await sent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith("Your message was not sent: The agent session it was written to takes no more messages", Alert(page));
        Assert.Equal("Rename it.", MessageBox(page));
        Assert.Empty(StreamOn(page));
    }

    [Fact]
    public async Task A_Task_whose_work_has_passed_takes_no_more_messages_while_it_is_pushed()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        AddManifest(overseer);
        VerificationTests.Changes(overseer, "acme/web", "src/export.ts");
        var pushing = new TaskCompletionSource();
        overseer.Sandboxes.HoldCommandsContaining("git push", pushing.Task);
        var working = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Done(working.Task)));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var task = address + "/tasks/1";
        await BoxAsync(overseer, task);
        var tab = await overseer.OpenAsync(task);
        working.SetResult();
        await EventuallyAsync(
            () => Task.FromResult(overseer.Sandboxes.CommandsRun.Any(command => command.Command.Contains("git push")) ? "" : null),
            "the push");

        var page = await GetAsync(overseer, task);
        using var sent = await tab.SubmitFormAsync("message-agent", MessageFields(1, "Rename it."));

        // Still Running, with no agent left to tell.
        Assert.Equal(["Running"], TasksOn(await GetAsync(overseer, address)).Select(shown => shown.State));
        Assert.False(OffersBox(page));
        var refused = await sent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith("Your message was not sent: The agent session it was written to takes no more messages", Alert(refused));
        Assert.Empty(StreamOn(refused));
        pushing.SetResult();
    }

    [Fact]
    public async Task A_message_without_text_is_refused()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var task = address + "/tasks/1";
        await BoxAsync(overseer, task);

        using var sent = await SendAsync(overseer, task, session: 1, "  \r\n ");

        var page = await sent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Your message was not sent: A message needs text: write what the agent should know.", Alert(page));
        Assert.Empty(StreamOn(page));
    }

    [Fact]
    public async Task A_message_never_puts_the_GitHub_token_on_the_page_or_in_the_database_or_in_front_of_the_agent()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var working = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Done(working.Task), FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var task = address + "/tasks/1";
        await BoxAsync(overseer, task);

        using var sent = await SendAsync(overseer, task, session: 1, $"Use {Token} to fetch.");

        Assert.Equal([Waiting("Use *** to fetch.")], StreamOn(await GetAsync(overseer, task)));
        Assert.DoesNotContain(await overseer.DumpDatabaseAsync(), table => table.Contains(Token));
        working.SetResult();
        var messages = await SquadMessagesAsync(overseer, 2);
        Assert.DoesNotContain(Token, messages[1].Message);
        await StreamAsync(overseer, task, Delivered("Use *** to fetch."));
        Assert.DoesNotContain(await overseer.DumpDatabaseAsync(), table => table.Contains(Token));
    }

    [Fact]
    public async Task A_message_never_keeps_a_token_saved_in_Settings_a_moment_ago_also_before_anything_has_used_it()
    {
        const string saved = "sk-gateway-0123456789abcdef";
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var task = address + "/tasks/1";
        await BoxAsync(overseer, task);
        // Saved after the session started, so no agent was given it.
        await SessionLogTests.SaveProviderTokenAsync(overseer, saved);

        using var sent = await SendAsync(overseer, task, session: 1, $"Use {saved}.");

        Assert.Equal([Waiting("Use ***.")], StreamOn(await GetAsync(overseer, task)));
        Assert.DoesNotContain(await overseer.DumpDatabaseAsync(), table => table.Contains(saved));
    }

    [Fact]
    public async Task What_cant_be_stored_is_gone_from_a_message_before_it_is_checked_for_text_and_secrets()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var task = address + "/tasks/1";
        await BoxAsync(overseer, task);
        // The Portal's form takes no NUL, so the application is given the message directly.
        var logs = overseer.Services.GetRequiredService<SessionLogs>();
        Task<string?> Send(string message) =>
            logs.SendMessageAsync(
                new TaskKey(Guid.Parse(address.Split('/')[^1]), 1), session: 1, message, TestContext.Current.CancellationToken);

        Assert.Equal("A message needs text: write what the agent should know.", await Send("\0 \r\n\0"));
        // A NUL splits the token, and without it the token is whole again.
        Assert.Null(await Send($"\0 Use {Token[..12]}\0{Token[12..]}. \0"));

        Assert.Equal([Waiting("Use ***.")], StreamOn(await GetAsync(overseer, task)));
        Assert.DoesNotContain(await overseer.DumpDatabaseAsync(), table => table.Contains(Token));
    }

    [Fact]
    public async Task A_turn_ending_in_a_question_with_a_message_waiting_asks_first_and_the_message_follows_the_answers_turn()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        AddManifest(overseer);
        var working = new TaskCompletionSource();
        var answered = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            new FakeAgentTurn([], new AgentOutcome.AskedQuestion("Which port?")) { EndsAfter = working.Task },
            Done(answered.Task),
            Done()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var task = address + "/tasks/1";
        await BoxAsync(overseer, task);
        using var sent = await SendAsync(overseer, task, session: 1, "Call it Download.");

        working.SetResult();

        await TaskQuestionTests.AskedAsync(overseer, address);
        Assert.Equal([Waiting("Call it Download.")], StreamOn(await GetAsync(overseer, task)));
        Assert.Single(SquadMessages(overseer));

        using var answer = await TaskQuestionTests.AnswerAsync(overseer, address, 1, "8080.");

        var messages = await SquadMessagesAsync(overseer, 2);
        Assert.Contains("8080.", messages[1].Message);
        Assert.DoesNotContain("Call it Download.", messages[1].Message);
        Assert.Equal([Waiting("Call it Download.")], StreamOn(await GetAsync(overseer, task)));

        answered.SetResult();

        await FinalVerificationTests.InReviewAsync(overseer, address);
        Assert.Contains("Call it Download.", SquadMessages(overseer)[2].Message);
        Assert.Equal([Delivered("Call it Download.")], StreamOn(await GetAsync(overseer, task)));
        Assert.Equal(
            ["Attempt 1: passed."],
            VerificationTests.AttemptsOn(await GetAsync(overseer, address), 1).Select(attempt => attempt.Heading));
    }

    [Fact]
    public async Task A_message_sent_while_an_answer_is_awaited_doesnt_answer_the_question_and_the_Tasks_page_says_it_waits()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var answered = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            new FakeAgentTurn([], new AgentOutcome.AskedQuestion("Which port?")), Done(answered.Task), FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        var task = address + "/tasks/1";
        await TaskQuestionTests.AskedAsync(overseer, address);
        var asked = await BoxAsync(overseer, task);
        Assert.Equal(
            "The agent waits for your answer to its question, which you give on the Intent's page. A message doesn't answer it: it is "
            + "delivered after the turn your answer starts.",
            ElementText(asked, "agent-message-question"));

        using var sent = await SendAsync(overseer, task, session: 1, "Call it Download.");

        Assert.Equal([Waiting("Call it Download.")], StreamOn(await GetAsync(overseer, task)));
        await TaskQuestionTests.AskedAsync(overseer, address);
        Assert.Single(SquadMessages(overseer));

        using var answer = await TaskQuestionTests.AnswerAsync(overseer, address, 1, "8080.");

        await SquadMessagesAsync(overseer, 2);
        Assert.DoesNotContain("class=\"agent-message-question\"", await GetAsync(overseer, task));
        answered.SetResult();
        Assert.Contains("Call it Download.", (await SquadMessagesAsync(overseer, 3))[2].Message);
        await StreamAsync(overseer, task, Delivered("Call it Download."));
    }

    private static readonly CommandResult Passes = new(0, "");

    private static readonly CommandResult Fails = new(1, "1 test failed");

    /// <summary>Waits for Overseer to be running the tests, which a verification does.</summary>
    private static Task TestingAsync(RunningOverseer overseer) =>
        EventuallyAsync(
            () => Task.FromResult(overseer.Sandboxes.CommandsRun.Any(command => command.Command.Contains("npm test")) ? "" : null),
            "the tests to run");

    /// <summary>Sends a message on the Task's page, as a browser would send the form it showed for that session.</summary>
    private static Task<HttpResponseMessage> SendAsync(RunningOverseer overseer, string taskAddress, int session, string message) =>
        overseer.SubmitFormAsync(taskAddress, "message-agent", MessageFields(session, message));

    private static KeyValuePair<string, string>[] MessageFields(int session, string message) =>
        [new("Session", session.ToString(CultureInfo.InvariantCulture)), new("Message", message)];

    /// <summary>Waits for the Task's page to show this, and nothing else, in the streams of its sessions.</summary>
    private static Task<string> StreamAsync(RunningOverseer overseer, string taskAddress, params Shown[] expected) =>
        EventuallyAsync(
            async () => await GetAsync(overseer, taskAddress) is var page && StreamOn(page).SequenceEqual(expected) ? page : null,
            $"the stream to show {string.Join(", ", expected.AsEnumerable())}");

    /// <summary>What the streams of the Task's page show, in order: what the agent said, and the user's messages.</summary>
    private static IReadOnlyList<Shown> StreamOn(string page) =>
        [.. Regex.Matches(page, "<li class=\"session-(message|user-message)\"([^>]*)>(.*?)</li>", RegexOptions.Singleline)
            .Select(entry => entry.Groups[1].Value == "message"
                ? Said(WebUtility.HtmlDecode(entry.Groups[3].Value))
                : new Shown(
                    Regex.Match(entry.Groups[2].Value, "data-delivery=\"(\\w+)\"").Groups[1].Value,
                    ElementText(entry.Groups[3].Value, "session-user-text")))];

    private static Shown Said(string text) => new("Agent", text);

    private static Shown Waiting(string text) => new("Waiting", text);

    private static Shown Delivered(string text) => new("Delivered", text);

    private static Shown NotDelivered(string text) => new("NotDelivered", text);

    /// <summary>What the Intent's page says about messages that were never delivered, under the Tasks it says it of.</summary>
    private static IReadOnlyList<(int Task, string Notice)> UndeliveredOn(string page) =>
        [.. Regex.Matches(page, "<li class=\"intent-task\" data-task=\"(\\d+)\"[^>]*>(.*?)</li>", RegexOptions.Singleline)
            .Where(task => task.Groups[2].Value.Contains("class=\"intent-task-undelivered\""))
            .Select(task => (
                int.Parse(task.Groups[1].Value, CultureInfo.InvariantCulture),
                Regex.Replace(
                    WebUtility.HtmlDecode(Regex.Replace(
                        Regex.Match(task.Groups[2].Value, "<p class=\"intent-task-undelivered\">(.*?)</p>", RegexOptions.Singleline).Groups[1].Value,
                        "<[^>]*>", "")),
                    "\\s+", " ").Trim()))];

    /// <summary>What the page's message box holds.</summary>
    private static string MessageBox(string page) =>
        WebUtility.HtmlDecode(
            Regex.Match(page, "<textarea name=\"Message\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline).Groups[1].Value).Trim();

    /// <summary>An entry of a stream.</summary>
    /// <param name="What">Who said it: the agent, or, for a message of the user's, how far it got.</param>
    private sealed record Shown(string What, string Text);

    private static AgentUpdate Says(string text) => SessionLogTests.Says(text);

    private static void AddManifest(RunningOverseer overseer) =>
        overseer.Sandboxes.AddRepositoryFile("acme/web", ".overseer.yml", "setup: npm ci\nbuild: npm run build\ntest: npm test\n");

    /// <summary>Waits for the Task's page to offer the message box, which it does once the agent's session has started.</summary>
    private static Task<string> BoxAsync(RunningOverseer overseer, string taskAddress) =>
        EventuallyAsync(
            async () =>
            {
                // The Intent has no Tasks, nor pages of them, until its implementation takes it up.
                using var response = await overseer.Http.GetAsync(taskAddress, TestContext.Current.CancellationToken);
                var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                return OffersBox(page) ? page : null;
            },
            "the message box");

    private static bool OffersBox(string page) => page.Contains("<textarea name=\"Message\"");

    /// <summary>A turn the agent ends as done, once <paramref name="after"/> completes when there is one.</summary>
    private static FakeAgentTurn Done(Task? after = null, params AgentUpdate[] updates) =>
        new(updates, new AgentOutcome.Done()) { EndsAfter = after };
}
