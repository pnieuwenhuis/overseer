using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Overseer.Sandboxes;
using static Overseer.Tests.ImplementationTests;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;

namespace Overseer.Tests;

/// <summary>
/// Following a Task: what each agent session on it said and the tools it called, which its page shows and PostgreSQL keeps, and
/// the line the Task list says about the one running.
/// </summary>
public class SessionLogTests(PostgresServer postgres)
{
    [Fact]
    public async Task Opening_a_Task_shows_what_its_agent_says_as_whole_messages_and_the_tools_it_calls_in_order()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(FakeAgentTurn.Endless(
            Says("I'll look at "),
            Says("the orders."),
            Calls("1", "read"),
            Calls("1", "read: src/orders.ts"),
            Calls("2", "bash: npm test"),
            Says("The tests "),
            // More about an earlier call doesn't cut the message short.
            Calls("1", "read: src/orders/index.ts"),
            Says("pass."))));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await TaskTests.StatesAsync(overseer, address, "Running");

        var page = await TaskPageAsync(overseer, $"{address}/tasks/1", sessions => sessions is [{ Entries.Count: 4 }]);

        Assert.Contains($"href=\"{address[1..]}/tasks/1\"", await GetAsync(overseer, address));
        var session = Assert.Single(SessionsOn(page));
        Assert.Equal("Session 1: Developer", session.Heading);
        Assert.Equal(
            [
                Message("I'll look at the orders."),
                ToolCall("read: src/orders/index.ts"),
                ToolCall("bash: npm test"),
                Message("The tests pass."),
            ],
            session.Entries);
    }

    [Fact]
    public async Task Every_session_of_a_retried_Task_keeps_its_log_which_is_read_after_the_Task_has_ended_and_after_a_restart()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        IReadOnlyList<ShownSession> ended;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            first.Sandboxes.ScriptAgentSessions(
                AgentRole.Developer,
                new FakeAgentSession(new FakeAgentTurn([Says("Reading the code")], new AgentOutcome.Done()) { Failure = "It exited." }),
                new FakeAgentSession(new FakeAgentTurn([Calls("1", "edit: src/orders.ts"), Says("Added.")], new AgentOutcome.Done())));
            address = await ImplementationTests.ImplementingAsync(first, "Export data", "acme/web");
            await FailedAsync(first, address);
            // What a session that broke off said until then is kept.
            Assert.Equal([Message("Reading the code")], Assert.Single(SessionsOn(await GetAsync(first, address + "/tasks/1"))).Entries);

            using var retried = await ImplementationTests.RetryAsync(first, address);

            await TaskTests.StatesAsync(first, address, "Done");
            ended = SessionsOn(await GetAsync(first, address + "/tasks/1"));
        }

        await using var second = await StartWithRepositoriesAsync(connectionString, []);
        var restarted = SessionsOn(await GetAsync(second, address + "/tasks/1"));

        foreach (var sessions in new[] { ended, restarted })
        {
            Assert.Equal(["Session 1: Developer", "Session 2: Developer"], sessions.Select(session => session.Heading));
            Assert.Equal([Message("Reading the code")], sessions[0].Entries);
            Assert.Equal([ToolCall("edit: src/orders.ts"), Message("Added.")], sessions[1].Entries);
        }
    }

    [Fact]
    public async Task Each_turn_of_a_session_has_messages_of_its_own()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.AddRepositoryFile("acme/web", ".overseer.yml", "setup: npm ci\nbuild: npm run build\ntest: npm test\n");
        overseer.Sandboxes.ScriptCommandsContaining("npm test", new CommandResult(1, "1 test failed"), new CommandResult(0, ""));
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            new FakeAgentTurn([Says("Added it.")], new AgentOutcome.Done()),
            // A tool call's ID is its turn's own.
            new FakeAgentTurn([Says("Fixed it.")], new AgentOutcome.Done())));

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        await TaskTests.StatesAsync(overseer, address, "Done");
        var session = Assert.Single(SessionsOn(await GetAsync(overseer, address + "/tasks/1")));
        Assert.Equal([Message("Added it."), Message("Fixed it.")], session.Entries);
    }

    [Fact]
    public async Task The_Tasks_stream_shows_the_agents_question_and_your_answer_where_they_came_and_keeps_them_with_the_log()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        IReadOnlyList<ShownEntry> followed;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            // As a question asked with the agent's own tool for it, which is no update of the turn.
            first.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
                new FakeAgentTurn([Says("Reading the code.")], new AgentOutcome.AskedQuestion("Which port,\n<80> or <8080>?")),
                new FakeAgentTurn([Says("Added.")], new AgentOutcome.Done())));
            address = await ImplementationTests.ImplementingAsync(first, "Export data", "acme/web");
            await TaskQuestionTests.AskedAsync(first, address);
            Assert.Equal(
                [Message("Reading the code."), Question("Which port,\n<80> or <8080>?")],
                Assert.Single(SessionsOn(await GetAsync(first, address + "/tasks/1"))).Entries);

            using var answered = await TaskQuestionTests.AnswerAsync(first, address, 1, "<8080>.");

            await TaskTests.StatesAsync(first, address, "Done");
            followed = Assert.Single(SessionsOn(await GetAsync(first, address + "/tasks/1"))).Entries;
        }

        await using var second = await StartWithRepositoriesAsync(connectionString, []);
        var restarted = Assert.Single(SessionsOn(await GetAsync(second, address + "/tasks/1"))).Entries;

        foreach (var entries in new[] { followed, restarted })
        {
            Assert.Equal(
                [Message("Reading the code."), Question("Which port,\n<80> or <8080>?"), Answer("<8080>."), Message("Added.")],
                entries);
        }
    }

    [Fact]
    public async Task A_question_the_agent_asked_in_its_last_message_shows_in_the_stream_once_as_its_question()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(new FakeAgentTurn(
            [Says("Reading the code."), Calls("1", "read: src/server.ts"), Says($"\n\nIs {Token} the "), Says("right key?\n")],
            new AgentOutcome.AskedQuestion($"Is {Token} the right key?"))));

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        await TaskQuestionTests.AskedAsync(overseer, address);
        Assert.Equal(
            [Message("Reading the code."), ToolCall("read: src/server.ts"), Question("Is *** the right key?")],
            Assert.Single(SessionsOn(await GetAsync(overseer, address + "/tasks/1"))).Entries);
        await AssertNowhereAsync(overseer, Token);
    }

    [Theory]
    [InlineData("model provider token", ProviderToken)]
    [InlineData("GitHub token", Token)]
    public async Task A_secret_the_agent_comes_out_with_is_never_stored_or_shown_also_while_it_arrives_in_pieces(string what, string secret)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await SaveProviderTokenAsync(overseer, ProviderToken);
        var live = Channel.CreateUnbounded<AgentUpdate>();
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer, new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done()) { Live = live.Reader }));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        live.Writer.TryWrite(Says($"The {what} is {secret[..12]}"));

        var page = await TaskPageAsync(overseer, address + "/tasks/1", sessions => sessions is [{ Entries.Count: > 0 }]);
        Assert.Equal([Message($"The {what} is")], Assert.Single(SessionsOn(page)).Entries);
        await AssertNowhereAsync(overseer, secret[..12]);
        // Still taken out once it has been replaced, as the session was started with it.
        await SaveProviderTokenAsync(overseer, "sk-gateway-REPLACEMENT");

        live.Writer.TryWrite(Says($"{secret[12..]}, so I use it."));
        live.Writer.TryWrite(Calls("1", $"bash: curl -H 'Authorization: {secret}'"));

        page = await TaskPageAsync(overseer, address + "/tasks/1", sessions => sessions is [{ Entries.Count: 2 }]);
        Assert.Equal(
            [Message($"The {what} is ***, so I use it."), ToolCall("bash: curl -H 'Authorization: ***'")],
            Assert.Single(SessionsOn(page)).Entries);
        live.Writer.Complete();
        await TaskTests.StatesAsync(overseer, address, "Done");
        await AssertNowhereAsync(overseer, secret);
    }

    [Fact]
    public async Task A_turn_that_breaks_off_in_the_middle_of_a_secret_leaves_none_of_it_in_the_log()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            new FakeAgentTurn([Says($"The token is {Token[..12]}")], new AgentOutcome.Done()) { Failure = "It exited." }));

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        await FailedAsync(overseer, address);
        Assert.Equal(
            [Message("The token is")], Assert.Single(SessionsOn(await GetAsync(overseer, address + "/tasks/1"))).Entries);
        await AssertNowhereAsync(overseer, Token[..12]);
    }

    [Fact]
    public async Task A_message_that_ends_as_a_secret_begins_is_whole_once_the_agent_has_finished_it()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            new FakeAgentTurn([Says("All tokens start with github_pat_")], new AgentOutcome.Done())));

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        await TaskTests.StatesAsync(overseer, address, "Done");
        Assert.Equal(
            [Message("All tokens start with github_pat_")],
            Assert.Single(SessionsOn(await GetAsync(overseer, address + "/tasks/1"))).Entries);
    }

    [Fact]
    public async Task The_Task_list_says_in_one_line_what_the_running_Tasks_agent_is_doing()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api", "acme/web");
        var live = Channel.CreateUnbounded<AgentUpdate>();
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Designer,
            new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done()) { Live = live.Reader }.WithScreenshot()));
        var address = await TaskTests.ImplementingAsync(overseer, TaskTests.ThreeTasks(), "Export data", "acme/api", "acme/web");
        await TaskTests.StatesAsync(overseer, address, "Running", "Pending", "Pending");
        Assert.Empty(ActivitiesOn(await GetAsync(overseer, address)));

        // The last line of a message that says something.
        live.Writer.TryWrite(Says("I'll look at the table.\n\nThe button goes **above** it.\n```\n\n"));
        await ActivityAsync(overseer, address, (1, "The button goes **above** it."));

        // The first line of a tool call.
        live.Writer.TryWrite(Calls("1", "bash: cat > button.html <<'EOF'\n<button>Export</button>\nEOF"));
        await ActivityAsync(overseer, address, (1, "bash: cat > button.html <<'EOF'"));

        live.Writer.TryWrite(Says("Done."));
        await ActivityAsync(overseer, address, (1, "Done."));

        // The next Task's agent, which keeps working without a word, says nothing yet.
        live.Writer.Complete();
        await TaskTests.StatesAsync(overseer, address, "Done", "Running", "Pending");
        Assert.Empty(ActivitiesOn(await GetAsync(overseer, address)));
    }

    [Fact]
    public async Task A_Task_run_again_says_nothing_of_what_its_last_session_was_doing()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer,
            new FakeAgentSession(new FakeAgentTurn([Says("Reading the code")], new AgentOutcome.Done()) { Failure = "It exited." }),
            new FakeAgentSession(FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await FailedAsync(overseer, address);
        Assert.Empty(ActivitiesOn(await GetAsync(overseer, address)));

        using var retried = await ImplementationTests.RetryAsync(overseer, address);

        await TaskTests.SquadMessagesAsync(overseer, 2);
        await TaskTests.StatesAsync(overseer, address, "Running");
        Assert.Empty(ActivitiesOn(await GetAsync(overseer, address)));
    }

    [Fact]
    public async Task The_Task_list_says_nothing_the_agent_did_last_while_its_work_is_verified_or_its_question_awaits_an_answer()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.AddRepositoryFile("acme/web", ".overseer.yml", "setup: npm ci\nbuild: npm run build\ntest: npm test\n");
        var testing = new TaskCompletionSource();
        overseer.Sandboxes.HoldCommandsContaining("npm test", testing.Task);
        overseer.Sandboxes.ScriptCommandsContaining("npm test", new CommandResult(1, "1 test failed"), new CommandResult(0, ""));
        var fixing = Channel.CreateUnbounded<AgentUpdate>();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            new FakeAgentTurn([Says("Added it.")], new AgentOutcome.Done()),
            new FakeAgentTurn([], new AgentOutcome.AskedQuestion("Which port?")) { Live = fixing.Reader },
            FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        await EventuallyAsync(
            () => Task.FromResult(overseer.Sandboxes.CommandsRun.Any(command => command.Command.Contains("npm test")) ? "" : null),
            "the tests to run");

        Assert.Empty(ActivitiesOn(await GetAsync(overseer, address)));
        testing.SetResult();

        // The agent's next turn says what it is doing again.
        fixing.Writer.TryWrite(Says("Fixing the test."));
        await ActivityAsync(overseer, address, (1, "Fixing the test."));
        fixing.Writer.Complete();

        var asked = await TaskQuestionTests.AskedAsync(overseer, address);
        Assert.Empty(ActivitiesOn(asked));

        // Nor once it is answered, until the agent says something.
        using var answered = await TaskQuestionTests.AnswerAsync(overseer, address, 1, "8080.");
        await TaskTests.SquadMessagesAsync(overseer, 3);
        Assert.Empty(ActivitiesOn(await GetAsync(overseer, address)));
    }

    /// <summary>Waits for the Intent's page to say this, and nothing else, about what its Tasks' agents are doing.</summary>
    private static Task ActivityAsync(RunningOverseer overseer, string address, params (int Task, string Line)[] expected) =>
        EventuallyAsync(
            async () => ActivitiesOn(await GetAsync(overseer, address)).SequenceEqual(expected) ? "" : null,
            $"the Task list to say {string.Join(", ", expected)}");

    /// <summary>What the Intent's page says each Task's agent is doing, for the Tasks it says it of.</summary>
    private static IReadOnlyList<(int Task, string Line)> ActivitiesOn(string page) =>
        [.. Regex.Matches(page, "<li class=\"intent-task\" data-task=\"(\\d+)\"[^>]*>(.*?)</li>", RegexOptions.Singleline)
            .Where(task => task.Groups[2].Value.Contains("class=\"intent-task-activity\""))
            .Select(task => (int.Parse(task.Groups[1].Value), ElementText(task.Groups[2].Value, "intent-task-activity")))];

    private const string ProviderToken = "sk-gateway-0123456789abcdef";

    internal static async Task SaveProviderTokenAsync(RunningOverseer overseer, string token)
    {
        using var response = await overseer.SubmitFormAsync("/settings", "provider-token", new Dictionary<string, string>
        {
            ["ProviderTokenName"] = "gateway",
            ["ProviderToken"] = token,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Asserts that neither the database nor any page about the Intent's Task holds <paramref name="secret"/>.</summary>
    private static async Task AssertNowhereAsync(RunningOverseer overseer, string secret)
    {
        // No application seam can show what a database does not hold, so this reads it directly.
        Assert.DoesNotContain(await overseer.DumpDatabaseAsync(), table => table.Contains(secret));
        var address = await AddressAsync(overseer, "Export data");
        Assert.DoesNotContain(secret, await GetAsync(overseer, address));
        Assert.DoesNotContain(secret, await GetAsync(overseer, address + "/tasks/1"));
    }

    [Fact]
    public async Task The_page_of_a_Task_no_agent_has_worked_on_says_so_and_a_Task_the_Intent_doesnt_have_has_no_page()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api", "acme/web");
        var address = await TaskTests.ImplementingAsync(overseer, TaskTests.ThreeTasks(), "Export data", "acme/api", "acme/web");
        await TaskTests.StatesAsync(overseer, address, "Running", "Pending", "Pending");

        var page = await GetAsync(overseer, address + "/tasks/2");

        Assert.Contains("Task 2: Add the export endpoint", WebUtility.HtmlDecode(page));
        Assert.Equal("No agent has worked on this Task yet.", ElementText(page, "agent-sessions-none"));
        using var missing = await overseer.Http.GetAsync(address + "/tasks/4", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    internal static AgentUpdate Says(string text) => new AgentUpdate.Message(text);

    internal static AgentUpdate Calls(string id, string title) => new AgentUpdate.ToolCall(id, title);

    private static ShownEntry Message(string text) => new("message", text);

    private static ShownEntry ToolCall(string title) => new("tool-call", title);

    private static ShownEntry Question(string text) => new("question", text);

    private static ShownEntry Answer(string text) => new("answer", text);

    /// <summary>Waits for the Task's page to show its sessions as <paramref name="expected"/> wants them.</summary>
    private static Task<string> TaskPageAsync(
        RunningOverseer overseer, string address, Func<IReadOnlyList<ShownSession>, bool> expected) =>
        EventuallyAsync(
            async () =>
            {
                // The Intent has no Tasks, nor pages of them, until its implementation takes it up.
                using var response = await overseer.Http.GetAsync(address, TestContext.Current.CancellationToken);
                var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                return response.IsSuccessStatusCode && expected(SessionsOn(page)) ? page : null;
            },
            "the Task's page to show its sessions");

    /// <summary>The agent sessions a Task's page shows, in its order.</summary>
    private static IReadOnlyList<ShownSession> SessionsOn(string page) =>
        [.. Regex.Matches(page, "<section class=\"agent-session\"[^>]*>(.*?)</section>", RegexOptions.Singleline).Select(session => new ShownSession(
            ElementText(session.Groups[1].Value, "agent-session-heading"),
            [.. Regex.Matches(session.Groups[1].Value, "<li class=\"session-(message|tool-call|question|answer)\"[^>]*>(.*?)</li>", RegexOptions.Singleline)
                .Select(entry => new ShownEntry(entry.Groups[1].Value, entry.Groups[1].Value switch
                {
                    "message" => WebUtility.HtmlDecode(entry.Groups[2].Value),
                    "tool-call" => ElementText(entry.Groups[2].Value, "session-tool-title"),
                    var kind => ElementText(entry.Groups[2].Value, $"session-{kind}-text"),
                }))]))];

    private sealed record ShownSession(string Heading, IReadOnlyList<ShownEntry> Entries);

    private sealed record ShownEntry(string Kind, string Text);
}
