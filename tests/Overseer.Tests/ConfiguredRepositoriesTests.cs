using System.Net;
using System.Text.RegularExpressions;
using static Overseer.Tests.RepositorySetupPortal;

namespace Overseer.Tests;

public class ConfiguredRepositoriesTests(PostgresServer postgres)
{
    private const string Token = "github_pat_11WORKING0123456789";

    [Fact]
    public async Task A_repository_the_token_can_reach_is_added_as_owner_and_name()
    {
        await using var overseer = await StartWithTokenAsync();
        overseer.GitHub.AddRepository("octocat/Hello-World");

        using var response = await AddAsync(overseer, "octocat/Hello-World");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("octocat/Hello-World is added.", page);
        Assert.Equal(["octocat/Hello-World"], Listed(page));
    }

    [Theory]
    [InlineData("https://github.com/octocat/Hello-World")]
    [InlineData("https://github.com/octocat/Hello-World/")]
    [InlineData("https://github.com/octocat/Hello-World.git")]
    [InlineData("https://github.com/octocat/Hello-World/tree/main/docs?tab=readme#usage")]
    [InlineData("github.com/octocat/Hello-World")]
    [InlineData("git@github.com:octocat/Hello-World.git")]
    [InlineData("  octocat/Hello-World  ")]
    public async Task A_pasted_GitHub_URL_is_added_as_owner_and_name(string input)
    {
        await using var overseer = await StartWithTokenAsync();
        overseer.GitHub.AddRepository("octocat/Hello-World");

        using var response = await AddAsync(overseer, input);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("octocat/Hello-World is added.", page);
        Assert.Equal(["octocat/Hello-World"], Listed(page));
    }

    [Fact]
    public async Task A_repository_is_stored_in_GitHubs_case_whatever_case_the_user_entered()
    {
        await using var overseer = await StartWithTokenAsync();
        overseer.GitHub.AddRepository("octocat/Hello-World");

        using var response = await AddAsync(overseer, "OCTOCAT/hello-world");

        Assert.Equal(["octocat/Hello-World"], Listed(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("Hello-World")]
    [InlineData("octocat/Hello World")]
    [InlineData("octocat/Hello-World/extra")]
    [InlineData("https://gitlab.com/octocat/Hello-World")]
    [InlineData("-octocat/Hello-World")]
    [InlineData("octocat/..")]
    public async Task What_is_not_a_repository_is_refused_without_asking_GitHub(string input)
    {
        await using var overseer = await StartWithTokenAsync();

        using var response = await AddAsync(overseer, input);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Enter a repository as owner/name", page);
        Assert.Empty(Listed(page));
    }

    [Fact]
    public async Task Without_a_GitHub_token_adding_is_refused()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        overseer.GitHub.AddRepository("octocat/Hello-World");

        using var response = await AddAsync(overseer, "octocat/Hello-World");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Set a GitHub token first", page);
        Assert.Contains("No repositories are configured.", page);
        Assert.Empty(Listed(page));
    }

    [Fact]
    public async Task A_repository_that_does_not_exist_is_refused()
    {
        await using var overseer = await StartWithTokenAsync();

        using var response = await AddAsync(overseer, "octocat/no-such-repository");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(
            "GitHub has no repository octocat/no-such-repository that the GitHub token can reach: it doesn&#x27;t exist, or the token has no access to it.",
            page);
        Assert.Empty(Listed(page));
        Assert.Contains("value=\"octocat/no-such-repository\"", page);
    }

    [Fact]
    public async Task A_repository_the_token_cannot_reach_is_refused()
    {
        await using var overseer = await StartWithTokenAsync();
        overseer.GitHub.AddRepository("octocat/private-repository", reachableBy: "github_pat_11SOMEONEELSE");

        using var response = await AddAsync(overseer, "octocat/private-repository");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("GitHub has no repository octocat/private-repository that the GitHub token can reach", page);
        Assert.Empty(Listed(page));
    }

    [Fact]
    public async Task When_GitHub_rejects_the_token_adding_is_refused_with_GitHubs_error()
    {
        await using var overseer = await StartWithTokenAsync();
        overseer.GitHub.AddRepository("octocat/Hello-World");
        overseer.GitHub.RejectToken(Token, "Bad credentials (HTTP 401)");

        using var response = await AddAsync(overseer, "octocat/Hello-World");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("GitHub could not confirm that the GitHub token can reach octocat/Hello-World: Bad credentials (HTTP 401)", page);
        Assert.Empty(Listed(page));
    }

    [Theory]
    [InlineData("octocat/Hello-World")]
    [InlineData("Octocat/hello-world")]
    public async Task A_repository_that_is_already_configured_is_refused(string again)
    {
        await using var overseer = await StartWithTokenAsync();
        overseer.GitHub.AddRepository("octocat/Hello-World");
        using var first = await AddAsync(overseer, "octocat/Hello-World");

        using var response = await AddAsync(overseer, again);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("octocat/Hello-World is already a Configured Repository.", page);
        Assert.DoesNotContain("is added.", page);
        Assert.Equal(["octocat/Hello-World"], Listed(page));
    }

    [Fact]
    public async Task A_repository_GitHub_now_names_in_another_case_is_still_already_configured()
    {
        await using var overseer = await StartWithTokenAsync();
        overseer.GitHub.AddRepository("octocat/Hello-World");
        using var first = await AddAsync(overseer, "octocat/Hello-World");
        overseer.GitHub.AddRepository("Octocat/hello-world");

        using var response = await AddAsync(overseer, "octocat/hello-world");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("is already a Configured Repository.", page);
        Assert.Equal(["octocat/Hello-World"], Listed(page));
    }

    [Fact]
    public async Task Configured_Repositories_are_listed_by_name_and_kept_across_a_restart()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var first = await StartWithTokenAsync(connectionString))
        {
            first.GitHub.AddRepository("octocat/Hello-World");
            first.GitHub.AddRepository("acme/web");
            first.GitHub.AddRepository("acme/API");
            foreach (var repository in new[] { "octocat/Hello-World", "acme/web", "acme/API" })
            {
                using var added = await AddAsync(first, repository);
            }
        }

        await using var second = await RunningOverseer.StartAsync(connectionString);
        await second.LogInAsync();

        var page = await second.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken);
        Assert.Equal(["acme/API", "acme/web", "octocat/Hello-World"], Listed(page));
        Assert.Contains("href=\"https://github.com/acme/API\"", page);
    }

    [Fact]
    public async Task A_Configured_Repository_can_be_removed()
    {
        await using var overseer = await StartWithTokenAsync();
        overseer.GitHub.AddRepository("octocat/Hello-World");
        overseer.GitHub.AddRepository("acme/web");
        using var first = await AddAsync(overseer, "octocat/Hello-World");
        using var second = await AddAsync(overseer, "acme/web");

        using var response = await RemoveAsync(overseer, "octocat/Hello-World");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("octocat/Hello-World is removed.", page);
        Assert.Equal(["acme/web"], Listed(page));
        Assert.Equal(["acme/web"], Listed(await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Removing_a_repository_that_is_no_longer_configured_says_so()
    {
        await using var overseer = await StartWithTokenAsync();
        overseer.GitHub.AddRepository("octocat/Hello-World");
        overseer.GitHub.AddRepository("acme/web");
        using var first = await AddAsync(overseer, "octocat/Hello-World");
        using var second = await AddAsync(overseer, "acme/web");
        using var removed = await RemoveAsync(overseer, "octocat/Hello-World");

        using var response = await RemoveAsync(overseer, "octocat/Hello-World");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("octocat/Hello-World is not a Configured Repository.", page);
        Assert.Equal(["acme/web"], Listed(page));
    }

    /// <summary>A running Overseer, logged in, with a GitHub token set that GitHub accepts.</summary>
    private async Task<RunningOverseer> StartWithTokenAsync(string? connectionString = null)
    {
        var overseer = await RunningOverseer.StartAsync(connectionString ?? await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        overseer.GitHub.AcceptToken(Token, "octocat");
        using var saved = await overseer.SubmitFormAsync("/settings", "github-token", new Dictionary<string, string> { ["GitHubToken"] = Token });
        Assert.Contains("The GitHub token is saved.", await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return overseer;
    }

    /// <summary>Enters <paramref name="repository"/> in the Settings page's repository field and adds it.</summary>
    private static Task<HttpResponseMessage> AddAsync(RunningOverseer overseer, string repository) =>
        overseer.SubmitFormAsync("/settings", "repository-add", new Dictionary<string, string> { ["Repository"] = repository });

    /// <summary>The Configured Repositories the Settings page lists, in its order.</summary>
    private static IReadOnlyList<string> Listed(string page) =>
        Regex.Match(page, "<ul[^>]*class=\"configured-repositories\"[^>]*>(.*?)</ul>", RegexOptions.Singleline) is { Success: true } list
            ? [.. Regex.Matches(list.Groups[1].Value, "<a[^>]*>(.*?)</a>", RegexOptions.Singleline).Select(match => WebUtility.HtmlDecode(match.Groups[1].Value.Trim()))]
            : [];
}
