using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Overseer.Implementation;
using Overseer.Sandboxes;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;
using static Overseer.Tests.SessionLogTests;
using static Overseer.Tests.TaskTests;

namespace Overseer.Tests;

/// <summary>
/// An agent asks a question: a Task's agent that ends its turn with one waits for the human's answer, which the Intent's detail
/// page takes and the same session gets.
/// </summary>
public class TaskQuestionTests(PostgresServer postgres)
{
    [Fact]
    public async Task A_turn_ending_in_a_question_turns_the_Intent_orange_and_shows_the_question_with_the_running_Task_which_waits()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api", "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Designer, new FakeAgentSession(Asks("Should the button be <b>blue</b>?")));

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        var page = await AskedAsync(overseer, address);
        Assert.Equal([new ShownTaskQuestion(1, Open: true, "Should the button be <b>blue</b>?", Answer: null)], QuestionsOn(page, task: 1));
        Assert.Equal(["Running", "Pending", "Pending"], TasksOn(page).Select(task => task.State));
        Assert.Equal("Implementing", ElementText(page, "intent-state"));
        var implementation = ImplementationTests.ImplementationOn(page)!;
        Assert.Equal(
            ("AwaitingAnswer", "The agent asks you a question: answer it under its Task below.", ("Orange", "Needs Attention"), null),
            (implementation.Phase, implementation.Status, implementation.Light, implementation.RetryAction));
        Assert.Matches("<span class=\"intent-light\" data-light=\"Orange\">\\s*Needs Attention\\s*</span>", await GetAsync(overseer, "/"));
        // The agent waits, in its session and its Sandbox: nothing is verified, and no other Task starts.
        Assert.Single(SquadSessionsStarted(overseer));
        Assert.Empty(SquadSessionsStopped(overseer));
        Assert.Single(SquadMessages(overseer));
        Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(VerificationTests.AttemptsOn(page, 1));
    }

    [Fact]
    public async Task The_answer_goes_to_the_same_session_the_light_turns_green_and_the_Task_goes_on_to_verification()
    {
        await using var overseer = await StartAsync("acme/web");
        var working = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            Asks("Which port should it listen on?"), new FakeAgentTurn([], new AgentOutcome.Done()) { EndsAfter = working.Task }));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await AskedAsync(overseer, address);

        using var answered = await AnswerAsync(overseer, address, 1, "Port 8080,\r\nas the <proxy> expects.\r\n");

        // Back on the detail page, so reloading it doesn't answer again.
        Assert.Equal(address, answered.FinalPath());
        var messages = await SquadMessagesAsync(overseer, 2);
        Assert.Contains("Port 8080,\nas the <proxy> expects.", messages[1].Message);
        Assert.Equal(messages[0].Sandbox, messages[1].Sandbox);
        Assert.Single(SquadSessionsStarted(overseer));
        Assert.Empty(SquadSessionsStopped(overseer));
        var page = await GetAsync(overseer, address);
        var implementation = ImplementationTests.ImplementationOn(page)!;
        Assert.Equal(("Ready", ("Green", "Working")), (implementation.Phase, implementation.Light));
        Assert.Equal(
            [new ShownTaskQuestion(1, Open: false, "Which port should it listen on?", "Port 8080,\nas the <proxy> expects.")],
            QuestionsOn(page, task: 1));

        working.SetResult();

        await FinalVerificationTests.InReviewAsync(overseer, address);
        var verified = await GetAsync(overseer, address);
        Assert.Equal(["Attempt 1: passed."], VerificationTests.AttemptsOn(verified, 1).Select(attempt => attempt.Heading));
        // Kept with the Task.
        Assert.Equal(
            [new ShownTaskQuestion(1, Open: false, "Which port should it listen on?", "Port 8080,\nas the <proxy> expects.")],
            QuestionsOn(verified, task: 1));
    }

    [Fact]
    public async Task A_question_and_its_answer_use_no_verification_attempt_also_when_the_question_follows_a_failed_verification()
    {
        await using var overseer = await StartAsync("acme/web");
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 2);
        // A question before the first attempt, and another after the first has failed.
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            Asks("Which port?"), Done(), Asks("Skip that test?"), Done()));
        overseer.Sandboxes.ScriptCommandsContaining("npm test", new CommandResult(1, "1 test failed"), new CommandResult(0, ""));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await AskedAsync(overseer, address);
        using var first = await AnswerAsync(overseer, address, 1, "8080.");

        var page = await AskedAsync(overseer, address, question: 2);

        Assert.Equal(
            [
                new ShownTaskQuestion(1, Open: false, "Which port?", "8080."),
                new ShownTaskQuestion(2, Open: true, "Skip that test?", Answer: null),
            ],
            QuestionsOn(page, task: 1));
        Assert.Equal(
            ["Attempt 1: failed. The test command of acme/web failed with exit code 1."],
            VerificationTests.AttemptsOn(page, 1).Select(attempt => attempt.Heading.ReplaceLineEndings(" ")));
        Assert.Contains("attempt 1 failed. Failed attempts so far: 1, of the 2 allowed.", SquadMessages(overseer)[2].Message);

        using var second = await AnswerAsync(overseer, address, 2, "No, fix it.");

        // The last of the two attempts allowed is still there to be made.
        await FinalVerificationTests.InReviewAsync(overseer, address);
        var done = await GetAsync(overseer, address);
        Assert.Equal(
            ["Attempt 1: failed. The test command of acme/web failed with exit code 1.", "Attempt 2: passed."],
            VerificationTests.AttemptsOn(done, 1).Select(attempt => attempt.Heading));
        Assert.Contains("No, fix it.", SquadMessages(overseer)[3].Message);
        Assert.Single(SquadSessionsStarted(overseer));
    }

    [Fact]
    public async Task An_answer_is_taken_only_once()
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer, new FakeAgentSession(Asks("Which port?"), Asks("Which host?"), FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await AskedAsync(overseer, address);
        var one = await overseer.OpenAsync(address);
        var other = await overseer.OpenAsync(address);

        var submitted = await Task.WhenAll(
            one.SubmitFormAsync("answer-question", AnswerFields(1, "8080.")),
            other.SubmitFormAsync("answer-question", AnswerFields(1, "9090.")));

        var pages = await Task.WhenAll(
            submitted.Select(response => response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Single(pages, page => page.Contains("Your answer was not sent: Question 1 is already answered."));
        var asked = await AskedAsync(overseer, address, question: 2);
        var taken = QuestionsOn(asked, task: 1)[0].Answer;
        Assert.Contains(taken, new[] { "8080.", "9090." });
        Assert.Contains(taken!, SquadMessages(overseer)[1].Message);
        // Sent again from a page opened before the agent asked another: the new question doesn't take it.
        using var again = await other.SubmitFormAsync("answer-question", AnswerFields(1, "7070."));
        Assert.Equal(
            "Your answer was not sent: Question 1 is already answered.",
            Alert(await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        var page = await GetAsync(overseer, address);
        Assert.Equal(
            [
                new ShownTaskQuestion(1, Open: false, "Which port?", taken),
                new ShownTaskQuestion(2, Open: true, "Which host?", Answer: null),
            ],
            QuestionsOn(page, task: 1));
        Assert.Equal(2, SquadMessages(overseer).Count);
    }

    [Theory]
    [InlineData(1, "  \r\n ", "An answer needs text: write what the agent should know.")]
    [InlineData(2, "8080.", "No agent asked a question 2.")]
    public async Task An_answer_without_text_or_to_a_question_never_asked_is_refused(int question, string answer, string refusal)
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Asks("Which port?"), FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await AskedAsync(overseer, address);

        using var answered = await AnswerAsync(overseer, address, question, answer);

        var page = await answered.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal($"Your answer was not sent: {refusal}", Alert(page));
        Assert.Equal([new ShownTaskQuestion(1, Open: true, "Which port?", Answer: null)], QuestionsOn(page, task: 1));
        Assert.Equal(("Orange", "Needs Attention"), LightOn(page));
        Assert.Single(SquadMessages(overseer));
    }

    [Fact]
    public async Task A_restart_leaves_the_question_with_its_Task_unanswered_for_good_and_the_Task_runs_again_in_a_new_session()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            first.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Asks("Which port?")));
            address = await ImplementationTests.ImplementingAsync(first, "Export data", "acme/web");
            await AskedAsync(first, address);
        }
        // Held in the setup of the fresh Sandbox, so that no session has started yet.
        var settingUp = new TaskCompletionSource();
        await using var second = await StartWithRepositoriesAsync(connectionString, [], sandboxes: sandboxes =>
        {
            sandboxes.HoldCommandsContaining("git checkout", settingUp.Task);
            sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Asks("Which host?"), FakeAgentTurn.Endless()));
        });

        // From the moment the Portal answers.
        var page = await GetAsync(second, address);

        Assert.Equal(("Green", "Working"), LightOn(page));
        Assert.Equal(["Pending"], TasksOn(page).Select(task => task.State));
        Assert.Equal([new ShownTaskQuestion(1, Open: false, "Which port?", Answer: null)], QuestionsOn(page, task: 1));
        Assert.Contains("Never answered: the session that asked it has ended.", page);
        using var late = await AnswerAsync(second, address, 1, "8080.");
        const string ended = "Your answer was not sent: Question 1 no longer awaits an answer: the session that asked it has ended.";
        Assert.Equal(ended, Alert(await late.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));

        settingUp.SetResult();

        var asked = await AskedAsync(second, address, question: 2);
        Assert.Equal(
            [
                new ShownTaskQuestion(1, Open: false, "Which port?", Answer: null),
                new ShownTaskQuestion(2, Open: true, "Which host?", Answer: null),
            ],
            QuestionsOn(asked, task: 1));
        using var stale = await AnswerAsync(second, address, 1, "8080.");
        Assert.Equal(ended, Alert(await stale.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Single(SquadMessages(second));
    }

    [Fact]
    public async Task A_GitHub_token_replaced_while_the_question_waits_reaches_the_Sandbox_before_the_answer_does()
    {
        const string replacement = "github_pat_11REPLACEMENT9876543210";
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Asks("Which port?"), FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await AskedAsync(overseer, address);
        var sandbox = SquadMessages(overseer)[0].Sandbox;
        overseer.GitHub.AcceptToken(replacement, "octocat");
        using var saved = await overseer.SubmitFormAsync(
            "/settings", "github-token", new Dictionary<string, string> { ["GitHubToken"] = replacement });

        using var answered = await AnswerAsync(overseer, address, 1, "8080.");

        await SquadMessagesAsync(overseer, 2);
        Assert.Equal(replacement, overseer.Sandboxes.GitHubTokenOf(sandbox));
    }

    [Fact]
    public async Task An_answer_whose_Sandbox_cant_be_given_the_GitHub_token_is_kept_and_the_implementation_fails()
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Asks("Which port?")));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await AskedAsync(overseer, address);
        using var removed = await overseer.SubmitFormAsync("/settings", "github-token-remove", new Dictionary<string, string>());

        using var answered = await AnswerAsync(overseer, address, 1, "8080.");

        var failed = await ImplementationTests.FailedAsync(overseer, address);
        Assert.Equal("The Sandbox could not be given the GitHub token: No GitHub token is set: set one in Settings.", failed.Status);
        var page = await GetAsync(overseer, address);
        Assert.Equal([new ShownTaskQuestion(1, Open: false, "Which port?", "8080.")], QuestionsOn(page, task: 1));
        Assert.Single(SquadMessages(overseer));
    }

    [Fact]
    public async Task A_session_that_breaks_off_on_the_answer_fails_the_implementation_which_keeps_the_question_and_answer()
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            Asks("Which port?"), new FakeAgentTurn([], new AgentOutcome.Done()) { Failure = "The agent process exited with code 1." }));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await AskedAsync(overseer, address);

        using var answered = await AnswerAsync(overseer, address, 1, "8080.");

        var failed = await ImplementationTests.FailedAsync(overseer, address);
        Assert.Equal("The Developer's session for Task 1 failed: The agent process exited with code 1.", failed.Status);
        var page = await GetAsync(overseer, address);
        Assert.Equal(["Pending"], TasksOn(page).Select(task => task.State));
        Assert.Equal([new ShownTaskQuestion(1, Open: false, "Which port?", "8080.")], QuestionsOn(page, task: 1));
    }

    [Fact]
    public async Task A_question_never_puts_the_GitHub_token_on_the_page_or_in_the_database()
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            Asks($"git said: Bearer {RepositorySetupPortal.Token} was refused. Now what?")));

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        var page = await AskedAsync(overseer, address);
        Assert.Equal("git said: Bearer *** was refused. Now what?", Assert.Single(QuestionsOn(page, task: 1)).Text);
        Assert.DoesNotContain(await overseer.DumpDatabaseAsync(), table => table.Contains(RepositorySetupPortal.Token));
    }

    [Theory]
    [InlineData(ProviderToken)]
    // A NUL splits the token, and PostgreSQL takes no NUL: without it the token is whole again.
    [InlineData("github_pat_11\0WORKING0123456789")]
    [InlineData("sk-gateway-\00123456789abcdef")]
    public async Task A_question_never_puts_a_secret_on_the_page_or_in_the_database_however_the_agent_writes_it(string written)
    {
        await using var overseer = await StartAsync("acme/web");
        await SaveProviderTokenAsync(overseer, ProviderToken);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Asks($"Is {written} still valid?")));

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        var page = await AskedAsync(overseer, address);
        Assert.Equal("Is *** still valid?", Assert.Single(QuestionsOn(page, task: 1)).Text);
        await AssertNoSecretKeptAsync(overseer, ProviderToken, RepositorySetupPortal.Token);
    }

    [Theory]
    [InlineData(ProviderToken)]
    [InlineData(RepositorySetupPortal.Token)]
    public async Task An_answer_never_puts_a_secret_on_the_page_or_in_the_database_or_in_front_of_the_agent(string written)
    {
        await using var overseer = await StartAsync("acme/web");
        await SaveProviderTokenAsync(overseer, ProviderToken);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Asks("Which key?"), FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await AskedAsync(overseer, address);

        using var answered = await AnswerAsync(overseer, address, 1, $"Use {written}.");

        var messages = await SquadMessagesAsync(overseer, 2);
        Assert.Contains("Use ***.", messages[1].Message);
        Assert.Equal("Use ***.", Assert.Single(QuestionsOn(await GetAsync(overseer, address), task: 1)).Answer);
        await AssertNoSecretKeptAsync(overseer, ProviderToken, RepositorySetupPortal.Token);
    }

    [Fact]
    public async Task A_token_saved_in_Settings_a_moment_ago_is_taken_out_of_an_answer_also_before_anything_has_used_it()
    {
        const string gitHubToken = "github_pat_11REPLACEMENT9876543210";
        const string providerToken = "sk-gateway-REPLACEMENT";
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Asks("Which keys?"), FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await AskedAsync(overseer, address);
        overseer.GitHub.AcceptToken(gitHubToken, "octocat");
        using var saved = await overseer.SubmitFormAsync(
            "/settings", "github-token", new Dictionary<string, string> { ["GitHubToken"] = gitHubToken });
        await SaveProviderTokenAsync(overseer, providerToken);

        using var answered = await AnswerAsync(overseer, address, 1, $"{gitHubToken} and {providerToken}.");

        Assert.Contains("*** and ***.", (await SquadMessagesAsync(overseer, 2))[1].Message);
        await AssertNoSecretKeptAsync(overseer, gitHubToken, providerToken);
    }

    [Fact]
    public async Task What_cant_be_stored_is_gone_from_an_answer_before_it_is_checked_for_text_and_secrets()
    {
        await using var overseer = await StartAsync("acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(Asks("Which key?"), FakeAgentTurn.Endless()));
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await AskedAsync(overseer, address);
        // The Portal's form takes no NUL, so the application is given the answer directly.
        var runner = overseer.Services.GetRequiredService<TaskRunner>();
        Task<string?> Answer(string answer) =>
            runner.AnswerAsync(Guid.Parse(address.Split('/')[^1]), 1, answer, TestContext.Current.CancellationToken);

        Assert.Equal("An answer needs text: write what the agent should know.", await Answer("\0 \r\n\0"));
        const string token = RepositorySetupPortal.Token;
        // A NUL splits the token, and without it the token is whole again.
        Assert.Null(await Answer($"\0 Use {token[..12]}\0{token[12..]}. \0"));

        Assert.Contains("\n\nUse ***.\n\n", (await SquadMessagesAsync(overseer, 2))[1].Message.ReplaceLineEndings("\n"));
        Assert.Equal("Use ***.", Assert.Single(QuestionsOn(await GetAsync(overseer, address), task: 1)).Answer);
        await AssertNoSecretKeptAsync(overseer, token);
    }

    private const string ProviderToken = "sk-gateway-0123456789abcdef";

    private static async Task AssertNoSecretKeptAsync(RunningOverseer overseer, params string[] secrets)
    {
        // No application seam can show what a database does not hold, so this reads it directly.
        var tables = await overseer.DumpDatabaseAsync();
        Assert.All(secrets, secret => Assert.DoesNotContain(tables, table => table.Contains(secret)));
    }

    /// <summary>Overseer with these repositories configured, each with a Repository Manifest in every Sandbox that holds it.</summary>
    private async Task<RunningOverseer> StartAsync(params string[] repositories)
    {
        var overseer = await StartWithRepositoriesAsync(postgres, repositories);
        foreach (var repository in repositories)
        {
            overseer.Sandboxes.AddRepositoryFile(repository, ".overseer.yml", "setup: npm ci\nbuild: npm run build\ntest: npm test\n");
        }
        return overseer;
    }

    /// <summary>Answers a question on the Intent's detail page, as a browser would send the form.</summary>
    internal static Task<HttpResponseMessage> AnswerAsync(RunningOverseer overseer, string address, int question, string answer) =>
        overseer.SubmitFormAsync(address, "answer-question", AnswerFields(question, answer));

    /// <summary>What a browser sends for the form that answers a question.</summary>
    private static KeyValuePair<string, string>[] AnswerFields(int question, string answer) =>
        [new("Question", question.ToString(System.Globalization.CultureInfo.InvariantCulture)), new("Answer", answer)];

    /// <summary>A turn the agent ends with this question.</summary>
    private static FakeAgentTurn Asks(string question) => new([], new AgentOutcome.AskedQuestion(question));

    /// <summary>A turn the agent ends as done.</summary>
    private static FakeAgentTurn Done() => new([], new AgentOutcome.Done());

    /// <summary>
    /// Waits for the Intent's detail page to show the question with this number awaiting its answer, with the light orange, and gives
    /// the page. The page reads the light and where the implementation stands a moment before the questions, so a page read as the
    /// question comes may show those of the moment before.
    /// </summary>
    internal static Task<string> AskedAsync(RunningOverseer overseer, string address, int question = 1) =>
        EventuallyAsync(
            async () => await GetAsync(overseer, address) is var page
                && page.Contains($"data-question=\"{question}\" data-open")
                && ImplementationTests.ImplementationOn(page) is { Phase: "AwaitingAnswer", Light.Colour: "Orange" } ? page : null,
            $"question {question}");

    /// <summary>The questions the detail page shows with the Task, in order.</summary>
    internal static IReadOnlyList<ShownTaskQuestion> QuestionsOn(string page, int task)
    {
        var shown = Regex.Match(page, $"<li class=\"intent-task\" data-task=\"{task}\"[^>]*>(.*?)</li>", RegexOptions.Singleline).Groups[1].Value;
        return [.. Regex.Matches(shown, "<div class=\"task-question\" data-question=\"(\\d+)\"([^>]*)>(.*?)</div>", RegexOptions.Singleline)
            .Select(question => new ShownTaskQuestion(
                int.Parse(question.Groups[1].Value),
                question.Groups[2].Value.Contains("data-open"),
                ElementText(question.Groups[3].Value, "task-question-text"),
                question.Groups[3].Value.Contains("class=\"task-question-answer\"")
                    ? ElementText(question.Groups[3].Value, "task-question-answer")
                    : null))];
    }

    /// <summary>A question of a Task's agent as the detail page shows it.</summary>
    /// <param name="Open">Whether it awaits the answer, with a form to give it.</param>
    internal sealed record ShownTaskQuestion(int Number, bool Open, string Text, string? Answer);
}
