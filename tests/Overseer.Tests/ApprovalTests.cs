using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Overseer.Sandboxes;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;
using static Overseer.Tests.SpecTests;

namespace Overseer.Tests;

/// <summary>
/// The user approves the Spec version they are looking at: it becomes the Approved Spec, and the Intent moves to Implementing when a
/// slot is free and approval started it, or to Approved, which takes no slot, otherwise.
/// </summary>
public class ApprovalTests(PostgresServer postgres)
{
    [Fact]
    public async Task With_a_slot_free_the_page_offers_Approve_and_start_which_locks_the_latest_Spec_and_moves_the_Intent_to_Implementing_with_a_green_light()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var address = await AwaitingApprovalOfAsync(overseer, 1);
        var edited = await SpecForm.OpenAsync(overseer, address);
        edited.Set("Summary", "Orders can be exported as **CSV**, by the user.");
        await edited.SubmitAsync("Save");
        var page = await GetAsync(overseer, address);
        Assert.Equal("Approve & start", ApproveButton(page));
        Assert.DoesNotContain("role=\"alert\"", page);

        using var approved = await ApproveAsync(overseer, address, 2, start: true);

        // Back on the detail page, so reloading it doesn't approve again.
        Assert.Equal(address, approved.FinalPath());
        var implementing = await approved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Implementing", ElementText(implementing, "intent-state"));
        Assert.Equal(("Green", "Working"), LightOn(implementing));
        // The Approved Spec, and which version it is.
        Assert.Equal(2, SpecVersionOn(implementing));
        Assert.Equal("Approved Spec, version 2", ElementText(implementing, "spec-approved"));
        Assert.Equal("<p>Orders can be exported as <strong>CSV</strong>, by the user.</p>", SpecMarkdown(implementing, "Summary"));
        Assert.Null(ApproveButton(implementing));
        Assert.DoesNotContain("Send back", implementing);
        Assert.DoesNotContain("/spec/edit", implementing);
        Assert.Equal("Implementing", ElementText(await GetAsync(overseer, "/"), "intent-state"));
    }

    // Story 42, for Approved.
    [Fact]
    public async Task With_every_slot_taken_the_page_explains_why_it_can_only_Approve_which_moves_the_Intent_to_Approved_with_no_light_and_no_slot()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await SetMaxActiveIntentsAsync(overseer, 1);
        var address = await AwaitingApprovalOfAsync(overseer, 1);
        using var other = await SaveAsync(overseer, "Import data", "acme/web");
        using var otherStarted = await StartAsync(overseer, await AddressAsync(overseer, "Import data"));

        var page = await GetAsync(overseer, address);

        Assert.Equal("Approve", ApproveButton(page));
        Assert.Contains("All Active Intent slots are taken, by &quot;Import data&quot;.", page);

        using var approved = await ApproveAsync(overseer, address, 1, start: false);

        var approvedPage = await approved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Approved", ElementText(approvedPage, "intent-state"));
        Assert.Null(LightOn(approvedPage));
        Assert.Equal("Approved Spec, version 1", ElementText(approvedPage, "spec-approved"));
        Assert.Null(ApproveButton(approvedPage));
        // It holds no slot: the only one taken is "Import data"'s.
        using var third = await SaveAsync(overseer, "Third", "acme/web");
        using var thirdStarted = await StartAsync(overseer, await AddressAsync(overseer, "Third"));
        Assert.Contains("All Active Intent slots are taken, by &quot;Import data&quot;.", await thirdStarted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Approve_and_start_is_refused_when_the_last_slot_was_taken_meanwhile_and_nothing_is_approved()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await SetMaxActiveIntentsAsync(overseer, 1);
        var address = await AwaitingApprovalOfAsync(overseer, 1);
        var opened = await overseer.OpenAsync(address);
        using var other = await SaveAsync(overseer, "Import data", "acme/web");
        using var otherStarted = await StartAsync(overseer, await AddressAsync(overseer, "Import data"));

        using var refused = await opened.SubmitFormAsync("approve-spec", [new("SpecVersion", "1"), new("Start", "true")]);

        var page = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("All Active Intent slots are taken, by \"Import data\". You can raise the maximum in Settings.", Alert(page));
        Assert.Equal("Awaiting Spec Approval", ElementText(page, "intent-state"));
        var after = await GetAsync(overseer, address);
        Assert.Equal("Awaiting Spec Approval", ElementText(after, "intent-state"));
        Assert.DoesNotContain("spec-approved", after);
        // Still editable: nothing is locked.
        Assert.Contains("/spec/edit", after);
    }

    [Fact]
    public async Task An_Approved_Intent_offers_Start_disabled_with_the_usual_explanation_while_every_slot_is_taken_and_Start_moves_it_to_Implementing_once_one_is_free()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await SetMaxActiveIntentsAsync(overseer, 1);
        var address = await AwaitingApprovalOfAsync(overseer, 1);
        using var approved = await ApproveAsync(overseer, address, 1, start: false);
        using var other = await SaveAsync(overseer, "Import data", "acme/web");
        var otherAddress = await AddressAsync(overseer, "Import data");
        using var otherStarted = await StartAsync(overseer, otherAddress);
        var openedFull = await overseer.OpenAsync(address);

        var full = await GetAsync(overseer, address);

        Assert.True(StartIsDisabled(full));
        Assert.Equal("All Active Intent slots are taken, by \"Import data\". You can raise the maximum in Settings.", Alert(full));
        using var refused = await openedFull.SubmitFormAsync("start-intent", []);
        Assert.Equal("Approved", ElementText(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), "intent-state"));

        await SetMaxActiveIntentsAsync(overseer, 2);
        var free = await GetAsync(overseer, address);
        Assert.False(StartIsDisabled(free));
        using var started = await StartAsync(overseer, address);

        Assert.Equal(address, started.FinalPath());
        var implementing = await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Implementing", ElementText(implementing, "intent-state"));
        Assert.Equal(("Green", "Working"), LightOn(implementing));
        Assert.Equal("Approved Spec, version 1", ElementText(implementing, "spec-approved"));
    }

    [Fact]
    public async Task Once_approved_the_server_refuses_any_edit_and_send_back_and_neither_is_offered()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var address = await AwaitingApprovalOfAsync(overseer, 1);
        var form = await SpecForm.OpenAsync(overseer, address);
        using var approved = await ApproveAsync(overseer, address, 1, start: false);

        form.Set("Summary", "Changed after approval.");
        var edit = await form.SubmitAsync("Save");
        using var sentBack = await overseer.SubmitFormAsync(address, "send-back-spec", [new("Comments", "Not right."), new("SpecVersion", "1")]);

        Assert.Contains("This Intent is Approved, and only a Spec awaiting approval can be edited.", WebUtility.HtmlDecode(edit.Html));
        Assert.Equal("This Intent is Approved, and only a Spec awaiting approval can be sent back.", Alert(await sentBack.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        var page = await GetAsync(overseer, address);
        Assert.Equal(1, SpecVersionOn(page));
        Assert.DoesNotContain("Send back", page);
        Assert.DoesNotContain("/spec/edit", page);
        Assert.Equal("<p>Orders can be exported as <strong>CSV</strong>.</p>", SpecMarkdown(page, "Summary"));
    }

    [Fact]
    public async Task Only_the_latest_Spec_version_can_be_approved_and_only_while_the_Intent_awaits_approval()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var address = await AwaitingApprovalOfAsync(overseer, 1);
        var form = await SpecForm.OpenAsync(overseer, address);
        form.Set("Summary", "Edited.");
        await form.SubmitAsync("Save");

        using var outdated = await ApproveAsync(overseer, address, 1, start: true);

        Assert.Equal(
            "The Spec was not approved: The Spec has a newer version, 2, since you read version 1. Read it, and approve it if it is right.",
            Alert(await outdated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Equal("Awaiting Spec Approval", ElementText(await GetAsync(overseer, address), "intent-state"));

        using var latest = await ApproveAsync(overseer, address, 2, start: false);
        Assert.Equal("Approved Spec, version 2", ElementText(await latest.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), "spec-approved"));
        using var again = await ApproveAsync(overseer, address, 2, start: true);
        Assert.Equal(
            "This Intent is Approved, and only a Spec awaiting approval can be approved.",
            Alert(await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Equal("Approved", ElementText(await GetAsync(overseer, address), "intent-state"));
    }

    [Fact]
    public async Task A_Draft_is_neither_offered_approval_nor_accepted_for_it()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        Assert.Null(ApproveButton(await GetAsync(overseer, address)));

        using var refused = await ApproveAsync(overseer, address, 1, start: false);

        Assert.Equal(
            "This Intent is Draft, and only a Spec awaiting approval can be approved.",
            Alert(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Equal("Draft", ElementText(await GetAsync(overseer, address), "intent-state"));
    }

    /// <summary>Whether the Start button on the page is disabled; the page must have one.</summary>
    private static bool StartIsDisabled(string page) => Regex.Match(page, @"<button[^>]*>\s*Start\s*</button>") is { Success: true } button
        ? button.Value.Contains("disabled")
        : throw new InvalidOperationException($"The page has no Start button: {page}");

    /// <summary>Has the Analyst hand in a valid Spec for a new Intent in acme/web, and gives its address once it awaits approval.</summary>
    private static async Task<string> AwaitingApprovalOfAsync(RunningOverseer overseer, int _)
    {
        // Any Intent started afterwards keeps refining.
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(Spec())), new FakeAgentSession(FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingSpecApprovalAsync(overseer, address);
        return address;
    }

    private static Task<HttpResponseMessage> ApproveAsync(RunningOverseer overseer, string address, int version, bool start) =>
        overseer.SubmitFormAsync(address, "approve-spec",
            [new("SpecVersion", version.ToString(System.Globalization.CultureInfo.InvariantCulture)), .. start ? [new KeyValuePair<string, string>("Start", "true")] : Array.Empty<KeyValuePair<string, string>>()]);

    /// <summary>The label of the page's approve button; <c>null</c> when it has none.</summary>
    private static string? ApproveButton(string page) =>
        Regex.Match(page, @"<button[^>]*class=""[^""]*""[^>]*>\s*(Approve[^<]*?)\s*</button>") is { Success: true } button
            ? WebUtility.HtmlDecode(button.Groups[1].Value)
            : null;
}
