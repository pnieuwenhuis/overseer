using System.Net;
using System.Text.RegularExpressions;
using Overseer.Sandboxes;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;
using static Overseer.Tests.RoundsTests;
using static Overseer.Tests.SpecTests;

namespace Overseer.Tests;

/// <summary>
/// The user sends a Spec that isn't right back to Refinement with comments: the Intent is Refining again, in a fresh Sandbox, and
/// the Analyst's next Spec is a new version.
/// </summary>
public class SendBackTests(PostgresServer postgres)
{
    [Fact]
    public async Task Sending_the_Spec_back_resumes_the_Refinement_in_a_fresh_Sandbox_whose_Analyst_gets_the_transcript_the_latest_Spec_and_the_comments_and_its_next_Spec_is_a_new_version()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var revised = Spec();
        revised["summary"] = "Orders can be exported as **CSV** and **Excel**.";
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst,
            new FakeAgentSession(HandsInRound(("Which format?", "CSV.")), HandsInSpec(Spec())),
            // Asks a further Round before it hands in the Spec again.
            new FakeAgentSession(HandsInRound(("Which Excel version?", "The current one.")), HandsInSpec(revised)));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingAnswersAsync(overseer, address);
        using var answered = await AnswerAsync(overseer, address, 1, ("1", "Both."));
        await AwaitingSpecApprovalAsync(overseer, address);
        var form = await SpecForm.OpenAsync(overseer, address);
        form.Set("Summary", "Orders can be exported as **CSV**, by the user.");
        await form.SubmitAsync("Save");
        var first = Assert.Single(overseer.Sandboxes.AgentSessionsStarted).Sandbox;

        using var sentBack = await SendBackAsync(overseer, address, 2, "Also cover *Excel*.\nKeep the CSV.");

        Assert.Equal(address, sentBack.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal("Refining", ElementText(await sentBack.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), "intent-state"));
        var asking = await AwaitingAnswersAsync(overseer, address);
        var second = overseer.Sandboxes.AgentSessionsStarted[1];
        Assert.Equal(AgentRole.Analyst, second.Role);
        Assert.NotEqual(first, second.Sandbox);
        Assert.Equal([second.Sandbox], await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        var message = overseer.Sandboxes.MessagesSent.First(sent => sent.Sandbox == second.Sandbox).Message;
        Assert.Contains("Question 1: Which format?\nThe human's answer: Both.", message);
        // The version the human edited, not the Analyst's.
        Assert.Contains("Orders can be exported as **CSV**, by the user.", message);
        Assert.DoesNotContain("Orders can be exported as **CSV**.\n", message);
        Assert.Contains("Spec version 2", message);
        Assert.Contains("Also cover *Excel*.\nKeep the CSV.", message);
        Assert.Equal(("Orange", "Needs Attention"), LightOn(asking));
        // Between the Spec version the comments were about and what followed.
        Assert.Equal(["Round 1", "Comments on version 2", "Round 2"], TranscriptOn(asking));
        Assert.Equal([("2", "1", "Also cover *Excel*.\nKeep the CSV.")], CommentsOn(asking));

        using var answeredAgain = await AnswerAsync(overseer, address, 2, ("1", AsSuggested));

        var page = await EventuallyAsync(
            async () => await GetAsync(overseer, address) is var current && ElementText(current, "intent-state") == "Awaiting Spec Approval" ? current : null,
            "the Intent to await Spec approval again");
        Assert.Equal(3, SpecVersionOn(page));
        Assert.Equal("Written by the Analyst", ElementText(page, "spec-written-by"));
        Assert.Equal("<p>Orders can be exported as <strong>CSV</strong> and <strong>Excel</strong>.</p>", SpecMarkdown(page, "Summary"));
        Assert.Equal(["Round 1", "Comments on version 2", "Round 2"], TranscriptOn(page));
        await RefinementTests.NoSandboxLeftAsync(overseer);
    }

    [Fact]
    public async Task The_Analyst_may_hand_in_a_new_Spec_at_once_and_the_comments_stay_in_the_transcript_of_an_Intent_without_Rounds()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var revised = Spec();
        revised["approach"] = "Stream the file.";
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst,
            new FakeAgentSession(HandsInSpec(Spec())), new FakeAgentSession(HandsInSpec(revised)));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingSpecApprovalAsync(overseer, address);

        using var sentBack = await SendBackAsync(overseer, address, 1, "Say how the file is streamed.");

        var page = await EventuallyAsync(
            async () => await GetAsync(overseer, address) is var current && SpecVersionOn(current) == 2 && ElementText(current, "intent-state") == "Awaiting Spec Approval" ? current : null,
            "the new Spec");
        Assert.Equal(["Comments on version 1"], TranscriptOn(page));
        Assert.Equal([("1", "0", "Say how the file is streamed.")], CommentsOn(page));
        Assert.Equal("<p>Stream the file.</p>", SpecMarkdown(page, "Approach"));
        Assert.Equal(("Orange", "Needs Attention"), LightOn(page));
    }

    [Fact]
    public async Task Send_back_takes_a_slot_so_it_is_disabled_with_the_explanation_of_Start_while_every_slot_is_taken_and_the_server_refuses_it_too()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await SetMaxActiveIntentsAsync(overseer, 1);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst,
            new FakeAgentSession(HandsInSpec(Spec())), new FakeAgentSession(FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        var awaiting = await AwaitingSpecApprovalAsync(overseer, address);
        // Awaiting approval holds no slot.
        Assert.False(SendBackIsDisabled(awaiting));
        Assert.DoesNotContain("role=\"alert\"", awaiting);
        var openedWithASlotFree = await overseer.OpenAsync(address);
        using var other = await SaveAsync(overseer, "Import data", "acme/web");
        using var otherStarted = await StartAsync(overseer, await AddressAsync(overseer, "Import data"));

        var page = await GetAsync(overseer, address);
        using var refused = await openedWithASlotFree.SubmitFormAsync("send-back-spec", [new("Comments", "Not right."), new("SpecVersion", "1")]);

        const string allSlotsTaken = "All Active Intent slots are taken, by \"Import data\". You can raise the maximum in Settings.";
        Assert.True(SendBackIsDisabled(page));
        Assert.Equal(allSlotsTaken, Alert(page));
        var refusal = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(allSlotsTaken, Alert(refusal));
        Assert.Equal("Awaiting Spec Approval", ElementText(refusal, "intent-state"));
        var after = await GetAsync(overseer, address);
        Assert.Equal("Awaiting Spec Approval", ElementText(after, "intent-state"));
        // Nothing of the refused comments is kept.
        Assert.Empty(CommentsOn(after));
    }

    [Fact]
    public async Task Comments_are_required_and_must_be_about_the_latest_version_and_a_refusal_keeps_what_the_user_wrote()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(Spec())));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingSpecApprovalAsync(overseer, address);
        var form = await SpecForm.OpenAsync(overseer, address);
        var opened = await GetAsync(overseer, address);
        Assert.Contains("required", Regex.Match(opened, "<textarea[^>]*name=\"Comments\"[^>]*>").Value);

        using var blank = await SendBackAsync(overseer, address, 1, "  \n ");
        form.Set("Summary", "Changed.");
        await form.SubmitAsync("Save");
        using var outdated = await SendBackAsync(overseer, address, 1, "About the first version.");

        Assert.Equal(
            "Your comments were not sent: Write your comments: say what is not right about the Spec.",
            Alert(await blank.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        var refusal = await outdated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            "Your comments were not sent: The Spec has a newer version, 2, since you read version 1. Read it, and send it back with your comments if they still apply.",
            Alert(refusal));
        Assert.Equal("About the first version.", WebUtility.HtmlDecode(Regex.Match(refusal, "<textarea[^>]*name=\"Comments\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline).Groups[1].Value).Trim());
        var after = await GetAsync(overseer, address);
        Assert.Equal("Awaiting Spec Approval", ElementText(after, "intent-state"));
        Assert.Empty(CommentsOn(after));
    }

    [Fact]
    public async Task Send_back_is_neither_offered_nor_accepted_unless_the_Intent_awaits_Spec_approval()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        Assert.DoesNotContain("Send back", await GetAsync(overseer, address));

        using var refused = await SendBackAsync(overseer, address, 1, "Not right.");

        Assert.Equal(
            "This Intent is Draft, and only a Spec awaiting approval can be sent back.",
            Alert(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Equal("Draft", ElementText(await GetAsync(overseer, address), "intent-state"));
    }

    private static Task<HttpResponseMessage> SendBackAsync(RunningOverseer overseer, string address, int version, string comments) =>
        overseer.SubmitFormAsync(address, "send-back-spec", [new("Comments", comments), new("SpecVersion", version.ToString(System.Globalization.CultureInfo.InvariantCulture))]);

    /// <summary>Whether the Send back button on the page is disabled; the page must have one.</summary>
    private static bool SendBackIsDisabled(string page) => Regex.Match(page, @"<button[^>]*>\s*Send back\s*</button>") is { Success: true } button
        ? button.Value.Contains("disabled")
        : throw new InvalidOperationException($"The page has no Send back button: {page}");

    /// <summary>The Rounds and the user's comments the detail page shows, in order.</summary>
    private static IReadOnlyList<string> TranscriptOn(string page) =>
        [.. Regex.Matches(page, "<article class=\"(round|spec-comment)\" data-(?:round|spec-version)=\"(\\d+)\"").Select(entry =>
            entry.Groups[1].Value == "round" ? $"Round {entry.Groups[2].Value}" : $"Comments on version {entry.Groups[2].Value}")];

    /// <summary>The user's comments the detail page shows: the version each is about, the Round it follows (0 when none), and its text.</summary>
    private static IReadOnlyList<(string Version, string AfterRound, string Text)> CommentsOn(string page) =>
        [.. Regex.Matches(page, "<article class=\"spec-comment\" data-spec-version=\"(\\d+)\" data-after-round=\"(\\d+)\"[^>]*>(.*?)</article>", RegexOptions.Singleline).Select(comment => (
            comment.Groups[1].Value,
            comment.Groups[2].Value,
            ElementText(comment.Groups[3].Value, "spec-comment-text")))];
}
