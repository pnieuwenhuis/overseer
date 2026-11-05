using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Overseer.Intents;
using static Overseer.Tests.RepositorySetupPortal;

namespace Overseer.Tests;

public class RepositoriesInScopeTests(PostgresServer postgres)
{
    [Fact]
    public async Task A_new_Intent_offers_every_Configured_Repository_all_selected()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/web", "acme/API");

        var page = await overseer.Http.GetStringAsync("/intents/new", TestContext.Current.CancellationToken);

        Assert.Equal([("acme/API", true), ("acme/web", true)], Offered(page));
    }

    [Fact]
    public async Task The_selected_Repositories_in_scope_show_in_the_Backlog_also_after_a_restart()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web", "acme/API", "acme/docs"]))
        {
            using var response = await SaveAsync(first, "Export data", "acme/web", "acme/API");

            Assert.Equal("/", response.RequestMessage!.RequestUri!.AbsolutePath);
            Assert.Equal([("Export data", "Repositories in scope: acme/API, acme/web")], Backlog(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        }

        await using var second = await RunningOverseer.StartAsync(connectionString);
        await second.LogInAsync();

        var home = await second.Http.GetStringAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal([("Export data", "Repositories in scope: acme/API, acme/web")], Backlog(home));
    }

    [Fact]
    public async Task A_Draft_can_be_saved_with_every_repository_deselected()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/web");

        using var response = await SaveAsync(overseer, "Export data");

        Assert.Equal([("Export data", "No Repositories in scope")], Backlog(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_Draft_can_be_saved_before_any_repository_is_configured()
    {
        await using var overseer = await StartWithRepositoriesAsync();

        var page = await overseer.Http.GetStringAsync("/intents/new", TestContext.Current.CancellationToken);
        using var response = await SaveAsync(overseer, "Export data");

        Assert.Contains("No repositories are configured", page);
        Assert.Equal([("Export data", "No Repositories in scope")], Backlog(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("acme/web")]
    [InlineData]
    public async Task Previewing_keeps_the_selection(params string[] selected)
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/API", "acme/web");

        using var response = await SubmitAsync(overseer, "Export data", "preview", selected);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal([("acme/API", false), ("acme/web", selected.Length > 0)], Offered(page));
    }

    [Fact]
    public async Task A_save_refused_for_its_text_keeps_the_selection_and_saves_nothing()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/API", "acme/web");

        using var response = await SaveAsync(overseer, " ", "acme/web");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("An Intent needs text: write what you want changed.", Alert(page));
        Assert.Equal([("acme/API", false), ("acme/web", true)], Offered(page));
        Assert.Empty(Backlog(await overseer.Http.GetStringAsync("/", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_repository_saved_in_another_case_or_twice_is_in_scope_once_as_it_is_configured()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/web");

        using var response = await SaveAsync(overseer, "Export data", "ACME/WEB", "acme/web");

        Assert.Equal([("Export data", "Repositories in scope: acme/web")], Backlog(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_repository_removed_after_the_form_opened_refuses_the_save_and_keeps_what_the_user_entered()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/API", "acme/docs", "acme/web");
        // The user's form still offers acme/web, as a browser tab opened before this removal does.
        using var removed = await RemoveAsync(overseer, "acme/web");

        using var response = await SaveAsync(overseer, "Export data", "acme/web", "acme/docs");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("/intents/new", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal("acme/web is no longer a Configured Repository. Check the Repositories in scope and save again.", Alert(page));
        Assert.Equal([("acme/API", false), ("acme/docs", true)], Offered(page));
        Assert.Contains(">Export data</textarea>", page);
        Assert.Empty(Backlog(await overseer.Http.GetStringAsync("/", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Several_repositories_removed_after_the_form_opened_are_all_named()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/API", "acme/web");
        using var removedFirst = await RemoveAsync(overseer, "acme/API");
        using var removedSecond = await RemoveAsync(overseer, "acme/web");

        using var response = await SaveAsync(overseer, "Export data", "acme/web", "acme/API");

        Assert.Equal(
            "acme/web and acme/API are no longer Configured Repositories. Check the Repositories in scope and save again.",
            Alert(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Previewing_after_a_selected_repository_was_removed_says_so_and_saves_nothing()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/API", "acme/docs", "acme/web");
        // The user's form still offers acme/web, as a browser tab opened before this removal does.
        using var removed = await RemoveAsync(overseer, "acme/web");

        using var response = await SubmitAsync(overseer, "Export data", "preview", "acme/web", "acme/docs");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("acme/web is no longer a Configured Repository. Check the Repositories in scope and save again.", Alert(page));
        Assert.Equal([("acme/API", false), ("acme/docs", true)], Offered(page));
        Assert.Contains(">Export data</textarea>", page);
        Assert.Empty(Backlog(await overseer.Http.GetStringAsync("/", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Previewing_after_a_repository_that_is_not_selected_was_removed_says_nothing()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/docs", "acme/web");
        using var removed = await RemoveAsync(overseer, "acme/web");

        using var response = await SubmitAsync(overseer, "Export data", "preview", "acme/docs");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("role=\"alert\"", page);
        Assert.Equal([("acme/docs", true)], Offered(page));
    }

    [Fact]
    public async Task Previewing_a_repository_in_another_case_keeps_it_selected_and_says_nothing()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/web");

        using var response = await SubmitAsync(overseer, "Export data", "preview", "ACME/WEB");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("role=\"alert\"", page);
        Assert.Equal([("acme/web", true)], Offered(page));
    }

    [Fact]
    public async Task A_selected_value_that_is_not_a_repository_refuses_the_save_naming_it()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/web");

        using var response = await SaveAsync(overseer, "Export data", "not a repository", "acme/web");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("not a repository is no longer a Configured Repository. Check the Repositories in scope and save again.", Alert(page));
        Assert.Empty(Backlog(await overseer.Http.GetStringAsync("/", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_save_without_text_after_a_selected_repository_was_removed_says_both_and_saves_nothing()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/API", "acme/docs", "acme/web");
        using var removed = await RemoveAsync(overseer, "acme/web");

        using var response = await SaveAsync(overseer, " ", "acme/web", "acme/docs");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            "An Intent needs text: write what you want changed. acme/web is no longer a Configured Repository. Check the Repositories in scope and save again.",
            Alert(page));
        Assert.Equal([("acme/API", false), ("acme/docs", true)], Offered(page));
        Assert.Empty(Backlog(await overseer.Http.GetStringAsync("/", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Removing_a_repository_an_open_Intent_has_in_scope_is_refused_naming_the_Intent()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/API", "acme/web");
        using var saved = await SaveAsync(overseer, "# Export data\n\nAs CSV.", "acme/web");

        using var response = await RemoveAsync(overseer, "acme/web");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("acme/web can't be removed: the open Intent \"Export data\" has it in scope.", Alert(page));
        Assert.DoesNotContain("is removed.", page);
        Assert.Equal(["acme/API", "acme/web"], Listed(await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Removing_a_repository_in_another_case_is_refused_all_the_same()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");

        using var response = await RemoveAsync(overseer, "ACME/WEB");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("acme/web can't be removed: the open Intent \"Export data\" has it in scope.", Alert(page));
        Assert.Equal(["acme/web"], Listed(page));
    }

    [Fact]
    public async Task A_refusal_names_every_open_Intent_with_the_repository_in_scope_newest_first()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/API", "acme/web"], time);
        foreach (var (text, repository) in new[] { ("Oldest", "acme/web"), ("Other repository", "acme/API"), ("Middle", "acme/web"), ("Newest", "acme/web") })
        {
            using var saved = await SaveAsync(overseer, text, repository);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        using var response = await RemoveAsync(overseer, "acme/web");

        Assert.Equal(
            "acme/web can't be removed: the open Intents \"Newest\", \"Middle\" and \"Oldest\" have it in scope.",
            Alert(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_repository_no_open_Intent_has_in_scope_can_be_removed()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/API", "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/API");

        using var response = await RemoveAsync(overseer, "acme/web");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("acme/web is removed.", page);
        Assert.Equal(["acme/API"], Listed(page));
    }

    [Theory]
    [InlineData(IntentState.Done)]
    [InlineData(IntentState.Cancelled)]
    public async Task An_Intent_that_is_Done_or_Cancelled_leaves_the_Backlog_and_does_not_hold_a_repository_back_but_keeps_its_history(IntentState doneOrCancelled)
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/web");
        using var savedNotOpen = await SaveAsync(overseer, "Not open", "acme/web");
        await SetStateAsync(overseer, "Not open", doneOrCancelled);

        Assert.Empty(Backlog(await overseer.Http.GetStringAsync("/", TestContext.Current.CancellationToken)));
        using var response = await RemoveAsync(overseer, "acme/web");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("acme/web is removed.", page);
        Assert.Empty(Listed(page));
        await using var database = overseer.OpenDatabase();
        Assert.Equal(["acme/web"], await database.Intents.SelectMany(intent => intent.RepositoriesInScope).Select(repository => repository.FullName).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_refusal_names_only_the_open_Intents()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/web");
        using var savedNotOpen = await SaveAsync(overseer, "Not open", "acme/web");
        using var savedOpen = await SaveAsync(overseer, "Still open", "acme/web");
        await SetStateAsync(overseer, "Not open", IntentState.Done);

        using var response = await RemoveAsync(overseer, "acme/web");

        Assert.Equal(
            "acme/web can't be removed: the open Intent \"Still open\" has it in scope.",
            Alert(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_capture_under_way_and_a_removal_of_its_repository_cannot_both_succeed()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/web");

        // With inserts into Intents held up, a capture stops short of its commit, however far it has got by then.
        var (saved, removed) = await RaceAsync(overseer, $"""LOCK TABLE "Intents" IN SHARE MODE""",
            () => SaveAsync(overseer, "Export data", "acme/web"),
            () => RemoveAsync(overseer, "acme/web"));

        await AssertExactlyOneSucceededAsync(overseer, saved, removed);
    }

    [Fact]
    public async Task A_removal_under_way_and_a_capture_with_its_repository_in_scope_cannot_both_succeed()
    {
        await using var overseer = await StartWithRepositoriesAsync("acme/web");

        // With deletes from ConfiguredRepositories held up, a removal stops short of its commit, however far it has got by then.
        var (removed, saved) = await RaceAsync(overseer, $"""LOCK TABLE "ConfiguredRepositories" IN SHARE MODE""",
            () => RemoveAsync(overseer, "acme/web"),
            () => SaveAsync(overseer, "Export data", "acme/web"));

        await AssertExactlyOneSucceededAsync(overseer, saved, removed);
    }

    /// <summary>
    /// Runs <paramref name="first"/>, held up by <paramref name="tableLock"/>, which lets the table's reads through but not its writes,
    /// then <paramref name="second"/>, which gets the chance to overtake it, and lets both finish.
    /// </summary>
    private static async Task<(HttpResponseMessage First, HttpResponseMessage Second)> RaceAsync(
        RunningOverseer overseer, FormattableString tableLock, Func<Task<HttpResponseMessage>> first, Func<Task<HttpResponseMessage>> second)
    {
        await using var holder = overseer.OpenDatabase();
        await using var transaction = await holder.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await holder.Database.ExecuteSqlAsync(tableLock, TestContext.Current.CancellationToken);
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watching.CancelAfter(TimeSpan.FromSeconds(30));

        var firstDone = first();
        await Task.WhenAny(firstDone, UntilWaitingOnLocksAsync(overseer, 1, watching.Token));
        var secondDone = second();
        await Task.WhenAny(secondDone, UntilWaitingOnLocksAsync(overseer, 2, watching.Token));
        await watching.CancelAsync();

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        return (await firstDone, await secondDone);
    }

    /// <summary>Returns once this many of the database's connections wait for a lock.</summary>
    private static async Task UntilWaitingOnLocksAsync(RunningOverseer overseer, int connections, CancellationToken cancellationToken)
    {
        await using var database = overseer.OpenDatabase();
        while (await database.Database.SqlQuery<int>(
            $"""SELECT count(*)::int AS "Value" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'""")
            .SingleAsync(cancellationToken) < connections)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private static async Task AssertExactlyOneSucceededAsync(RunningOverseer overseer, HttpResponseMessage saved, HttpResponseMessage removed)
    {
        var intentSaved = saved.RequestMessage!.RequestUri!.AbsolutePath == "/";
        var repositoryRemoved = (await removed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Contains("acme/web is removed.");
        Assert.NotEqual(intentSaved, repositoryRemoved);
        Assert.Equal(
            intentSaved ? ["acme/web"] : [],
            Listed(await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken)));
        Assert.Equal(
            intentSaved ? [("Export data", "Repositories in scope: acme/web")] : [],
            Backlog(await overseer.Http.GetStringAsync("/", TestContext.Current.CancellationToken)));
    }

    /// <summary>Sets an Intent's state directly: the Portal can't make one Done or Cancelled yet.</summary>
    private static async Task SetStateAsync(RunningOverseer overseer, string text, IntentState state)
    {
        await using var database = overseer.OpenDatabase();
        await database.Intents.Where(intent => intent.Text == text)
            .ExecuteUpdateAsync(intent => intent.SetProperty(record => record.State, state), TestContext.Current.CancellationToken);
    }

    /// <summary>Writes an Intent's text on the New Intent page, ticks exactly these repositories and saves it.</summary>
    private static Task<HttpResponseMessage> SaveAsync(RunningOverseer overseer, string text, params string[] repositories) =>
        SubmitAsync(overseer, text, "save", repositories);

    /// <summary>Writes an Intent's text on the New Intent page, ticks exactly these repositories and presses the button with the given value.</summary>
    private static Task<HttpResponseMessage> SubmitAsync(RunningOverseer overseer, string text, string action, params string[] repositories) =>
        overseer.SubmitFormAsync("/intents/new", "new-intent",
        [
            new("IntentText", text),
            new("IntentAction", action),
            // A browser sends a ticked checkbox as its value, and an unticked one not at all.
            .. repositories.Select(repository => KeyValuePair.Create("RepositoriesInScope", repository)),
        ]);

    /// <summary>The text of the page's alert, as a browser would show it.</summary>
    private static string Alert(string page) =>
        Regex.Match(page, "<p[^>]*role=\"alert\"[^>]*>(.*?)</p>", RegexOptions.Singleline) is { Success: true } alert
            ? WebUtility.HtmlDecode(alert.Groups[1].Value).Trim()
            : throw new InvalidOperationException($"The page has no alert: {page}");

    /// <summary>A running Overseer, logged in, with these repositories configured.</summary>
    private async Task<RunningOverseer> StartWithRepositoriesAsync(params string[] repositories) =>
        await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), repositories);

    private static async Task<RunningOverseer> StartWithRepositoriesAsync(string connectionString, string[] repositories, TimeProvider? time = null)
    {
        var overseer = await RunningOverseer.StartAsync(connectionString, time: time);
        await overseer.LogInAsync();
        overseer.GitHub.AcceptToken(Token, "octocat");
        using var saved = await overseer.SubmitFormAsync("/settings", "github-token", new Dictionary<string, string> { ["GitHubToken"] = Token });
        foreach (var repository in repositories)
        {
            overseer.GitHub.AddRepository(repository);
            using var added = await overseer.SubmitFormAsync("/settings", "repository-add", new Dictionary<string, string> { ["Repository"] = repository });
            Assert.Contains($"{repository} is added.", await added.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        return overseer;
    }

    /// <summary>The Configured Repositories the New Intent page offers, in its order, and whether each is ticked.</summary>
    private static IReadOnlyList<(string Repository, bool Selected)> Offered(string page) =>
        [.. Regex.Matches(page, "<input[^>]*name=\"RepositoriesInScope\"[^>]*>").Select(checkbox => (
            WebUtility.HtmlDecode(Regex.Match(checkbox.Value, "value=\"([^\"]*)\"").Groups[1].Value),
            Regex.IsMatch(checkbox.Value, @"\schecked\b")))];

    /// <summary>The Backlog's Intents, top to bottom, as a browser would show their first line and Repositories in scope.</summary>
    private static IReadOnlyList<(string FirstLine, string Repositories)> Backlog(string page) =>
        [.. Regex.Matches(page, "<li[^>]*data-intent[^>]*>.*?</li>", RegexOptions.Singleline).Select(intent => (
            ElementText(intent.Value, "intent-first-line"),
            ElementText(intent.Value, "intent-repositories")))];

    /// <summary>The text of the element with the given CSS class, as a browser would show it.</summary>
    private static string ElementText(string html, string cssClass) =>
        Regex.Match(html, $"<[^>]*class=\"{cssClass}\"[^>]*>(.*?)</", RegexOptions.Singleline) is { Success: true } element
            ? WebUtility.HtmlDecode(element.Groups[1].Value).Trim()
            : throw new InvalidOperationException($"No element of class {cssClass} in: {html}");
}
