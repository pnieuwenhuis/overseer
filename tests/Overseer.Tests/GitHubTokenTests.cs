using System.Text.RegularExpressions;
using Overseer.GitHub;

namespace Overseer.Tests;

public class GitHubTokenTests(PostgresServer postgres)
{
    private const string Token = "github_pat_11WORKING0123456789";
    private const string OtherToken = "github_pat_22OTHER9876543210";

    [Fact]
    public async Task Without_a_GitHub_token_the_Settings_page_says_none_is_set()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        var page = await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken);

        Assert.Equal("No GitHub token is set.", Status(page));
        Assert.DoesNotContain("Remove the GitHub token", page);
        Assert.Null(await StoredTokenCheckAsync(overseer));
    }

    [Fact]
    public async Task A_working_token_is_stored_and_the_page_shows_the_login_it_works_as_never_the_token()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        overseer.GitHub.AcceptToken(Token, "octocat");

        using var response = await SaveAsync(overseer, Token);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The GitHub token is saved.", page);
        Assert.Equal("A GitHub token is set, and works as octocat.", Status(page));
        Assert.DoesNotContain(Token, page);
        Assert.Equal(new GitHubTokenCheck.Works("octocat"), await StoredTokenCheckAsync(overseer));
        var reloaded = await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken);
        Assert.Equal("A GitHub token is set.", Status(reloaded));
        Assert.DoesNotContain(Token, reloaded);
        Assert.Contains("Remove the GitHub token", reloaded);
        Assert.Contains("Check with GitHub", reloaded);
    }

    [Fact]
    public async Task A_working_token_replaces_the_token_in_use()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        overseer.GitHub.AcceptToken(Token, "octocat");
        overseer.GitHub.AcceptToken(OtherToken, "hubot");
        using var first = await SaveAsync(overseer, Token);

        using var response = await SaveAsync(overseer, OtherToken);

        Assert.Equal("A GitHub token is set, and works as hubot.", Status(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(new GitHubTokenCheck.Works("hubot"), await StoredTokenCheckAsync(overseer));
    }

    [Fact]
    public async Task A_token_that_fails_the_check_shows_GitHubs_error_and_leaves_the_token_in_use()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        overseer.GitHub.AcceptToken(Token, "octocat");
        overseer.GitHub.RejectToken(OtherToken, "Bad credentials (HTTP 401)");
        using var first = await SaveAsync(overseer, Token);

        using var response = await SaveAsync(overseer, OtherToken);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Bad credentials (HTTP 401)", page);
        Assert.Contains("Store it anyway", page);
        Assert.DoesNotContain("The GitHub token is saved.", page);
        Assert.DoesNotContain(OtherToken, page);
        Assert.Equal("A GitHub token is set.", Status(page));
        Assert.Equal(new GitHubTokenCheck.Works("octocat"), await StoredTokenCheckAsync(overseer));
    }

    [Fact]
    public async Task A_token_that_fails_the_check_is_stored_once_the_user_confirms_it()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        overseer.GitHub.AcceptToken(Token, "octocat");
        overseer.GitHub.RejectToken(OtherToken, "Bad credentials (HTTP 401)");
        using var first = await SaveAsync(overseer, Token);
        using var failed = await SaveAsync(overseer, OtherToken);

        using var response = await ResolveFailedAsync(overseer, "store");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The GitHub token is saved.", page);
        Assert.Equal("A GitHub token is set, but fails: Bad credentials (HTTP 401)", Status(page));
        Assert.Equal(new GitHubTokenCheck.Fails("Bad credentials (HTTP 401)"), await StoredTokenCheckAsync(overseer));
    }

    [Fact]
    public async Task A_token_that_fails_the_check_waits_for_confirmation_until_the_user_discards_it()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        overseer.GitHub.RejectToken(OtherToken, "Bad credentials (HTTP 401)");
        using var failed = await SaveAsync(overseer, OtherToken);
        Assert.Contains("Store it anyway", await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken));

        using var response = await ResolveFailedAsync(overseer, "discard");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The token is discarded.", page);
        Assert.DoesNotContain("Store it anyway", page);
        Assert.Equal("No GitHub token is set.", Status(page));
        Assert.DoesNotContain("Store it anyway", await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken));
        Assert.Null(await StoredTokenCheckAsync(overseer));
    }

    [Fact]
    public async Task Checking_asks_GitHub_about_the_stored_token()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        overseer.GitHub.AcceptToken(Token, "octocat");
        using var first = await SaveAsync(overseer, Token);
        overseer.GitHub.RejectToken(Token, "Bad credentials (HTTP 401)");

        using var response = await overseer.SubmitFormAsync("/settings", "github-token-check", new Dictionary<string, string>());

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("A GitHub token is set, but fails: Bad credentials (HTTP 401)", Status(page));
        Assert.DoesNotContain(Token, page);
    }

    [Fact]
    public async Task Removing_the_token_leaves_none_set()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        overseer.GitHub.AcceptToken(Token, "octocat");
        using var first = await SaveAsync(overseer, Token);

        using var response = await overseer.SubmitFormAsync("/settings", "github-token-remove", new Dictionary<string, string>());

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The GitHub token is removed.", page);
        Assert.Equal("No GitHub token is set.", Status(page));
        Assert.Null(await StoredTokenCheckAsync(overseer));
    }

    [Fact]
    public async Task What_cannot_be_a_token_is_refused_without_asking_GitHub()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await SaveAsync(overseer, "github_pat_11 with spaces");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("This is not a GitHub token", page);
        Assert.DoesNotContain("Store it anyway", page);
        Assert.Null(await StoredTokenCheckAsync(overseer));
    }

    /// <summary>Enters <paramref name="token"/> in the Settings page's GitHub token field and saves it.</summary>
    private static Task<HttpResponseMessage> SaveAsync(RunningOverseer overseer, string token) =>
        overseer.SubmitFormAsync("/settings", "github-token", new Dictionary<string, string> { ["GitHubToken"] = token });

    /// <summary>Presses "Store it anyway" (<c>store</c>) or "Discard it" (<c>discard</c>) for a token that failed the check.</summary>
    private static Task<HttpResponseMessage> ResolveFailedAsync(RunningOverseer overseer, string action) =>
        overseer.SubmitFormAsync("/settings", "github-token-failed", new Dictionary<string, string> { ["FailedGitHubTokenAction"] = action });

    /// <summary>What the gateway says about the stored GitHub token, which it reads itself.</summary>
    private static Task<GitHubTokenCheck?> StoredTokenCheckAsync(RunningOverseer overseer) =>
        overseer.GitHub.CheckTokenAsync(TestContext.Current.CancellationToken);

    /// <summary>What the Settings page says about the GitHub token.</summary>
    private static string Status(string page) =>
        Regex.Match(page, "<p[^>]*class=\"github-token-status\"[^>]*>(.*?)</p>", RegexOptions.Singleline) is { Success: true } status
            ? Regex.Replace(Regex.Replace(status.Groups[1].Value, "<[^>]+>", ""), @"\s+", " ").Trim()
            : throw new InvalidOperationException("The page shows no GitHub token status.");
}
