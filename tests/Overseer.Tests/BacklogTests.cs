using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using Overseer.Intents;
using static Overseer.Tests.IntentPortal;

namespace Overseer.Tests;

public class BacklogTests(PostgresServer postgres)
{
    [Fact]
    public async Task The_Home_page_shows_an_empty_Backlog_and_offers_a_new_Intent()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        var home = await overseer.Http.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Matches("<h1[^>]*>Backlog</h1>", home);
        Assert.Contains("The Backlog is empty.", home);
        Assert.Matches("<a[^>]*href=\"intents/new\"[^>]*>New Intent</a>", home);
    }

    [Fact]
    public async Task A_saved_Intent_is_a_Draft_in_the_Backlog_also_after_a_restart()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var first = await RunningOverseer.StartAsync(connectionString))
        {
            await first.LogInAsync();

            using var response = await SaveAsync(first, "Let users export their data\n\nAs CSV, from the account page.");

            // Saving returns to the Backlog, so reloading the page doesn't save the Intent again.
            Assert.Equal("/", response.RequestMessage!.RequestUri!.AbsolutePath);
            var backlog = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal([("Let users export their data", "Draft")], Backlog(backlog));
            Assert.DoesNotContain("The Backlog is empty.", backlog);
        }

        await using var second = await RunningOverseer.StartAsync(connectionString);
        await second.LogInAsync();

        var home = await second.Http.GetStringAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal([("Let users export their data", "Draft")], Backlog(home));
    }

    [Theory]
    [InlineData("# Export data\n\nAs CSV.", "Export data")]
    [InlineData("###   Export data", "Export data")]
    [InlineData("  ## Export data ##", "Export data")]
    [InlineData("# Port it to C#", "Port it to C#")]
    [InlineData("#hashtags are no heading", "#hashtags are no heading")]
    [InlineData("####### seven is no heading", "####### seven is no heading")]
    [InlineData("\n\n  Export data  \nAs CSV.", "Export data")]
    [InlineData("#\n\nExport data", "Export data")]
    [InlineData("## ##\n\nExport data", "Export data")]
    [InlineData("#", "#")]
    [InlineData("#\n##", "#")]
    public async Task The_Backlog_lists_an_Intent_by_the_first_line_of_its_text_without_heading_markers(string text, string firstLine)
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await SaveAsync(overseer, text);

        var backlog = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal([(firstLine, "Draft")], Backlog(backlog));
    }

    [Fact]
    public async Task HTML_in_the_first_line_shows_as_text_in_the_Backlog()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await SaveAsync(overseer, "Remove <script>alert(1)</script> from the view");

        var backlog = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("<script>alert(1)", backlog);
        Assert.Equal([("Remove <script>alert(1)</script> from the view", "Draft")], Backlog(backlog));
    }

    [Fact]
    public async Task The_Backlog_lists_the_newest_Intent_first()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync(), time: time);
        await overseer.LogInAsync();
        using var older = await SaveAsync(overseer, "Older");
        time.Advance(TimeSpan.FromMinutes(1));

        using var response = await SaveAsync(overseer, "Newer");

        var backlog = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal([("Newer", "Draft"), ("Older", "Draft")], Backlog(backlog));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\n  ")]
    public async Task An_Intent_without_text_is_refused_and_nothing_is_saved(string text)
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await SaveAsync(overseer, text);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("/intents/new", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("An Intent needs text", page);
        Assert.Empty(Backlog(await overseer.Http.GetStringAsync("/", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Previewing_renders_the_Markdown_keeps_the_text_in_the_editor_and_saves_nothing()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await SubmitAsync(overseer, "# Export data\n\n- As ~~XML~~ CSV", "preview");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("""
            <h1>Export data</h1>
            <ul>
            <li>As <del>XML</del> CSV</li>
            </ul>
            """, Preview(page));
        Assert.Equal("# Export data\n\n- As ~~XML~~ CSV", Editor(page));
        Assert.Empty(Backlog(await overseer.Http.GetStringAsync("/", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task HTML_in_an_Intent_is_previewed_as_text()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await SubmitAsync(overseer, "Remove <script>alert(1)</script> from the view", "preview");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("<p>Remove &lt;script&gt;alert(1)&lt;/script&gt; from the view</p>", Preview(page));
    }

    [Fact]
    public async Task Previewing_no_text_is_not_refused()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await SubmitAsync(overseer, "", "preview");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Nothing to preview.", Preview(page));
        Assert.DoesNotContain("An Intent needs text", page);
    }

    [Theory]
    [InlineData(IntentState.AwaitingSpecApproval, "Awaiting Spec Approval")]
    [InlineData(IntentState.InReview, "In Review")]
    public async Task The_Backlog_shows_a_state_as_the_glossary_words_it(IntentState state, string shownAs)
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        using var saved = await SaveAsync(overseer, "Export data");
        await SetStateAsync(overseer, "Export data", state);

        Assert.Equal([("Export data", shownAs)], Backlog(await GetAsync(overseer, "/")));
    }

    /// <summary>Writes an Intent's text in the New Intent page's editor and saves it.</summary>
    private static Task<HttpResponseMessage> SaveAsync(RunningOverseer overseer, string text) =>
        SubmitAsync(overseer, text, "save");

    /// <summary>Writes an Intent's text in the New Intent page's editor and presses the button with the given value.</summary>
    private static Task<HttpResponseMessage> SubmitAsync(RunningOverseer overseer, string text, string action) =>
        overseer.SubmitFormAsync("/intents/new", "new-intent", new Dictionary<string, string>
        {
            // A browser sends a textarea's line breaks as CR LF.
            ["IntentText"] = text.ReplaceLineEndings("\r\n"),
            ["IntentAction"] = action,
        });

    /// <summary>The Backlog's Intents, top to bottom, as a browser would show their first line and state.</summary>
    private static IReadOnlyList<(string FirstLine, string State)> Backlog(string page) =>
        [.. Regex.Matches(page, "<li[^>]*data-intent[^>]*>.*?</li>", RegexOptions.Singleline).Select(intent => (
            ElementText(intent.Value, "intent-first-line"),
            ElementText(intent.Value, "intent-state")))];

    /// <summary>The text of the element with the given CSS class, as a browser would show it.</summary>
    private static string ElementText(string html, string cssClass) =>
        Regex.Match(html, $"<[^>]*class=\"{cssClass}\"[^>]*>(.*?)</", RegexOptions.Singleline) is { Success: true } element
            ? WebUtility.HtmlDecode(element.Groups[1].Value)
            : throw new InvalidOperationException($"No element of class {cssClass} in: {html}");
}
