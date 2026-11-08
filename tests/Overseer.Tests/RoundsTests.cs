using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Overseer.Sandboxes;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;

namespace Overseer.Tests;

/// <summary>Rounds: the Analyst hands in numbered questions with its suggested answers, and the human answers them on the detail page.</summary>
public class RoundsTests(PostgresServer postgres)
{
    [Fact]
    public async Task A_turn_ending_with_a_valid_Round_shows_it_numbered_with_its_numbered_questions_and_suggestions_and_the_light_turns_orange()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(
            HandsInRound(("Which format should the export have?", "CSV, which every spreadsheet opens."), ("Which page offers it?", "The orders page."))));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var page = await AwaitingAnswersAsync(overseer, address);
        Assert.Equal(
            [
                new ShownRound(1, Open: true,
                [
                    new ShownQuestion(1, "Which format should the export have?", "CSV, which every spreadsheet opens.", Answer: null, AnalystsChoice: false),
                    new ShownQuestion(2, "Which page offers it?", "The orders page.", Answer: null, AnalystsChoice: false),
                ]),
            ],
            Rounds(page));
        Assert.Equal(("Orange", "Needs Attention"), LightOn(page));
        Assert.Equal("The Analyst asks you the questions of Round 1 below.", ElementText(page, "refinement-status"));
        Assert.Matches("<span class=\"intent-light\" data-light=\"Orange\">\\s*Needs Attention\\s*</span>", await GetAsync(overseer, "/"));
    }

    [Fact]
    public async Task The_Analysts_instructions_describe_how_to_hand_in_a_Round_in_a_format_Overseer_accepts()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");

        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Export data"));

        var instructions = (await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.SingleOrDefault()), "the Analyst's first message")).Message;
        Assert.Contains($"To hand in a Round, write it as JSON to {RoundPath}", instructions);
        Assert.Contains("then end your turn", instructions);
        Assert.Contains("\"suggestedAnswer\"", instructions);
        Assert.Contains("Analyst's Choice", instructions);
        // The example it is given is itself a valid Round.
        var example = Regex.Match(instructions, @"^\{\n.*?^\}$", RegexOptions.Singleline | RegexOptions.Multiline).Value;
        var round = JsonDocument.Parse(example).RootElement.GetProperty("questions")[0];
        Assert.False(string.IsNullOrWhiteSpace(round.GetProperty("question").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(round.GetProperty("suggestedAnswer").GetString()));
    }

    [Fact]
    public async Task Answers_given_as_free_text_as_suggested_or_blank_go_to_the_same_Analyst_session_with_each_Analysts_Choice_marked_and_the_light_turns_green()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(
            HandsInRound(("Which format?", "CSV."), ("Which page offers it?", "The orders page."), ("Up to how many rows?", "All of them.")),
            FakeAgentTurn.Endless("Reading the answers")));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingAnswersAsync(overseer, address);

        using var answered = await AnswerAsync(overseer, address, 1, ("1", "Excel, as\r\nthe finance team asks."), ("2", AsSuggested), ("3", ""));

        Assert.Equal(address, answered.RequestMessage!.RequestUri!.AbsolutePath);
        var sent = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.ElementAtOrDefault(1)), "the answers to reach the Analyst");
        var session = Assert.Single(overseer.Sandboxes.AgentSessionsStarted);
        Assert.Equal((AgentRole.Analyst, session.Sandbox), (sent.Role, sent.Sandbox));
        Assert.Contains("Round 1", sent.Message);
        Assert.Contains("Question 1: Which format?\nThe human's answer: Excel, as\nthe finance team asks.", sent.Message);
        Assert.Contains("Question 2: Which page offers it?\nAnalyst's Choice: the human left this to you, so your suggested answer stands: The orders page.", sent.Message);
        Assert.Contains("Question 3: Up to how many rows?\nAnalyst's Choice: the human left this to you, so your suggested answer stands: All of them.", sent.Message);
        var page = await EventuallyAsync(async () => await GetAsync(overseer, address) is var shown && shown.Contains("data-phase=\"AnalystWorking\"") ? shown : null, "the Analyst to work again");
        Assert.Equal(("Green", "Working"), LightOn(page));
        Assert.Equal(
            [
                new ShownRound(1, Open: false,
                [
                    new ShownQuestion(1, "Which format?", "CSV.", "Excel, as\nthe finance team asks.", AnalystsChoice: false),
                    new ShownQuestion(2, "Which page offers it?", "The orders page.", "The orders page.", AnalystsChoice: true),
                    new ShownQuestion(3, "Up to how many rows?", "All of them.", "All of them.", AnalystsChoice: true),
                ]),
            ],
            Rounds(page));
    }

    [Fact]
    public async Task A_question_given_both_an_own_answer_and_as_suggested_refuses_the_answers_and_keeps_what_was_typed()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(
            HandsInRound(("Which format?", "CSV."), ("Which page offers it?", "The orders page."))));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingAnswersAsync(overseer, address);

        using var answered = await AnswerAsync(overseer, address, 1, ("1", "Excel & <PDF>."), ("1", AsSuggested), ("2", "The reports page."));

        var page = await answered.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            "Your answers were not sent: Question 1 has an answer of your own and As suggested ticked. "
            + "Untick As suggested to send your answer, or clear your answer to accept the suggestion.",
            Alert(page));
        Assert.Equal([("1", "Excel & <PDF>.", true), ("2", "The reports page.", false)], AnswerFormOn(page));
        Assert.Equal([new ShownRound(1, Open: true,
            [
                new ShownQuestion(1, "Which format?", "CSV.", Answer: null, AnalystsChoice: false),
                new ShownQuestion(2, "Which page offers it?", "The orders page.", Answer: null, AnalystsChoice: false),
            ])], Rounds(await GetAsync(overseer, address)));
        Assert.Equal(("Orange", "Needs Attention"), LightOn(page));
        Assert.Single(overseer.Sandboxes.MessagesSent);
    }

    [Fact]
    public async Task Answers_to_a_Round_that_is_already_answered_or_isnt_the_open_one_are_refused_and_reach_no_Analyst()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(
            HandsInRound(("Which format?", "CSV.")),
            HandsInRound(("Which page offers it?", "The orders page."))));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingAnswersAsync(overseer, address);
        // A second tab, opened on Round 1 before it was answered.
        var otherTab = await overseer.OpenAsync(address);
        using var answered = await AnswerAsync(overseer, address, 1, ("1", "Excel."));
        await EventuallyAsync(async () => Rounds(await GetAsync(overseer, address)) is [_, { Open: true }] ? "" : null, "Round 2");

        using var again = await otherTab.SubmitFormAsync("answer-round", [new("Round", "1"), new("Answer.1", "PDF.")]);
        using var ahead = await AnswerAsync(overseer, address, 3, ("1", "PDF."));

        Assert.Equal("Your answers were not sent: Round 1 is already answered.", Alert(await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Equal("Your answers were not sent: The Analyst asked no Round 3.", Alert(await ahead.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        var page = await GetAsync(overseer, address);
        Assert.Equal(
            [
                new ShownRound(1, Open: false, [new ShownQuestion(1, "Which format?", "CSV.", "Excel.", AnalystsChoice: false)]),
                new ShownRound(2, Open: true, [new ShownQuestion(1, "Which page offers it?", "The orders page.", Answer: null, AnalystsChoice: false)]),
            ],
            Rounds(page));
        Assert.Equal(2, overseer.Sandboxes.MessagesSent.Count);
    }

    [Fact]
    public async Task The_transcript_is_kept_in_PostgreSQL_and_after_a_restart_the_open_Round_is_answered_once_the_Analyst_has_caught_up()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            first.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(
                HandsInRound(("Which format?", "CSV."), ("Which page offers it?", "The orders page.")),
                HandsInRound(("Up to how many rows?", "All of them."))));
            using var saved = await SaveAsync(first, "Export data", "acme/web");
            address = await AddressAsync(first, "Export data");
            using var started = await StartAsync(first, address);
            await AwaitingAnswersAsync(first, address);
            using var answered = await AnswerAsync(first, address, 1, ("1", "Excel."));
            await EventuallyAsync(async () => Rounds(await GetAsync(first, address)) is [_, { Open: true }] ? "" : null, "Round 2");
        }

        var catchingUp = new TaskCompletionSource();

        // The resumed Analyst catches up on the transcript, then goes on from the answers with its next Round.
        await using var second = await StartWithRepositoriesAsync(connectionString, [], sandboxes: sandboxes => sandboxes.ScriptAgentSessions(AgentRole.Analyst,
            new FakeAgentSession(
                new FakeAgentTurn([], new AgentOutcome.Done()) { EndsAfter = catchingUp.Task },
                HandsInRound(("Which columns?", "All of them.")),
                FakeAgentTurn.Endless())));

        var catchingUpPage = await EventuallyAsync(async () => await GetAsync(second, address) is var shown && shown.Contains("data-phase=\"AnalystWorking\"") ? shown : null,
            "the Analyst to catch up");
        Assert.Equal("Not answered yet: you can answer it once the Analyst is back.", ElementText(catchingUpPage, "question-unanswered"));
        catchingUp.SetResult();
        var page = await AwaitingAnswersAsync(second, address);
        Assert.Equal(
            [
                new ShownRound(1, Open: false,
                [
                    new ShownQuestion(1, "Which format?", "CSV.", "Excel.", AnalystsChoice: false),
                    new ShownQuestion(2, "Which page offers it?", "The orders page.", "The orders page.", AnalystsChoice: true),
                ]),
                new ShownRound(2, Open: true, [new ShownQuestion(1, "Up to how many rows?", "All of them.", Answer: null, AnalystsChoice: false)]),
            ],
            Rounds(page));
        Assert.Equal(("Orange", "Needs Attention"), LightOn(page));
        Assert.Equal("The Analyst asks you the questions of Round 2 below.", ElementText(page, "refinement-status"));

        using var answeredAfterTheRestart = await AnswerAsync(second, address, 2, ("1", "Ten thousand."));

        var sent = await EventuallyAsync(() => Task.FromResult(second.Sandboxes.MessagesSent.ElementAtOrDefault(1)), "the answers to reach the Analyst");
        Assert.Equal(Assert.Single(second.Sandboxes.AgentSessionsStarted).Sandbox, sent.Sandbox);
        Assert.Contains("The human answered Round 2.\n\nQuestion 1: Up to how many rows?\nThe human's answer: Ten thousand.", sent.Message);
        var continued = await EventuallyAsync(async () => Rounds(await GetAsync(second, address)) is [_, _, { Open: true } next] ? next : null, "Round 3");
        Assert.Equal(new ShownRound(3, Open: true, [new ShownQuestion(1, "Which columns?", "All of them.", Answer: null, AnalystsChoice: false)]), continued);
    }

    [Fact]
    public async Task Of_two_submissions_of_the_same_Round_at_once_only_one_is_taken()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInRound(("Which format?", "CSV.")), FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingAnswersAsync(overseer, address);
        var first = await overseer.OpenAsync(address);
        var second = await overseer.OpenAsync(address);

        var submitted = await Task.WhenAll(
            first.SubmitFormAsync("answer-round", [new("Round", "1"), new("Answer.1", "Excel.")]),
            second.SubmitFormAsync("answer-round", [new("Round", "1"), new("Answer.1", "PDF.")]));

        var pages = await Task.WhenAll(submitted.Select(response => response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Single(pages, page => page.Contains("Your answers were not sent: Round 1 is already answered."));
        var answer = Assert.Single(Rounds(await GetAsync(overseer, address))).Questions.Single().Answer;
        await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.Count == 2 ? "" : null), "the answers to reach the Analyst");
        Assert.Contains($"The human's answer: {answer}", overseer.Sandboxes.MessagesSent[1].Message);
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        Assert.Equal(2, overseer.Sandboxes.MessagesSent.Count);
    }

    [Fact]
    public async Task An_invalid_Round_is_never_shown_and_the_Analyst_gets_what_is_wrong_and_its_next_turn_is_handled_as_usual()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(
            HandsIn("""{ "questions": [ { "question": "Which format?" }, { "question": " ", "suggestedAnswer": "The orders page." } ] }"""),
            HandsInRound(("Which format?", "CSV."))));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var page = await AwaitingAnswersAsync(overseer, address);
        Assert.Equal([new ShownRound(1, Open: true, [new ShownQuestion(1, "Which format?", "CSV.", Answer: null, AnalystsChoice: false)])], Rounds(page));
        Assert.Equal(2, overseer.Sandboxes.MessagesSent.Count);
        var rejection = overseer.Sandboxes.MessagesSent[1].Message;
        Assert.Contains($"The Round you wrote to {RoundPath} is not valid, so the human hasn't seen it:", rejection);
        Assert.Contains("- Question 1 has no \"suggestedAnswer\": it must be text that isn't empty.\n- Question 2 has no \"question\": it must be text that isn't empty.", rejection);
    }

    [Theory]
    [InlineData("not json", "The file is not valid JSON:")]
    [InlineData("""{ "questions": [] }""", "A Round needs at least one question.")]
    [InlineData("""[ { "question": "Which format?", "suggestedAnswer": "CSV." } ]""", "The file must hold a JSON object whose \"questions\" is a list of questions.")]
    [InlineData("""{ "questions": [ "Which format?" ] }""", "Question 1 must be an object with \"question\" and \"suggestedAnswer\".")]
    public async Task A_Round_that_isnt_a_list_of_questions_with_suggested_answers_is_invalid(string round, string error)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsIn(round), FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var rejection = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.ElementAtOrDefault(1)), "the Analyst to be told");
        Assert.Contains($"- {error}", rejection.Message);
        Assert.Empty(Rounds(await GetAsync(overseer, address)));
    }

    [Fact]
    public async Task A_turn_ending_without_a_Round_tells_the_Analyst_to_hand_one_in_or_the_Spec()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(
            new FakeAgentTurn([], new AgentOutcome.AskedQuestion("Which format?")),
            HandsInRound(("Which format?", "CSV."))));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        await AwaitingAnswersAsync(overseer, address);
        Assert.Equal(
            $"You asked \"Which format?\" outside a Round, so the human hasn't seen it. Put your questions in a Round, written to {RoundPath}, "
            + $"or, once you have enough to write it, hand in the Spec at {SpecTests.SpecPath}, in the format of your instructions, then end your turn.",
            overseer.Sandboxes.MessagesSent[1].Message);
    }

    // Each attempt costs a turn of the Analyst's model; an Analyst that never gets it right would go on for good.
    [Theory]
    [InlineData("done", "The Analyst's session failed: it handed in no valid Round or Spec in 3 turns in a row. The last time, it ended its turn without handing in a Round or a Spec.")]
    [InlineData("asked", "The Analyst's session failed: it handed in no valid Round or Spec in 3 turns in a row. The last time, it asked a question outside a Round: Which format?")]
    [InlineData("invalid", "The Analyst's session failed: it handed in no valid Round or Spec in 3 turns in a row. The last time, its Round was not valid: A Round needs at least one question.")]
    [InlineData("invalid Spec", "The Analyst's session failed: it handed in no valid Round or Spec in 3 turns in a row. The last time, its Spec was not valid: The Spec has no Tasks: \"tasks\" must be a list of at least one Task.")]
    public async Task Three_turns_in_a_row_without_a_valid_Round_or_Spec_fail_the_Refinement_which_can_be_retried(string turn, string error)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(turn switch
        {
            "invalid" => HandsIn("""{ "questions": [] }"""),
            "invalid Spec" => SpecTests.HandsInSpec(SpecTests.SpecWithout("tasks")),
            "asked" => new FakeAgentTurn([], new AgentOutcome.AskedQuestion("Which format?")),
            _ => new FakeAgentTurn([], new AgentOutcome.Done()),
        }));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var page = await EventuallyAsync(async () => await GetAsync(overseer, address) is var shown && shown.Contains("data-phase=\"Failed\"") ? shown : null, "the Refinement to fail");
        Assert.Equal(error, ElementText(page, "refinement-status"));
        Assert.Equal(("Red", "Failed"), LightOn(page));
        Assert.Contains($"action=\"intents/{address["/intents/".Length..]}/retry\"", page);
        Assert.Equal(3, overseer.Sandboxes.MessagesSent.Count);
        await EventuallyAsync(async () => (await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken)).Count == 0 ? "" : null, "the Sandbox to be destroyed");
    }

    /// <summary>What <see cref="AnswerAsync"/> takes for a question answered "as suggested".</summary>
    internal const string AsSuggested = "as suggested";

    /// <summary>
    /// Answers a Round on the Intent's detail page as a browser would send the form: each question's own answer, or "as suggested"
    /// ticked, by question number.
    /// </summary>
    internal static Task<HttpResponseMessage> AnswerAsync(RunningOverseer overseer, string address, int round, params (string Question, string Answer)[] answers) =>
        overseer.SubmitFormAsync(address, "answer-round",
        [
            new("Round", round.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            .. answers.Select(answer => answer.Answer == AsSuggested
                ? KeyValuePair.Create("AsSuggested", answer.Question)
                : KeyValuePair.Create($"Answer.{answer.Question}", answer.Answer)),
        ]);

    /// <summary>The fixed path in its Sandbox at which the Analyst hands in a Round, as its instructions say.</summary>
    internal const string RoundPath = "/home/overseer/.overseer/round.json";

    /// <summary>A turn in which the Analyst hands in a Round of these questions, each with its suggested answer.</summary>
    internal static FakeAgentTurn HandsInRound(params (string Question, string SuggestedAnswer)[] questions) =>
        HandsIn(JsonSerializer.Serialize(new
        {
            questions = questions.Select(question => new { question = question.Question, suggestedAnswer = question.SuggestedAnswer }),
        }));

    /// <summary>A turn in which the Analyst writes this as its Round.</summary>
    internal static FakeAgentTurn HandsIn(string round) =>
        new([], new AgentOutcome.Done()) { Writes = new Dictionary<string, string> { [RoundPath] = round } };

    /// <summary>
    /// Waits for the Intent's detail page to show its Refinement awaiting answers, and its light orange with it, and gives the page. The
    /// page reads the Intent, with its light, a moment before its Refinement, so a page read as the Round comes may show the light of
    /// the moment before.
    /// </summary>
    internal static Task<string> AwaitingAnswersAsync(RunningOverseer overseer, string address) =>
        EventuallyAsync(async () => await GetAsync(overseer, address) is var page && page.Contains("data-phase=\"AwaitingAnswers\"")
            && LightOn(page) is ("Orange", _) ? page : null,
            "the Refinement to await answers");

    /// <summary>
    /// The answer form of the open Round as the page fills it in: per question, by number, the answer in its text box and whether
    /// As suggested is ticked.
    /// </summary>
    internal static IReadOnlyList<(string Question, string Answer, bool AsSuggested)> AnswerFormOn(string page) =>
        [.. Regex.Matches(Regex.Match(page, "<form[^>]*class=\"round-form\".*?</form>", RegexOptions.Singleline).Value,
            "<textarea[^>]*name=\"Answer\\.(\\d+)\"[^>]*>(.*?)</textarea>.*?(<input[^>]*name=\"AsSuggested\"[^>]*>)", RegexOptions.Singleline)
            .Select(question => (
                question.Groups[1].Value,
                WebUtility.HtmlDecode(question.Groups[2].Value),
                Regex.IsMatch(question.Groups[3].Value, @"\schecked\b")))];

    /// <summary>The Rounds the detail page shows, in order.</summary>
    internal static IReadOnlyList<ShownRound> Rounds(string page) =>
        [.. Regex.Matches(page, "<article class=\"round\" data-round=\"(\\d+)\"([^>]*)>(.*?)</article>", RegexOptions.Singleline).Select(round => new ShownRound(
            int.Parse(round.Groups[1].Value),
            round.Groups[2].Value.Contains("data-open"),
            [.. Regex.Matches(round.Groups[3].Value, "<li class=\"round-question\" data-question=\"(\\d+)\"[^>]*>(.*?)</li>", RegexOptions.Singleline).Select(question =>
            {
                var answer = Regex.Match(question.Groups[2].Value, "<p class=\"question-answer\"([^>]*)>(.*?)</p>", RegexOptions.Singleline);
                return new ShownQuestion(
                    int.Parse(question.Groups[1].Value),
                    ElementText(question.Groups[2].Value, "question-text"),
                    ElementText(question.Groups[2].Value, "question-suggestion"),
                    answer.Success ? WebUtility.HtmlDecode(answer.Groups[2].Value).Trim() : null,
                    answer.Success && answer.Groups[1].Value.Contains("data-analysts-choice"));
            })]))];

    /// <summary>A Round as the detail page shows it.</summary>
    /// <param name="Open">Whether it awaits answers, with a form to give them.</param>
    internal sealed record ShownRound(int Number, bool Open, IReadOnlyList<ShownQuestion> Questions)
    {
        public bool Equals(ShownRound? other) =>
            other is not null && (Number, Open) == (other.Number, other.Open) && Questions.SequenceEqual(other.Questions);

        public override int GetHashCode() => HashCode.Combine(Number, Open);

        public override string ToString() => $"Round {Number}{(Open ? " (open)" : "")}: {string.Join("; ", Questions)}";
    }

    /// <summary>A question of a Round as the detail page shows it.</summary>
    /// <param name="Answer">The answer recorded for it; <c>null</c> while it has none.</param>
    /// <param name="AnalystsChoice">Whether that answer is the Analyst's Choice.</param>
    internal sealed record ShownQuestion(int Number, string Text, string SuggestedAnswer, string? Answer, bool AnalystsChoice);
}
