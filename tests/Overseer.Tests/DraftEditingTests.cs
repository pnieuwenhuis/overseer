using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Overseer.Intents;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;

namespace Overseer.Tests;

public class DraftEditingTests(PostgresServer postgres)
{
    [Fact]
    public async Task Clicking_an_Intent_in_the_Backlog_opens_its_detail_page_with_its_rendered_text_state_and_Repositories_in_scope()
    {
        await using var overseer = await StartAsync("acme/API", "acme/web", "acme/docs");
        using var saved = await SaveAsync(overseer, "# Export data\n\n- As ~~XML~~ CSV", "acme/web", "acme/API");

        var page = await GetAsync(overseer, await AddressAsync(overseer, "Export data"));

        Assert.Equal("""
            <h1>Export data</h1>
            <ul>
            <li>As <del>XML</del> CSV</li>
            </ul>
            """, RenderedText(page));
        Assert.Equal("Draft", ElementText(page, "intent-state"));
        Assert.Equal("Repositories in scope: acme/API, acme/web", ElementText(page, "intent-repositories"));
    }

    [Fact]
    public async Task HTML_in_an_Intent_shows_as_text_on_its_detail_page()
    {
        await using var overseer = await StartAsync();
        using var saved = await SaveAsync(overseer, "Remove <script>alert(1)</script> from the view");

        var page = await GetAsync(overseer, await AddressAsync(overseer, "Remove <script>alert(1)</script> from the view"));

        Assert.Equal("<p>Remove &lt;script&gt;alert(1)&lt;/script&gt; from the view</p>", RenderedText(page));
        Assert.DoesNotContain("<script>alert(1)", page);
    }

    [Theory]
    [InlineData("/intents/{0}")]
    [InlineData("/intents/{0}/edit")]
    [InlineData("/intents/{0}/delete")]
    [InlineData("/intents/not-an-id")]
    [InlineData("/intents/not-an-id/edit")]
    [InlineData("/intents/not-an-id/delete")]
    [InlineData("/intents/00000000-0000-0000-0000-00000000000")]
    public async Task An_unknown_or_malformed_Intent_id_gets_the_not_found_page(string address)
    {
        await using var overseer = await StartAsync();

        using var response = await overseer.Http.GetAsync(string.Format(address, Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Sorry, the content you are looking for does not exist.", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_detail_page_of_a_Draft_offers_editing_and_deleting()
    {
        await using var overseer = await StartAsync();
        using var saved = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");

        var page = await GetAsync(overseer, address);

        Assert.Matches($"<a[^>]*href=\"{address[1..]}/edit\"[^>]*>Edit</a>", page);
        Assert.Matches($"<a[^>]*href=\"{address[1..]}/delete\"[^>]*>Delete</a>", page);
    }

    [Theory]
    [InlineData(IntentState.Done)]
    [InlineData(IntentState.Cancelled)]
    public async Task The_detail_page_of_an_Intent_that_is_no_Draft_shows_it_but_offers_no_changes(IntentState state)
    {
        await using var overseer = await StartAsync();
        using var saved = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");
        await SetStateAsync(overseer, "Export data", state);

        var page = await GetAsync(overseer, address);

        Assert.Equal(state.ToString(), ElementText(page, "intent-state"));
        Assert.Equal("<p>Export data</p>", RenderedText(page));
        Assert.DoesNotContain($"{address[1..]}/edit", page);
        Assert.DoesNotContain($"{address[1..]}/delete", page);
    }

    [Fact]
    public async Task The_editor_starts_from_the_stored_text_and_the_stored_Repositories_in_scope()
    {
        await using var overseer = await StartAsync("acme/API", "acme/docs", "acme/web");
        using var saved = await SaveAsync(overseer, "# Export data\n\nAs <CSV>.", "acme/web");

        var page = await GetAsync(overseer, await AddressAsync(overseer, "Export data") + "/edit");

        Assert.Equal("# Export data\n\nAs <CSV>.", Editor(page));
        Assert.Equal([("acme/API", false), ("acme/docs", false), ("acme/web", true)], Offered(page));
        Assert.Equal("<h1>Export data</h1>\n<p>As &lt;CSV&gt;.</p>", Preview(page));
    }

    [Fact]
    public async Task The_editor_of_a_Draft_without_Repositories_in_scope_starts_with_none_selected()
    {
        await using var overseer = await StartAsync("acme/web");
        using var saved = await SaveAsync(overseer, "Export data");

        var page = await GetAsync(overseer, await AddressAsync(overseer, "Export data") + "/edit");

        Assert.Equal([("acme/web", false)], Offered(page));
    }

    [Fact]
    public async Task Saving_an_edit_changes_the_text_and_the_Repositories_in_scope_also_after_a_restart()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/API", "acme/web"]))
        {
            using var saved = await SaveAsync(first, "Export data", "acme/web");
            address = await AddressAsync(first, "Export data");

            // A browser sends a textarea's line breaks as CR LF.
            using var response = await EditAsync(first, address, "save", "# Import data\r\n\r\nAs CSV.", "acme/API");

            // Saving returns to the detail page, so reloading the page doesn't save the edit again.
            Assert.Equal(address, response.RequestMessage!.RequestUri!.AbsolutePath);
            var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal("<h1>Import data</h1>\n<p>As CSV.</p>", RenderedText(page));
            Assert.Equal("Repositories in scope: acme/API", ElementText(page, "intent-repositories"));
        }

        await using var second = await RunningOverseer.StartAsync(connectionString);
        await second.LogInAsync();

        var reopened = await GetAsync(second, address);
        Assert.Equal("<h1>Import data</h1>\n<p>As CSV.</p>", RenderedText(reopened));
        Assert.Equal("Repositories in scope: acme/API", ElementText(reopened, "intent-repositories"));
        Assert.Equal(address, await AddressAsync(second, "Import data"));
        await using var database = second.OpenDatabase();
        Assert.Equal("# Import data\n\nAs CSV.", await database.Intents.Select(intent => intent.Text).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_Draft_can_be_edited_to_have_no_Repositories_in_scope()
    {
        await using var overseer = await StartAsync("acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");

        using var response = await EditAsync(overseer, await AddressAsync(overseer, "Export data"), "save", "Export data");

        Assert.Equal("No Repositories in scope", ElementText(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), "intent-repositories"));
    }

    [Fact]
    public async Task A_repository_in_another_case_or_twice_is_in_scope_once_as_it_is_configured_after_an_edit()
    {
        await using var overseer = await StartAsync("acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");

        using var response = await EditAsync(overseer, await AddressAsync(overseer, "Export data"), "save", "Export data", "ACME/WEB", "acme/web");

        Assert.Equal("Repositories in scope: acme/web", ElementText(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), "intent-repositories"));
    }

    [Fact]
    public async Task An_edit_keeps_the_Intent_in_its_place_in_the_Backlog_and_leaves_other_Intents_alone()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        using var older = await SaveAsync(overseer, "Older", "acme/web");
        time.Advance(TimeSpan.FromMinutes(1));
        using var newer = await SaveAsync(overseer, "Newer", "acme/web");
        time.Advance(TimeSpan.FromMinutes(1));

        using var response = await EditAsync(overseer, await AddressAsync(overseer, "Older"), "save", "Older, edited");

        Assert.Equal(
            [("Newer", "Repositories in scope: acme/web"), ("Older, edited", "No Repositories in scope")],
            Backlog(await GetAsync(overseer, "/")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\n  ")]
    public async Task An_edit_without_text_is_refused_keeping_the_selection_and_the_stored_Intent(string text)
    {
        await using var overseer = await StartAsync("acme/API", "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var response = await EditAsync(overseer, address, "save", text, "acme/API");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(address + "/edit", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal("An Intent needs text: write what you want changed.", Alert(page));
        Assert.Equal([("acme/API", true), ("acme/web", false)], Offered(page));
        Assert.Equal([("Export data", "Repositories in scope: acme/web")], Backlog(await GetAsync(overseer, "/")));
    }

    [Fact]
    public async Task Previewing_an_edit_renders_it_keeps_what_was_entered_and_saves_nothing()
    {
        await using var overseer = await StartAsync("acme/API", "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");

        using var response = await EditAsync(overseer, await AddressAsync(overseer, "Export data"), "preview", "Import <b>data</b>", "acme/API");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("<p>Import &lt;b&gt;data&lt;/b&gt;</p>", Preview(page));
        Assert.Equal("Import <b>data</b>", Editor(page));
        Assert.Equal([("acme/API", true), ("acme/web", false)], Offered(page));
        Assert.DoesNotContain("role=\"alert\"", page);
        Assert.Equal([("Export data", "Repositories in scope: acme/web")], Backlog(await GetAsync(overseer, "/")));
    }

    [Fact]
    public async Task Previewing_an_edit_with_every_repository_deselected_keeps_them_deselected()
    {
        await using var overseer = await StartAsync("acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");

        using var response = await EditAsync(overseer, await AddressAsync(overseer, "Export data"), "preview", "Export data");

        Assert.Equal([("acme/web", false)], Offered(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_repository_removed_after_the_editor_opened_refuses_the_edit_and_keeps_what_was_entered()
    {
        await using var overseer = await StartAsync("acme/API", "acme/docs", "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/docs");
        var address = await AddressAsync(overseer, "Export data");
        using var removed = await RemoveAsync(overseer, "acme/web");

        using var response = await EditAsync(overseer, address, "save", "Import data", "acme/web", "acme/API");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("acme/web is no longer a Configured Repository. Check the Repositories in scope and save again.", Alert(page));
        Assert.Equal("Import data", Editor(page));
        Assert.Equal([("acme/API", true), ("acme/docs", false)], Offered(page));
        Assert.Equal([("Export data", "Repositories in scope: acme/docs")], Backlog(await GetAsync(overseer, "/")));
    }

    [Fact]
    public async Task Previewing_an_edit_after_a_selected_repository_was_removed_says_so()
    {
        await using var overseer = await StartAsync("acme/docs", "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/docs");
        var address = await AddressAsync(overseer, "Export data");
        using var removed = await RemoveAsync(overseer, "acme/web");

        using var response = await EditAsync(overseer, address, "preview", "Import data", "acme/web");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("acme/web is no longer a Configured Repository. Check the Repositories in scope and save again.", Alert(page));
        Assert.Equal("Import data", Editor(page));
    }

    [Fact]
    public async Task An_edit_decides_what_holds_a_repository_back_from_removal()
    {
        await using var overseer = await StartAsync("acme/API", "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");

        using var edited = await EditAsync(overseer, await AddressAsync(overseer, "Export data"), "save", "Export data", "acme/API");

        using var freed = await RemoveAsync(overseer, "acme/web");
        Assert.Contains("acme/web is removed.", await freed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var heldBack = await RemoveAsync(overseer, "acme/API");
        Assert.Equal("acme/API can't be removed: the open Intent \"Export data\" has it in scope.", Alert(await heldBack.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Opening_the_delete_page_asks_for_confirmation_showing_the_text_safely_and_deletes_nothing()
    {
        await using var overseer = await StartAsync();
        using var saved = await SaveAsync(overseer, "Remove <script>alert(1)</script> from the view");
        var address = await AddressAsync(overseer, "Remove <script>alert(1)</script> from the view");

        var page = await GetAsync(overseer, address + "/delete");

        Assert.Contains("Delete this Draft?", page);
        Assert.Equal("<p>Remove &lt;script&gt;alert(1)&lt;/script&gt; from the view</p>", RenderedText(page));
        Assert.DoesNotContain("<script>alert(1)", page);
        Assert.Matches($"<a[^>]*href=\"{address[1..]}\"[^>]*>Keep it</a>", page);
        Assert.Single(Backlog(await GetAsync(overseer, "/")));
    }

    [Theory]
    [InlineData]
    [InlineData("yes")]
    public async Task A_delete_submitted_without_confirmation_asks_again_and_deletes_nothing(params string[] confirmation)
    {
        await using var overseer = await StartAsync();
        using var saved = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");

        using var response = await overseer.SubmitFormAsync(address + "/delete", "delete-intent",
            confirmation.Select(value => KeyValuePair.Create("DeleteConfirmation", value)));

        Assert.Contains("Delete this Draft?", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Single(Backlog(await GetAsync(overseer, "/")));
        await using var database = overseer.OpenDatabase();
        Assert.Equal(1, await database.Intents.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_confirmed_delete_removes_the_Draft_from_the_Backlog_and_from_PostgreSQL_with_its_Repositories_in_scope()
    {
        await using var overseer = await StartAsync("acme/API", "acme/web");
        using var kept = await SaveAsync(overseer, "Stays", "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web", "acme/API");
        var address = await AddressAsync(overseer, "Export data");

        using var response = await DeleteAsync(overseer, address);

        // Deleting returns to the Backlog.
        Assert.Equal("/", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal([("Stays", "Repositories in scope: acme/web")], Backlog(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        await using var database = overseer.OpenDatabase();
        // It is gone, not Cancelled, and of the Repositories in scope only the other Intent's are left.
        Assert.Equal(["Stays"], await database.Intents.Select(intent => intent.Text).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["acme/web"], await database.Intents.SelectMany(intent => intent.RepositoriesInScope).Select(repository => repository.FullName).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await database.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "RepositoriesInScope" """).SingleAsync(TestContext.Current.CancellationToken));
        using var gone = await overseer.Http.GetAsync(address, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_Draft_frees_the_Configured_Repositories_it_had_in_scope_for_removal()
    {
        await using var overseer = await StartAsync("acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        using var refused = await RemoveAsync(overseer, "acme/web");
        Assert.Equal("acme/web can't be removed: the open Intent \"Export data\" has it in scope.", Alert(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));

        using var deleted = await DeleteAsync(overseer, await AddressAsync(overseer, "Export data"));

        using var removed = await RemoveAsync(overseer, "acme/web");
        Assert.Contains("acme/web is removed.", await removed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(IntentState.Done)]
    [InlineData(IntentState.Cancelled)]
    public async Task The_editor_of_an_Intent_that_is_no_Draft_says_so_and_offers_no_form(IntentState state)
    {
        await using var overseer = await StartAsync();
        using var saved = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");
        await SetStateAsync(overseer, "Export data", state);

        var page = await GetAsync(overseer, address + "/edit");

        Assert.Equal($"This Intent is {state}, and only a Draft can be edited.", Alert(page));
        Assert.DoesNotContain("<textarea", page);
        Assert.Matches($"<a[^>]*href=\"{address[1..]}\"[^>]*>Back to the Intent</a>", page);
    }

    [Theory]
    [InlineData(IntentState.Done)]
    [InlineData(IntentState.Cancelled)]
    public async Task The_delete_page_of_an_Intent_that_is_no_Draft_says_so_and_offers_no_button(IntentState state)
    {
        await using var overseer = await StartAsync();
        using var saved = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");
        await SetStateAsync(overseer, "Export data", state);

        var page = await GetAsync(overseer, address + "/delete");

        Assert.Equal($"This Intent is {state}, and only a Draft can be deleted.", Alert(page));
        Assert.DoesNotContain("DeleteConfirmation", page);
    }

    [Theory]
    [InlineData(IntentState.Done)]
    [InlineData(IntentState.Cancelled)]
    public async Task An_edit_from_an_editor_opened_while_the_Intent_was_a_Draft_is_refused_keeping_what_was_entered(IntentState state)
    {
        await using var overseer = await StartAsync("acme/API", "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        var editor = await overseer.OpenAsync(address + "/edit");
        await SetStateAsync(overseer, "Export data", state);

        using var response = await editor.SubmitFormAsync("edit-intent", EditorFields("Import data", "save", "acme/API"));

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal($"This Intent is {state}, and only a Draft can be edited.", Alert(page));
        Assert.Equal("Import data", Editor(page));
        Assert.Equal([("acme/API", true), ("acme/web", false)], Offered(page));
        var stored = await GetAsync(overseer, address);
        Assert.Equal("<p>Export data</p>", RenderedText(stored));
        Assert.Equal("Repositories in scope: acme/web", ElementText(stored, "intent-repositories"));
    }

    [Fact]
    public async Task Previewing_in_an_editor_opened_while_the_Intent_was_a_Draft_says_it_no_longer_can_be_edited()
    {
        await using var overseer = await StartAsync("acme/API");
        using var saved = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");
        var editor = await overseer.OpenAsync(address + "/edit");
        await SetStateAsync(overseer, "Export data", IntentState.Done);

        using var response = await editor.SubmitFormAsync("edit-intent", EditorFields("Import data", "preview", "acme/API"));

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("This Intent is Done, and only a Draft can be edited.", Alert(page));
        Assert.Equal("<p>Import data</p>", Preview(page));
        Assert.Equal([("acme/API", true)], Offered(page));
    }

    [Theory]
    [InlineData(IntentState.Done)]
    [InlineData(IntentState.Cancelled)]
    public async Task A_delete_from_a_page_opened_while_the_Intent_was_a_Draft_is_refused_and_deletes_nothing(IntentState state)
    {
        await using var overseer = await StartAsync("acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        var deletePage = await overseer.OpenAsync(address + "/delete");
        await SetStateAsync(overseer, "Export data", state);

        using var response = await deletePage.SubmitFormAsync("delete-intent", Confirmed);

        Assert.Equal(
            $"This Intent is {state}, and only a Draft can be deleted.",
            Alert(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Equal("<p>Export data</p>", RenderedText(await GetAsync(overseer, address)));
        await using var database = overseer.OpenDatabase();
        Assert.Equal(["acme/web"], await database.Intents.SelectMany(intent => intent.RepositoriesInScope).Select(repository => repository.FullName).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_edit_during_a_change_of_state_waits_for_it_and_is_refused()
    {
        await using var overseer = await StartAsync("acme/web");
        using var saved = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");
        var editor = await overseer.OpenAsync(address + "/edit");

        using var response = await WhileLeavingDraftAsync(overseer, () => editor.SubmitFormAsync("edit-intent", EditorFields("Import data", "save", "acme/web")));

        Assert.Equal("This Intent is Done, and only a Draft can be edited.", Alert(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        var stored = await GetAsync(overseer, address);
        Assert.Equal("<p>Export data</p>", RenderedText(stored));
        Assert.Equal("No Repositories in scope", ElementText(stored, "intent-repositories"));
    }

    [Fact]
    public async Task A_delete_during_a_change_of_state_waits_for_it_and_is_refused()
    {
        await using var overseer = await StartAsync();
        using var saved = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");
        var deletePage = await overseer.OpenAsync(address + "/delete");

        using var response = await WhileLeavingDraftAsync(overseer, () => deletePage.SubmitFormAsync("delete-intent", Confirmed));

        Assert.Equal("This Intent is Done, and only a Draft can be deleted.", Alert(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Equal("Done", ElementText(await GetAsync(overseer, address), "intent-state"));
    }

    [Fact]
    public async Task An_edit_of_an_Intent_deleted_after_the_editor_opened_says_so_keeping_what_was_entered_and_stores_nothing()
    {
        await using var overseer = await StartAsync("acme/API", "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        var editor = await overseer.OpenAsync(address + "/edit");
        using var deleted = await DeleteAsync(overseer, address);

        using var response = await editor.SubmitFormAsync("edit-intent", EditorFields("Import data", "save", "acme/API"));

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"Got {response.StatusCode}");
        Assert.Equal("There is no such Intent: it may have been deleted.", Alert(page));
        Assert.Equal("Import data", Editor(page));
        Assert.Equal([("acme/API", true), ("acme/web", false)], Offered(page));
        Assert.Matches("<a[^>]*href=\"/\"[^>]*>Back to the Backlog</a>", page);
        Assert.Empty(Backlog(await GetAsync(overseer, "/")));
        await using var database = overseer.OpenDatabase();
        Assert.Equal(0, await database.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "RepositoriesInScope" """).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Previewing_an_Intent_deleted_after_the_editor_opened_says_so_keeping_what_was_entered()
    {
        await using var overseer = await StartAsync();
        using var saved = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");
        var editor = await overseer.OpenAsync(address + "/edit");
        using var deleted = await DeleteAsync(overseer, address);

        using var response = await editor.SubmitFormAsync("edit-intent", EditorFields("Import data", "preview"));

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("There is no such Intent: it may have been deleted.", Alert(page));
        Assert.Equal("<p>Import data</p>", Preview(page));
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("not confirmed")]
    public async Task A_delete_of_an_Intent_someone_already_deleted_says_so(string confirmation)
    {
        await using var overseer = await StartAsync();
        using var saved = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");
        var deletePage = await overseer.OpenAsync(address + "/delete");
        using var deleted = await DeleteAsync(overseer, address);

        using var response = await deletePage.SubmitFormAsync("delete-intent", [new("DeleteConfirmation", confirmation)]);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"Got {response.StatusCode}");
        Assert.Equal("There is no such Intent: it may have been deleted.", Alert(page));
        Assert.Matches("<a[^>]*href=\"/\"[^>]*>Back to the Backlog</a>", page);
    }

    [Fact]
    public async Task An_edit_under_way_and_a_removal_of_a_repository_it_brings_into_scope_cannot_both_succeed()
    {
        await using var overseer = await StartAsync("acme/web");
        using var saved = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");

        // With updates of Intents held up, an edit stops short of its commit, however far it has got by then.
        var (edited, removed) = await RaceAsync(overseer, $"""LOCK TABLE "Intents" IN SHARE MODE""",
            () => EditAsync(overseer, address, "save", "Import data", "acme/web"),
            () => RemoveAsync(overseer, "acme/web"));

        await AssertExactlyOneSucceededAsync(overseer, address, edited, removed);
    }

    [Fact]
    public async Task A_removal_under_way_and_an_edit_bringing_its_repository_into_scope_cannot_both_succeed()
    {
        await using var overseer = await StartAsync("acme/web");
        using var saved = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");

        // With deletes from ConfiguredRepositories held up, a removal stops short of its commit, however far it has got by then.
        var (removed, edited) = await RaceAsync(overseer, $"""LOCK TABLE "ConfiguredRepositories" IN SHARE MODE""",
            () => RemoveAsync(overseer, "acme/web"),
            () => EditAsync(overseer, address, "save", "Import data", "acme/web"));

        await AssertExactlyOneSucceededAsync(overseer, address, edited, removed);
    }

    [Fact]
    public async Task A_removal_during_a_delete_is_refused_as_it_would_be_a_moment_earlier_and_succeeds_after_it()
    {
        await using var overseer = await StartAsync("acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        // With deletes from Intents held up, a delete stops short of its commit, however far it has got by then.
        var (deleted, removed) = await RaceAsync(overseer, $"""LOCK TABLE "Intents" IN SHARE MODE""",
            () => DeleteAsync(overseer, address),
            () => RemoveAsync(overseer, "acme/web"));

        Assert.Equal("/", deleted.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal("acme/web can't be removed: the open Intent \"Export data\" has it in scope.", Alert(await removed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Empty(Backlog(await GetAsync(overseer, "/")));
        using var removedAfter = await RemoveAsync(overseer, "acme/web");
        Assert.Contains("acme/web is removed.", await removedAfter.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_edit_during_a_delete_waits_for_it_and_finds_the_Intent_gone()
    {
        await using var overseer = await StartAsync("acme/web");
        using var saved = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");
        var editor = await overseer.OpenAsync(address + "/edit");

        // With deletes from Intents held up, a delete stops short of its commit, however far it has got by then.
        var (deleted, edited) = await RaceAsync(overseer, $"""LOCK TABLE "Intents" IN SHARE MODE""",
            () => DeleteAsync(overseer, address),
            () => editor.SubmitFormAsync("edit-intent", EditorFields("Import data", "save", "acme/web")));

        Assert.Equal("/", deleted.RequestMessage!.RequestUri!.AbsolutePath);
        var page = await edited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(edited.IsSuccessStatusCode, $"Got {edited.StatusCode}");
        Assert.Equal("There is no such Intent: it may have been deleted.", Alert(page));
        Assert.Empty(Backlog(await GetAsync(overseer, "/")));
        await using var database = overseer.OpenDatabase();
        Assert.Equal(0, await database.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "RepositoriesInScope" """).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_delete_during_an_edit_waits_for_it_and_leaves_nothing_behind()
    {
        await using var overseer = await StartAsync("acme/web");
        using var saved = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");
        var deletePage = await overseer.OpenAsync(address + "/delete");

        // With updates of Intents held up, an edit stops short of its commit, however far it has got by then.
        var (edited, deleted) = await RaceAsync(overseer, $"""LOCK TABLE "Intents" IN SHARE MODE""",
            () => EditAsync(overseer, address, "save", "Import data", "acme/web"),
            () => deletePage.SubmitFormAsync("delete-intent", Confirmed));

        Assert.Equal(address, edited.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal("/", deleted.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Empty(Backlog(await GetAsync(overseer, "/")));
        await using var database = overseer.OpenDatabase();
        Assert.Equal(0, await database.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "RepositoriesInScope" """).SingleAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Runs <paramref name="change"/> while another transaction, not committed yet, takes the only Intent from Draft to Done, and commits
    /// that once the change waits for it. The page's first look at the Intent still finds a Draft, as a check before the writing would.
    /// </summary>
    private static async Task<HttpResponseMessage> WhileLeavingDraftAsync(RunningOverseer overseer, Func<Task<HttpResponseMessage>> change)
    {
        await using var holder = overseer.OpenDatabase();
        await using var transaction = await holder.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await holder.Intents.ExecuteUpdateAsync(intent => intent.SetProperty(record => record.State, IntentState.Done), TestContext.Current.CancellationToken);
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watching.CancelAfter(TimeSpan.FromSeconds(30));

        var done = change();
        await Task.WhenAny(done, UntilWaitingOnLocksAsync(overseer, 1, watching.Token));
        await watching.CancelAsync();

        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return await done;
    }

    private static async Task AssertExactlyOneSucceededAsync(RunningOverseer overseer, string address, HttpResponseMessage edited, HttpResponseMessage removed)
    {
        var intentEdited = edited.RequestMessage!.RequestUri!.AbsolutePath == address;
        var repositoryRemoved = (await removed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Contains("acme/web is removed.");
        Assert.NotEqual(intentEdited, repositoryRemoved);
        Assert.Equal(
            intentEdited ? ["acme/web"] : [],
            Listed(await GetAsync(overseer, "/settings")));
        Assert.Equal(
            intentEdited ? [("Import data", "Repositories in scope: acme/web")] : [("Export data", "No Repositories in scope")],
            Backlog(await GetAsync(overseer, "/")));
    }

    /// <summary>A running Overseer, logged in, with these repositories configured.</summary>
    private async Task<RunningOverseer> StartAsync(params string[] repositories) =>
        await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), repositories);

    private static Task<string> GetAsync(RunningOverseer overseer, string address) =>
        overseer.Http.GetStringAsync(address, TestContext.Current.CancellationToken);

    /// <summary>The address the Backlog links the Intent with this first line to, as a click on it goes to.</summary>
    private static async Task<string> AddressAsync(RunningOverseer overseer, string firstLine)
    {
        var backlog = await GetAsync(overseer, "/");
        var link = Regex.Match(backlog, $"<a[^>]*href=\"([^\"]*)\"[^>]*class=\"intent-first-line\"[^>]*>{Regex.Escape(WebUtility.HtmlEncode(firstLine))}</a>");
        return link.Success ? "/" + link.Groups[1].Value : throw new InvalidOperationException($"The Backlog has no link to \"{firstLine}\": {backlog}");
    }

    /// <summary>Opens the Intent's editor, writes this text, ticks exactly these repositories and presses the button with the given value.</summary>
    private static Task<HttpResponseMessage> EditAsync(RunningOverseer overseer, string address, string action, string text, params string[] repositories) =>
        overseer.SubmitFormAsync(address + "/edit", "edit-intent", EditorFields(text, action, repositories));

    /// <summary>Opens the Intent's delete page and confirms.</summary>
    private static Task<HttpResponseMessage> DeleteAsync(RunningOverseer overseer, string address) =>
        overseer.SubmitFormAsync(address + "/delete", "delete-intent", Confirmed);

    /// <summary>What a browser sends for the button that confirms deleting an Intent.</summary>
    private static readonly KeyValuePair<string, string>[] Confirmed = [new("DeleteConfirmation", "confirmed")];

    /// <summary>The text in the Intent's editor, as a browser would read it.</summary>
    private static string Editor(string page) =>
        Regex.Match(page, "<textarea[^>]*name=\"IntentText\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline) is { Success: true } editor
            ? WebUtility.HtmlDecode(editor.Groups[1].Value)
            : throw new InvalidOperationException($"The page has no Intent editor: {page}");

    /// <summary>The HTML of the Intent's preview, without surrounding whitespace.</summary>
    private static string Preview(string page) =>
        Regex.Match(page, "<section[^>]*aria-label=\"Intent preview\"[^>]*>(.*?)</section>", RegexOptions.Singleline) is { Success: true } preview
            ? preview.Groups[1].Value.Trim().ReplaceLineEndings("\n")
            : throw new InvalidOperationException($"The page has no Intent preview: {page}");

    /// <summary>The Backlog's Intents, top to bottom, as a browser would show their first line and Repositories in scope.</summary>
    private static IReadOnlyList<(string FirstLine, string Repositories)> Backlog(string page) =>
        [.. Regex.Matches(page, "<li[^>]*data-intent[^>]*>.*?</li>", RegexOptions.Singleline).Select(intent => (
            ElementText(intent.Value, "intent-first-line"),
            ElementText(intent.Value, "intent-repositories")))];

    /// <summary>The HTML of the Intent's rendered text, without surrounding whitespace.</summary>
    private static string RenderedText(string page) =>
        Regex.Match(page, "<section[^>]*aria-label=\"Intent text\"[^>]*>(.*?)</section>", RegexOptions.Singleline) is { Success: true } text
            ? text.Groups[1].Value.Trim().ReplaceLineEndings("\n")
            : throw new InvalidOperationException($"The page has no rendered Intent text: {page}");
}
