using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using Overseer.Sandboxes;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;
using static Overseer.Tests.RoundsTests;

namespace Overseer.Tests;

/// <summary>
/// An idle Refinement pauses: a Round left unanswered for longer than the Refinement idle timeout moves the Intent to Paused, which
/// frees its Sandbox and slot, and Resume continues the Refinement in a fresh Sandbox from its transcript.
/// </summary>
public class IdleRefinementTests(PostgresServer postgres)
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_Round_left_unanswered_for_longer_than_the_idle_timeout_pauses_the_Intent_stops_the_Analyst_and_destroys_its_Sandbox()
    {
        var time = new FakeTimeProvider(Morning);
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInRound(("Which format?", "CSV."))));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingAnswersAsync(overseer, address);
        var openedBeforeThePause = await overseer.OpenAsync(address);

        time.Advance(TimeSpan.FromMinutes(29));
        await NothingHappensAsync();
        Assert.Contains("data-phase=\"AwaitingAnswers\"", await GetAsync(overseer, address));
        time.Advance(TimeSpan.FromMinutes(2));

        var page = await PausedAsync(overseer, address);
        Assert.Null(LightOn(page));
        Assert.Equal("Paused", ElementText(await GetAsync(overseer, "/"), "intent-state"));
        Assert.Null(LightOn(await GetAsync(overseer, "/")));
        await RefinementTests.NoSandboxLeftAsync(overseer);
        Assert.Equal([AgentRole.Analyst], overseer.Sandboxes.AgentSessionsStopped.Select(session => session.Role));
        Assert.Equal([new ShownRound(1, Open: false, [new ShownQuestion(1, "Which format?", "CSV.", Answer: null, AnalystsChoice: false)])], Rounds(page));
        Assert.Equal("Not answered yet: Resume the Intent to answer it.", ElementText(page, "question-unanswered"));
        using var late = await openedBeforeThePause.SubmitFormAsync("answer-round", [new("Round", "1"), new("Answer.1", "Excel.")]);
        Assert.Equal(
            "Your answers were not sent: This Intent is Paused. Resume it to answer Round 1.",
            Alert(await late.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Only_the_time_a_Round_waits_for_answers_counts_as_idle_not_the_time_the_Analyst_works_before_it()
    {
        var time = new FakeTimeProvider(Morning);
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        var working = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(
            HandsInRound(("Which format?", "CSV.")) with { EndsAfter = working.Task },
            HandsInRound(("Which page offers it?", "The orders page.")) with { EndsAfter = working.Task }));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.SingleOrDefault()), "the Analyst's first message");

        time.Advance(TimeSpan.FromMinutes(45));
        working.SetResult();
        await AwaitingAnswersAsync(overseer, address);
        time.Advance(TimeSpan.FromMinutes(20));
        using var answered = await AnswerAsync(overseer, address, 1, ("1", "Excel."));
        await EventuallyAsync(async () => Rounds(await GetAsync(overseer, address)) is [_, { Open: true }] ? "" : null, "Round 2");
        time.Advance(TimeSpan.FromMinutes(25));
        await NothingHappensAsync();

        Assert.Contains("data-phase=\"AwaitingAnswers\"", await GetAsync(overseer, address));
        time.Advance(TimeSpan.FromMinutes(6));
        await PausedAsync(overseer, address);
    }

    [Fact]
    public async Task Lowering_the_idle_timeout_below_the_time_a_Round_has_waited_pauses_the_Intent_at_once()
    {
        var time = new FakeTimeProvider(Morning);
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInRound(("Which format?", "CSV."))));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingAnswersAsync(overseer, address);
        time.Advance(TimeSpan.FromMinutes(15));

        await SetRefinementIdleTimeoutAsync(overseer, TimeSpan.FromMinutes(10));

        await PausedAsync(overseer, address);
    }

    [Fact]
    public async Task Raising_the_idle_timeout_lets_a_Round_already_waiting_wait_until_the_new_timeout()
    {
        var time = new FakeTimeProvider(Morning);
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInRound(("Which format?", "CSV."))));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingAnswersAsync(overseer, address);
        time.Advance(TimeSpan.FromMinutes(15));

        await SetRefinementIdleTimeoutAsync(overseer, TimeSpan.FromMinutes(60));
        time.Advance(TimeSpan.FromMinutes(30));
        await NothingHappensAsync();

        Assert.Contains("data-phase=\"AwaitingAnswers\"", await GetAsync(overseer, address));
        time.Advance(TimeSpan.FromMinutes(16));
        await PausedAsync(overseer, address);
    }

    [Fact]
    public async Task A_Paused_Intent_takes_no_slot_shows_no_light_and_its_Working_Time_stops()
    {
        var time = new FakeTimeProvider(Morning);
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        await SetMaxActiveIntentsAsync(overseer, 1);
        var address = await PausedAfterOneRoundAsync(overseer, time, "Export data");
        using var other = await SaveAsync(overseer, "Import data", "acme/web");
        var otherAddress = await AddressAsync(overseer, "Import data");

        time.Advance(TimeSpan.FromHours(3));
        using var started = await StartAsync(overseer, otherAddress);

        Assert.Equal("Refining", ElementText(await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), "intent-state"));
        var page = await GetAsync(overseer, address);
        Assert.Null(LightOn(page));
        // Refining until the Round had waited 31 minutes.
        Assert.Equal("31 min", ElementText(page, "intent-working-time"));
    }

    [Fact]
    public async Task A_Paused_Intent_offers_Resume_which_is_disabled_with_the_same_explanation_as_Start_while_every_slot_is_taken()
    {
        var time = new FakeTimeProvider(Morning);
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        await SetMaxActiveIntentsAsync(overseer, 1);
        var address = await PausedAfterOneRoundAsync(overseer, time, "Export data");
        var paused = await GetAsync(overseer, address);
        Assert.False(ResumeIsDisabled(paused));
        Assert.DoesNotContain("role=\"alert\"", paused);
        var openedWithASlotFree = await overseer.OpenAsync(address);
        using var other = await SaveAsync(overseer, "Import data", "acme/web");
        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Import data"));

        var page = await GetAsync(overseer, address);
        using var refused = await openedWithASlotFree.SubmitFormAsync("resume-intent", []);

        const string allSlotsTaken = "All Active Intent slots are taken, by \"Import data\". You can raise the maximum in Settings.";
        Assert.True(ResumeIsDisabled(page));
        Assert.Equal(allSlotsTaken, Alert(page));
        var refusal = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(allSlotsTaken, Alert(refusal));
        Assert.Equal("Paused", ElementText(refusal, "intent-state"));
        Assert.Equal("Paused", ElementText(await GetAsync(overseer, address), "intent-state"));
    }

    [Fact]
    public async Task Resume_continues_the_Refinement_in_a_fresh_Sandbox_whose_Analyst_gets_the_whole_transcript_and_the_open_Round_stays_answerable()
    {
        var time = new FakeTimeProvider(Morning);
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst,
            new FakeAgentSession(
                HandsInRound(("Which format?", "CSV."), ("Which page offers it?", "The orders page.")),
                HandsInRound(("Up to how many rows?", "All of them."))),
            // Catches up on the transcript, then works on the answers.
            new FakeAgentSession(new FakeAgentTurn(["Reading the code"], new AgentOutcome.Done()), FakeAgentTurn.Endless("Reading the answers")));
        using var saved = await SaveAsync(overseer, "# Export data\n\nAs CSV.", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingAnswersAsync(overseer, address);
        using var answered = await AnswerAsync(overseer, address, 1, ("1", "Excel."), ("2", AsSuggested));
        await EventuallyAsync(async () => Rounds(await GetAsync(overseer, address)) is [_, { Open: true }] ? "" : null, "Round 2");
        time.Advance(TimeSpan.FromMinutes(31));
        await PausedAsync(overseer, address);
        var first = Assert.Single(overseer.Sandboxes.AgentSessionsStarted).Sandbox;

        using var resumed = await ResumeAsync(overseer, address);

        Assert.Equal(address, resumed.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal("Refining", ElementText(await resumed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), "intent-state"));
        var page = await AwaitingAnswersAsync(overseer, address);
        var second = overseer.Sandboxes.AgentSessionsStarted[1];
        Assert.Equal(AgentRole.Analyst, second.Role);
        Assert.NotEqual(first, second.Sandbox);
        Assert.Equal([second.Sandbox], await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        var transcript = overseer.Sandboxes.MessagesSent.First(message => message.Sandbox == second.Sandbox).Message;
        Assert.Contains("# Export data\n\nAs CSV.", transcript);
        Assert.Contains(
            "Round 1, answered:\n\n"
            + "Question 1: Which format?\nThe human's answer: Excel.\n\n"
            + "Question 2: Which page offers it?\nAnalyst's Choice: the human left this to you, so your suggested answer stands: The orders page.\n\n"
            + "Round 2, still open:\n\n"
            + "Question 1: Up to how many rows?\nYour suggested answer: All of them.",
            transcript);
        Assert.Contains("Round 2 is still open: the human hasn't answered it yet", transcript);
        Assert.Equal(("Orange", "Needs Attention"), LightOn(page));
        Assert.Equal([1, 2], Rounds(page).Select(round => round.Number));
        Assert.True(Rounds(page)[1].Open);

        using var answeredAfterResuming = await AnswerAsync(overseer, address, 2, ("1", "Ten thousand."));

        var answers = await EventuallyAsync(
            () => Task.FromResult(overseer.Sandboxes.MessagesSent.Where(message => message.Sandbox == second.Sandbox).ElementAtOrDefault(1)),
            "the answers to reach the resumed Analyst");
        Assert.Contains("The human answered Round 2.\n\nQuestion 1: Up to how many rows?\nThe human's answer: Ten thousand.", answers.Message);
        Assert.Equal(("Green", "Working"), LightOn(await GetAsync(overseer, address)));
    }

    /// <summary>
    /// Captures a Draft with this text and <c>acme/web</c> in scope, starts it, has the Analyst ask a Round and leaves it unanswered for
    /// 31 minutes, so that the Intent is Paused; returns the address of its detail page.
    /// </summary>
    private static async Task<string> PausedAfterOneRoundAsync(RunningOverseer overseer, FakeTimeProvider time, string text)
    {
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInRound(("Which format?", "CSV."))));
        using var saved = await SaveAsync(overseer, text, "acme/web");
        var address = await AddressAsync(overseer, text);
        using var started = await StartAsync(overseer, address);
        await AwaitingAnswersAsync(overseer, address);
        time.Advance(TimeSpan.FromMinutes(31));
        await PausedAsync(overseer, address);
        AnalystKeepsWorking(overseer);
        return address;
    }

    /// <summary>Opens the Intent's detail page and presses Resume, whether or not the page offers it.</summary>
    private static Task<HttpResponseMessage> ResumeAsync(RunningOverseer overseer, string address) =>
        overseer.SubmitFormAsync(address, "resume-intent", []);

    /// <summary>Whether the Resume button on the page is disabled; the page must have one.</summary>
    private static bool ResumeIsDisabled(string page) => Regex.Match(page, @"<button[^>]*>\s*Resume\s*</button>") is { Success: true } button
        ? Regex.IsMatch(button.Value, @"\sdisabled\b")
        : throw new InvalidOperationException($"The page has no Resume button: {page}");

    /// <summary>Gives what isn't announced the time to have happened, before checking that it didn't.</summary>
    private static Task NothingHappensAsync() => Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

    /// <summary>Waits for the Intent's detail page to show it Paused, and gives the page.</summary>
    internal static Task<string> PausedAsync(RunningOverseer overseer, string address) =>
        EventuallyAsync(async () => await GetAsync(overseer, address) is var page && ElementText(page, "intent-state") == "Paused" ? page : null,
            "the Intent to be Paused");
}
