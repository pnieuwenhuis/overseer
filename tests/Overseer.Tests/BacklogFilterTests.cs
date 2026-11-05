using System.Text.RegularExpressions;
using Overseer.Intents;
using static Overseer.Tests.IntentPortal;

namespace Overseer.Tests;

public class BacklogFilterTests(PostgresServer postgres)
{
    [Fact]
    public async Task The_Backlog_opens_on_Open_with_every_Intent_not_Done_or_Cancelled_and_Closed_has_the_others()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        foreach (var state in Enum.GetValues<IntentState>())
        {
            using var saved = await SaveAsync(overseer, state.ToString(), "acme/web");
            await SetStateAsync(overseer, state.ToString(), state);
        }

        string[] open = ["Paused", "InReview", "Implementing", "Approved", "AwaitingSpecApproval", "Refining", "Draft"];
        Assert.Equal(open, FirstLines(await GetAsync(overseer, "/")));
        Assert.Equal(open, FirstLines(await GetAsync(overseer, "/?filter=open")));
        Assert.Equal(["Cancelled", "Done"], FirstLines(await GetAsync(overseer, "/?filter=closed")));
    }

    [Fact]
    public async Task The_filter_is_a_link_for_each_choice_marking_the_one_in_the_address()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");

        Assert.Equal("Open", CurrentFilter(await GetAsync(overseer, "/")));
        Assert.Equal("Open", CurrentFilter(await GetAsync(overseer, "/?filter=open")));
        Assert.Equal("Closed", CurrentFilter(await GetAsync(overseer, "/?filter=closed")));

        var page = await GetAsync(overseer, "/");
        Assert.Matches("<a[^>]*href=\"/\\?filter=open\"[^>]*>Open</a>", page);
        Assert.Matches("<a[^>]*href=\"/\\?filter=closed\"[^>]*>Closed</a>", page);
    }

    [Fact]
    public async Task A_filter_the_Portal_does_not_offer_shows_Open()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");

        var page = await GetAsync(overseer, "/?filter=everything");

        Assert.Equal("Open", CurrentFilter(page));
        Assert.Equal(["Export data"], FirstLines(page));
    }

    [Fact]
    public async Task Closed_stays_empty_until_an_Intent_is_Done_or_Cancelled_and_says_so()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");

        var page = await GetAsync(overseer, "/?filter=closed");

        Assert.Empty(FirstLines(page));
        Assert.Contains("No Intent is Done or Cancelled.", page);
        Assert.DoesNotContain("The Backlog is empty.", page);
    }

    private static IReadOnlyList<string> FirstLines(string page) => [.. Backlog(page).Select(intent => intent.FirstLine)];

    /// <summary>The filter the page marks as the current one.</summary>
    private static string CurrentFilter(string page) =>
        Regex.Match(page, "<nav[^>]*aria-label=\"Backlog filter\".*?<a[^>]*aria-current=\"page\"[^>]*>(.*?)</a>", RegexOptions.Singleline) is { Success: true } current
            ? current.Groups[1].Value
            : throw new InvalidOperationException($"The page marks no filter as current: {page}");
}
