using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Overseer.Intents;
using static Overseer.Tests.RepositorySetupPortal;

namespace Overseer.Tests;

/// <summary>Working with Intents in the Portal, and reading back what its pages show of them.</summary>
public static class IntentPortal
{
    /// <summary>A running Overseer on an empty database, logged in, with these repositories configured.</summary>
    public static async Task<RunningOverseer> StartWithRepositoriesAsync(PostgresServer postgres, params string[] repositories) =>
        await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), repositories);

    /// <summary>A running Overseer, logged in, with these repositories configured.</summary>
    public static async Task<RunningOverseer> StartWithRepositoriesAsync(string connectionString, string[] repositories, TimeProvider? time = null)
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

    /// <summary>Writes an Intent's text on the New Intent page, ticks exactly these repositories and saves it.</summary>
    public static Task<HttpResponseMessage> SaveAsync(RunningOverseer overseer, string text, params string[] repositories) =>
        overseer.SubmitFormAsync("/intents/new", "new-intent", EditorFields(text, "save", repositories));

    /// <summary>What a browser sends for an Intent's editor when the button with the given value is pressed.</summary>
    public static IEnumerable<KeyValuePair<string, string>> EditorFields(string text, string action, params string[] repositories) =>
    [
        new("IntentText", text),
        new("IntentAction", action),
        // A browser sends a ticked checkbox as its value, and an unticked one not at all.
        .. repositories.Select(repository => KeyValuePair.Create("RepositoriesInScope", repository)),
    ];

    /// <summary>Sets an Intent's state directly: the Portal can't take one out of Draft yet.</summary>
    public static async Task SetStateAsync(RunningOverseer overseer, string text, IntentState state)
    {
        await using var database = overseer.OpenDatabase();
        await database.Intents.Where(intent => intent.Text == text)
            .ExecuteUpdateAsync(intent => intent.SetProperty(record => record.State, state), TestContext.Current.CancellationToken);
    }

    /// <summary>The text of the page's alert, as a browser would show it.</summary>
    public static string Alert(string page) =>
        Regex.Match(page, "<p[^>]*role=\"alert\"[^>]*>(.*?)</p>", RegexOptions.Singleline) is { Success: true } alert
            ? WebUtility.HtmlDecode(alert.Groups[1].Value).Trim()
            : throw new InvalidOperationException($"The page has no alert: {page}");

    /// <summary>The Configured Repositories an Intent's editor offers, in its order, and whether each is ticked.</summary>
    public static IReadOnlyList<(string Repository, bool Selected)> Offered(string page) =>
        [.. Regex.Matches(page, "<input[^>]*name=\"RepositoriesInScope\"[^>]*>").Select(checkbox => (
            WebUtility.HtmlDecode(Regex.Match(checkbox.Value, "value=\"([^\"]*)\"").Groups[1].Value),
            Regex.IsMatch(checkbox.Value, @"\schecked\b")))];

    /// <summary>The Backlog's Intents, top to bottom, as a browser would show their first line and Repositories in scope.</summary>
    public static IReadOnlyList<(string FirstLine, string Repositories)> Backlog(string page) =>
        [.. Regex.Matches(page, "<li[^>]*data-intent[^>]*>.*?</li>", RegexOptions.Singleline).Select(intent => (
            ElementText(intent.Value, "intent-first-line"),
            ElementText(intent.Value, "intent-repositories")))];

    /// <summary>The text of the element with the given CSS class, as a browser would show it.</summary>
    public static string ElementText(string html, string cssClass) =>
        Regex.Match(html, $"<[^>]*class=\"{cssClass}\"[^>]*>(.*?)</", RegexOptions.Singleline) is { Success: true } element
            ? WebUtility.HtmlDecode(element.Groups[1].Value).Trim()
            : throw new InvalidOperationException($"No element of class {cssClass} in: {html}");
}
