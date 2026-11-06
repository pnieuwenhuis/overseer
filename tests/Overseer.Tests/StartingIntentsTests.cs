using System.Text.RegularExpressions;
using Overseer.Intents;
using static Overseer.Tests.DatabaseRaces;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;

namespace Overseer.Tests;

public class StartingIntentsTests(PostgresServer postgres)
{
    [Fact]
    public async Task Starting_a_Draft_from_its_detail_page_moves_it_to_Refining_in_the_Backlog_and_on_the_page_also_after_a_restart()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            using var saved = await SaveAsync(first, "Export data", "acme/web");
            address = await AddressAsync(first, "Export data");
            var draft = await GetAsync(first, address);
            Assert.False(StartIsDisabled(draft));
            Assert.DoesNotContain("role=\"alert\"", draft);

            using var response = await StartAsync(first, address);

            // Starting returns to the detail page, so reloading the page doesn't start the Intent again.
            Assert.Equal(address, response.FinalPath());
            Assert.Equal("Refining", await ShownStateAsync(response));
            Assert.Equal("Refining", ElementText(await GetAsync(first, "/"), "intent-state"));
        }

        await using var second = await RunningOverseer.StartAsync(connectionString);
        await second.LogInAsync();
        Assert.Equal("Refining", ElementText(await GetAsync(second, address), "intent-state"));
    }

    // What Start does here is move a Draft to Refining. The v1 spec's other ways into Refining, from AwaitingSpecApproval and Paused, are
    // triggers of their own, and its Approved -> Implementing on "User starts" takes Approved off this list when it arrives.
    [Theory]
    [InlineData(IntentState.Refining, "Refining")]
    [InlineData(IntentState.AwaitingSpecApproval, "Awaiting Spec Approval")]
    [InlineData(IntentState.Approved, "Approved")]
    [InlineData(IntentState.Implementing, "Implementing")]
    [InlineData(IntentState.InReview, "In Review")]
    [InlineData(IntentState.Paused, "Paused")]
    [InlineData(IntentState.Done, "Done")]
    [InlineData(IntentState.Cancelled, "Cancelled")]
    public async Task The_Intent_Lifecycle_refuses_a_Start_from_these_states_as_Start_moves_a_Draft_to_Refining(IntentState state, string shownAs)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        var detailPage = await overseer.OpenAsync(address);
        await SetStateAsync(overseer, "Export data", state);

        using var response = await detailPage.SubmitFormAsync("start-intent", []);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal($"This Intent is {shownAs}, and Start moves a Draft to Refining.", Alert(page));
        Assert.Equal(shownAs, ElementText(page, "intent-state"));
        Assert.DoesNotContain(">Start<", page);
        var opened = await GetAsync(overseer, address);
        Assert.Equal(shownAs, ElementText(opened, "intent-state"));
        Assert.DoesNotContain(">Start<", opened);
        Assert.DoesNotContain("role=\"alert\"", opened);
    }

    [Fact]
    public async Task Starting_an_Intent_someone_already_deleted_says_so()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        var detailPage = await overseer.OpenAsync(address);
        using var deleted = await DeleteAsync(overseer, address);

        using var response = await detailPage.SubmitFormAsync("start-intent", []);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"Got {response.StatusCode}");
        Assert.Equal("There is no such Intent: it may have been deleted.", Alert(page));
        Assert.DoesNotContain(">Start<", page);
        Assert.Matches("<a[^>]*href=\"/\"[^>]*>Back to the Backlog</a>", page);
        Assert.Empty(Backlog(await GetAsync(overseer, "/")));
    }

    [Fact]
    public async Task A_Draft_without_Repositories_in_scope_cannot_be_started_until_it_has_one()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");

        var page = await GetAsync(overseer, address);
        Assert.True(StartIsDisabled(page));
        Assert.Equal(NoRepositoryInScope, Alert(page));

        // The server refuses as well, for a browser that ignores the disabled button.
        using var refused = await StartAsync(overseer, address);
        var refusal = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(NoRepositoryInScope, Alert(refusal));
        Assert.True(StartIsDisabled(refusal));
        Assert.Equal("Draft", ElementText(refusal, "intent-state"));

        using var edited = await EditAsync(overseer, address, "save", "Export data", "acme/web");
        var editedPage = await edited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.False(StartIsDisabled(editedPage));
        Assert.DoesNotContain("role=\"alert\"", editedPage);
        using var started = await StartAsync(overseer, address);
        Assert.Equal("Refining", await ShownStateAsync(started));
    }

    [Fact]
    public async Task A_Draft_without_Repositories_in_scope_when_all_slots_are_taken_is_told_both()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await SetMaxActiveIntentsAsync(overseer, 1);
        await StartNewAsync(overseer, "Running");
        using var saved = await SaveAsync(overseer, "Export data");

        var page = await GetAsync(overseer, await AddressAsync(overseer, "Export data"));

        Assert.Equal($"{NoRepositoryInScope} {AllSlotsTaken("\"Running\"")}", Alert(page));
    }

    [Fact]
    public async Task A_started_Intent_offers_no_changes_and_its_pages_say_it_can_no_longer_be_changed()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var address = await StartNewAsync(overseer, "Export data");

        var page = await GetAsync(overseer, address);
        Assert.DoesNotContain(">Start<", page);
        Assert.DoesNotContain($"{address[1..]}/edit", page);
        Assert.DoesNotContain($"{address[1..]}/delete", page);

        var editor = await GetAsync(overseer, address + "/edit");
        Assert.Equal("This Intent is Refining, and only a Draft can be edited.", Alert(editor));
        Assert.DoesNotContain("<textarea", editor);

        var deletePage = await GetAsync(overseer, address + "/delete");
        Assert.Equal("This Intent is Refining, and only a Draft can be deleted.", Alert(deletePage));
        Assert.DoesNotContain("DeleteConfirmation", deletePage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_server_refuses_to_edit_a_started_Intent_with_the_Portal_bypassed(bool editorOpenedBeforeStarting)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/API", "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        var editorBefore = await overseer.OpenAsync(address + "/edit");
        using var started = await StartAsync(overseer, address);
        // Opened after the Start, the page has no form, so this is a request made by hand with the session's antiforgery token.
        var editor = editorOpenedBeforeStarting ? editorBefore : await overseer.OpenAsync(address + "/edit");

        using var response = await editor.SubmitFormAsync("edit-intent", EditorFields("Import data", "save", "acme/API"));

        Assert.Equal("This Intent is Refining, and only a Draft can be edited.", Alert(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        var stored = await GetAsync(overseer, address);
        Assert.Equal("<p>Export data</p>", RenderedText(stored));
        Assert.Equal("Repositories in scope: acme/web", ElementText(stored, "intent-repositories"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_server_refuses_to_delete_a_started_Intent_with_the_Portal_bypassed(bool pageOpenedBeforeStarting)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        var pageBefore = await overseer.OpenAsync(address + "/delete");
        using var started = await StartAsync(overseer, address);
        // Opened after the Start, the page has no form, so this is a request made by hand with the session's antiforgery token.
        var deletePage = pageOpenedBeforeStarting ? pageBefore : await overseer.OpenAsync(address + "/delete");

        using var response = await deletePage.SubmitFormAsync("delete-intent", Confirmed);

        Assert.Equal("This Intent is Refining, and only a Draft can be deleted.", Alert(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Equal("Refining", ElementText(await GetAsync(overseer, address), "intent-state"));
    }

    [Fact]
    public async Task A_started_Intent_still_holds_its_Repositories_in_scope_back_from_removal()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await StartNewAsync(overseer, "Export data");

        using var refused = await RemoveAsync(overseer, "acme/web");

        Assert.Equal("acme/web can't be removed: the open Intent \"Export data\" has it in scope.", Alert(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task With_every_slot_taken_Start_is_disabled_and_the_page_names_the_Intents_holding_them_and_the_way_out()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await StartNewAsync(overseer, "# Export <b>data</b>\n\nAs CSV.", "Export <b>data</b>");
        await StartNewAsync(overseer, "Import data");
        using var saved = await SaveAsync(overseer, "Waiting", "acme/web");
        var address = await AddressAsync(overseer, "Waiting");

        var page = await GetAsync(overseer, address);

        Assert.True(StartIsDisabled(page));
        Assert.Equal(AllSlotsTaken("\"Import data\" and \"Export <b>data</b>\""), Alert(page));
        Assert.DoesNotContain("<b>data", page);
    }

    [Fact]
    public async Task A_Start_with_every_slot_taken_is_refused_by_the_server_with_the_same_explanation()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await StartNewAsync(overseer, "Export data");
        using var saved = await SaveAsync(overseer, "Waiting", "acme/web");
        var address = await AddressAsync(overseer, "Waiting");
        var detailPage = await overseer.OpenAsync(address);
        await StartNewAsync(overseer, "Import data");

        using var response = await detailPage.SubmitFormAsync("start-intent", []);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AllSlotsTaken("\"Import data\" and \"Export data\""), Alert(page));
        Assert.True(StartIsDisabled(page));
        Assert.Equal("Draft", ElementText(page, "intent-state"));
        Assert.Equal("Draft", ElementText(await GetAsync(overseer, address), "intent-state"));
    }

    // Writes the states directly, as most can't be reached yet: each later ticket that makes one reachable owes a test through its real route.
    [Theory]
    [InlineData(IntentState.Refining, true)]
    [InlineData(IntentState.Implementing, true)]
    [InlineData(IntentState.InReview, true)]
    [InlineData(IntentState.Draft, false)]
    [InlineData(IntentState.AwaitingSpecApproval, false)]
    [InlineData(IntentState.Approved, false)]
    [InlineData(IntentState.Paused, false)]
    [InlineData(IntentState.Done, false)]
    [InlineData(IntentState.Cancelled, false)]
    public async Task Only_an_Intent_in_an_Active_state_takes_a_slot(IntentState state, bool takesASlot)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await SetMaxActiveIntentsAsync(overseer, 1);
        using var other = await SaveAsync(overseer, "Other", "acme/web");
        await SetStateAsync(overseer, "Other", state);
        using var saved = await SaveAsync(overseer, "Waiting", "acme/web");
        var address = await AddressAsync(overseer, "Waiting");

        Assert.Equal(takesASlot, StartIsDisabled(await GetAsync(overseer, address)));

        using var response = await StartAsync(overseer, address);
        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(takesASlot ? "Draft" : "Refining", ElementText(page, "intent-state"));
        if (takesASlot)
        {
            Assert.Equal(AllSlotsTaken("\"Other\""), Alert(page));
        }
    }

    [Fact]
    public async Task A_Repository_Setup_takes_no_slot()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await SetMaxActiveIntentsAsync(overseer, 1);
        using var setup = await StartSetupAsync(overseer, "acme", "api");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        Assert.False(StartIsDisabled(await GetAsync(overseer, address)));
        using var response = await StartAsync(overseer, address);

        Assert.Equal("Refining", await ShownStateAsync(response));
    }

    [Fact]
    public async Task Lowering_the_maximum_below_the_Active_Intents_leaves_them_Refining_and_Start_disabled_until_a_slot_is_free()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var first = await StartNewAsync(overseer, "First");
        var second = await StartNewAsync(overseer, "Second");
        using var saved = await SaveAsync(overseer, "Waiting", "acme/web");
        var address = await AddressAsync(overseer, "Waiting");

        await SetMaxActiveIntentsAsync(overseer, 1);

        Assert.Equal("Refining", ElementText(await GetAsync(overseer, first), "intent-state"));
        Assert.Equal("Refining", ElementText(await GetAsync(overseer, second), "intent-state"));
        var page = await GetAsync(overseer, address);
        Assert.True(StartIsDisabled(page));
        Assert.Equal(AllSlotsTaken("\"Second\" and \"First\""), Alert(page));
        using var refused = await StartAsync(overseer, address);
        Assert.Equal(AllSlotsTaken("\"Second\" and \"First\""), Alert(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));

        // One freed slot isn't enough: two Intents hold the one slot there is.
        await SetStateAsync(overseer, "First", IntentState.Done);
        Assert.Equal(AllSlotsTaken("\"Second\""), Alert(await GetAsync(overseer, address)));

        await SetStateAsync(overseer, "Second", IntentState.Done);
        Assert.False(StartIsDisabled(await GetAsync(overseer, address)));
    }

    [Fact]
    public async Task Raising_the_maximum_as_the_page_suggests_lets_the_Start_through()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await StartNewAsync(overseer, "First");
        await StartNewAsync(overseer, "Second");
        using var saved = await SaveAsync(overseer, "Waiting", "acme/web");
        var address = await AddressAsync(overseer, "Waiting");
        Assert.True(StartIsDisabled(await GetAsync(overseer, address)));

        await SetMaxActiveIntentsAsync(overseer, 3);

        Assert.False(StartIsDisabled(await GetAsync(overseer, address)));
        using var response = await StartAsync(overseer, address);
        Assert.Equal("Refining", await ShownStateAsync(response));
    }

    [Fact]
    public async Task Two_Starts_racing_for_the_last_slot_cannot_both_succeed_and_the_loser_gets_the_same_explanation()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await StartNewAsync(overseer, "Running");
        using var savedFirst = await SaveAsync(overseer, "First", "acme/web");
        using var savedSecond = await SaveAsync(overseer, "Second", "acme/web");
        var first = await AddressAsync(overseer, "First");
        var second = await AddressAsync(overseer, "Second");

        // With updates of Intents held up, the first Start stops short of writing, after its look at the slots. The second gets the
        // chance to look at the slots then, while the first hasn't taken one yet, which is what a count before the update would let through.
        var (won, lost) = await RaceAsync(overseer, $"""LOCK TABLE "Intents" IN SHARE MODE""",
            () => StartAsync(overseer, first),
            () => StartAsync(overseer, second));

        Assert.Equal("Refining", await ShownStateAsync(won));
        var page = await lost.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AllSlotsTaken("\"First\" and \"Running\""), Alert(page));
        Assert.True(StartIsDisabled(page));
        Assert.Equal("Draft", ElementText(page, "intent-state"));
        Assert.Equal("Draft", ElementText(await GetAsync(overseer, second), "intent-state"));
    }

    [Fact]
    public async Task Many_Starts_at_the_same_moment_fill_the_free_slots_and_no_more()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await SetMaxActiveIntentsAsync(overseer, 3);
        string[] names = ["One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight"];
        List<PortalTab> tabs = [];
        foreach (var name in names)
        {
            using var saved = await SaveAsync(overseer, name, "acme/web");
            tabs.Add(await overseer.OpenAsync(await AddressAsync(overseer, name)));
        }

        var responses = await Task.WhenAll(tabs.Select(tab => tab.SubmitFormAsync("start-intent", [])));

        var pages = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.All(responses, response => Assert.True(response.IsSuccessStatusCode, $"Got {response.StatusCode}"));
        Assert.Equal(3, pages.Count(page => ElementText(page, "intent-state") == "Refining"));
        Assert.All(
            pages.Where(page => ElementText(page, "intent-state") == "Draft"),
            page => Assert.StartsWith("All Active Intent slots are taken, by ", Alert(page)));
        Assert.Equal(3, Regex.Matches(await GetAsync(overseer, "/"), "class=\"intent-state\"[^>]*>Refining<").Count);
    }

    [Fact]
    public async Task An_edit_during_a_Start_waits_for_it_and_is_refused()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/API", "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        var editor = await overseer.OpenAsync(address + "/edit");

        // With updates of Intents held up, a Start stops short of its commit, however far it has got by then.
        var (started, edited) = await RaceAsync(overseer, $"""LOCK TABLE "Intents" IN SHARE MODE""",
            () => StartAsync(overseer, address),
            () => editor.SubmitFormAsync("edit-intent", EditorFields("Import data", "save", "acme/API")));

        Assert.Equal("Refining", await ShownStateAsync(started));
        Assert.Equal("This Intent is Refining, and only a Draft can be edited.", Alert(await edited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        var stored = await GetAsync(overseer, address);
        Assert.Equal("<p>Export data</p>", RenderedText(stored));
        Assert.Equal("Repositories in scope: acme/web", ElementText(stored, "intent-repositories"));
    }

    [Fact]
    public async Task A_delete_during_a_Start_waits_for_it_and_is_refused()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        var deletePage = await overseer.OpenAsync(address + "/delete");

        // With updates of Intents held up, a Start stops short of its commit, however far it has got by then.
        var (started, deleted) = await RaceAsync(overseer, $"""LOCK TABLE "Intents" IN SHARE MODE""",
            () => StartAsync(overseer, address),
            () => deletePage.SubmitFormAsync("delete-intent", Confirmed));

        Assert.Equal("Refining", await ShownStateAsync(started));
        Assert.Equal("This Intent is Refining, and only a Draft can be deleted.", Alert(await deleted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Equal("Refining", ElementText(await GetAsync(overseer, address), "intent-state"));
    }

    [Fact]
    public async Task A_Start_during_an_edit_taking_every_Repository_out_of_scope_waits_for_it_and_is_refused()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        var detailPage = await overseer.OpenAsync(address);

        // With updates of Intents held up, an edit stops short of its commit, however far it has got by then.
        var (edited, started) = await RaceAsync(overseer, $"""LOCK TABLE "Intents" IN SHARE MODE""",
            () => EditAsync(overseer, address, "save", "Export data"),
            () => detailPage.SubmitFormAsync("start-intent", []));

        Assert.Equal(address, edited.FinalPath());
        var page = await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(NoRepositoryInScope, Alert(page));
        Assert.Equal("Draft", ElementText(page, "intent-state"));
    }

    [Fact]
    public async Task Two_Starts_of_the_same_Intent_take_one_slot_and_the_second_is_told_it_has_started()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        using var other = await SaveAsync(overseer, "Import data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        var otherTab = await overseer.OpenAsync(address);

        // With updates of Intents held up, the first Start stops short of its commit, however far it has got by then.
        var (first, second) = await RaceAsync(overseer, $"""LOCK TABLE "Intents" IN SHARE MODE""",
            () => StartAsync(overseer, address),
            () => otherTab.SubmitFormAsync("start-intent", []));

        Assert.Equal("Refining", await ShownStateAsync(first));
        Assert.Equal("This Intent is Refining, and Start moves a Draft to Refining.", Alert(await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        // The other of the default two slots is still free.
        Assert.False(StartIsDisabled(await GetAsync(overseer, await AddressAsync(overseer, "Import data"))));
    }

    private const string NoRepositoryInScope = "Start needs at least one Repository in scope: edit this Draft to select one.";

    private static string AllSlotsTaken(string holders) => $"All Active Intent slots are taken, by {holders}. You can raise the maximum in Settings.";

    /// <summary>Captures a Draft with this text, with <c>acme/web</c> in scope, and starts it; returns the address of its detail page.</summary>
    /// <param name="firstLine">What the Backlog lists it by, when that isn't the whole text.</param>
    private static async Task<string> StartNewAsync(RunningOverseer overseer, string text, string? firstLine = null)
    {
        using var saved = await SaveAsync(overseer, text, "acme/web");
        var address = await AddressAsync(overseer, firstLine ?? text);
        using var started = await StartAsync(overseer, address);
        Assert.Equal("Refining", await ShownStateAsync(started));
        return address;
    }

    /// <summary>The state shown on the page this response brought back.</summary>
    private static async Task<string> ShownStateAsync(HttpResponseMessage response) =>
        ElementText(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), "intent-state");

    /// <summary>Whether the Start button on the page is disabled; the page must have one.</summary>
    private static bool StartIsDisabled(string page) => Regex.Match(page, @"<button[^>]*>\s*Start\s*</button>") is { Success: true } button
        ? Regex.IsMatch(button.Value, @"\sdisabled\b")
        : throw new InvalidOperationException($"The page has no Start button: {page}");
}
