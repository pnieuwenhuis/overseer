using System.Net;
using System.Text.RegularExpressions;

namespace Overseer.Tests;

/// <summary>Starting Repository Setups and removing Configured Repositories on the Settings page, and reading back what the page shows of them.</summary>
public static class RepositorySetupPortal
{
    /// <summary>The GitHub token the Overseer is started with, which the fake GitHub accepts as octocat.</summary>
    public const string Token = "github_pat_11WORKING0123456789";

    /// <summary>A running Overseer, logged in, with a GitHub token set that GitHub accepts, and the organization acme.</summary>
    public static async Task<RunningOverseer> StartWithTokenAsync(
        PostgresServer postgres, string? connectionString = null, TimeProvider? time = null)
    {
        var overseer = await RunningOverseer.StartAsync(connectionString ?? await postgres.CreateEmptyDatabaseAsync(), time: time);
        await overseer.LogInAsync();
        overseer.GitHub.AcceptToken(Token, "octocat");
        overseer.GitHub.AddOrganization("acme");
        using var saved = await overseer.SubmitFormAsync("/settings", "github-token", new Dictionary<string, string> { ["GitHubToken"] = Token });
        Assert.Contains("The GitHub token is saved.", await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return overseer;
    }

    /// <summary>Fills in the Settings page's new repository form and starts the Repository Setup.</summary>
    public static Task<HttpResponseMessage> StartSetupAsync(
        RunningOverseer overseer, string owner, string name,
        string purpose = "The public website of Acme.", string techStack = "Astro and TypeScript, deployed to Azure Static Web Apps.") =>
        overseer.SubmitFormAsync("/settings", "repository-setup", new Dictionary<string, string>
        {
            ["SetupOwner"] = owner,
            ["SetupName"] = name,
            ["SetupPurpose"] = purpose,
            ["SetupTechStack"] = techStack,
        });

    /// <summary>Presses the Remove button of <paramref name="repository"/> on the Settings page.</summary>
    public static Task<HttpResponseMessage> RemoveAsync(RunningOverseer overseer, string repository) =>
        overseer.SubmitFormAsync("/settings", "repository-remove", new Dictionary<string, string> { ["RemoveRepository"] = repository });

    /// <summary>The Configured Repositories the Settings page lists, in its order.</summary>
    public static IReadOnlyList<string> Listed(string page) =>
        Regex.Match(page, "<ul[^>]*class=\"configured-repositories\"[^>]*>(.*?)</ul>", RegexOptions.Singleline) is { Success: true } list
            ? [.. Regex.Matches(list.Groups[1].Value, "<a[^>]*>(.*?)</a>", RegexOptions.Singleline).Select(match => WebUtility.HtmlDecode(match.Groups[1].Value.Trim()))]
            : [];

    /// <summary>Waits for <paramref name="condition"/> to hold, as the Repository Setup's agent part runs in the background.</summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T?>> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            if (await condition() is { } value)
            {
                return value;
            }
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what}.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The Repository Setups the Settings page shows, latest first.</summary>
    public static IReadOnlyList<ShownSetup> Setups(string page) =>
        [.. Regex.Matches(page, "<section class=\"repository-setup\" data-repository=\"([^\"]+)\" data-phase=\"([^\"]+)\"[^>]*>(.*?)</section>", RegexOptions.Singleline)
            .Select(setup => new ShownSetup(
                WebUtility.HtmlDecode(setup.Groups[1].Value),
                setup.Groups[2].Value,
                WebUtility.HtmlDecode(Regex.Match(setup.Groups[3].Value, "<p class=\"repository-setup-status[^\"]*\"[^>]*>(.*?)</p>", RegexOptions.Singleline).Groups[1].Value.Trim()),
                Regex.Match(setup.Groups[3].Value, "<form[^>]*action=\"([^\"]*retry)\"", RegexOptions.Singleline) is { Success: true } retry ? retry.Groups[1].Value : null))];

    /// <summary>The Repository Setup of <paramref name="repository"/> as the Settings page shows it now.</summary>
    public static async Task<ShownSetup?> SetupAsync(RunningOverseer overseer, string repository) =>
        Setups(await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken))
            .FirstOrDefault(setup => setup.Repository == repository);

    /// <summary>Presses the Retry button of <paramref name="repository"/>'s Repository Setup on the Settings page, as a browser would.</summary>
    public static async Task<HttpResponseMessage> RetryAsync(RunningOverseer overseer, string repository)
    {
        var page = await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken);
        var section = Regex.Match(
            page,
            $"<section class=\"repository-setup\" data-repository=\"{Regex.Escape(repository)}\".*?</section>",
            RegexOptions.Singleline).Value;
        var form = Regex.Match(section, "<form[^>]*action=\"([^\"]*retry)\".*?</form>", RegexOptions.Singleline);
        Assert.True(form.Success, $"The Settings page offers no Retry for {repository}.");
        var token = Regex.Match(form.Value, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        return await overseer.Http.PostAsync(
            WebUtility.HtmlDecode(form.Groups[1].Value),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }),
            TestContext.Current.CancellationToken);
    }

    /// <summary>Waits for the Settings page to show <paramref name="repository"/>'s latest Repository Setup done or failed.</summary>
    public static Task<ShownSetup> FinishedAsync(RunningOverseer overseer, string repository) =>
        EventuallyAsync(async () => await SetupAsync(overseer, repository) is { Phase: "Done" or "Failed" } setup ? setup : null,
            $"the Repository Setup of {repository} to finish");
}

/// <summary>A Repository Setup as the Settings page shows it.</summary>
/// <param name="Phase">Where it is: ProvisioningSandbox, AgentWorking, Done or Failed.</param>
/// <param name="Status">What the page says about it, such as the error it failed with.</param>
/// <param name="RetryAction">Where its Retry button posts to; <c>null</c> when it has none.</param>
public sealed record ShownSetup(string Repository, string Phase, string Status, string? RetryAction);
