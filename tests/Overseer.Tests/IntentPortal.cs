using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Overseer.Intents;
using static Overseer.Tests.RepositorySetupPortal;

namespace Overseer.Tests;

/// <summary>Working with Intents in the Portal, reading back what its pages show of them, and holding up what the server writes.</summary>
public static class IntentPortal
{
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

    /// <summary>The text of the element with the given CSS class, as a browser would show it.</summary>
    public static string ElementText(string html, string cssClass) =>
        Regex.Match(html, $"<[^>]*class=\"{cssClass}\"[^>]*>(.*?)</", RegexOptions.Singleline) is { Success: true } element
            ? WebUtility.HtmlDecode(element.Groups[1].Value).Trim()
            : throw new InvalidOperationException($"No element of class {cssClass} in: {html}");

    /// <summary>
    /// Runs <paramref name="first"/>, held up by <paramref name="tableLock"/>, which lets the table's reads through but not its writes,
    /// then <paramref name="second"/>, which gets the chance to overtake it, and lets both finish.
    /// </summary>
    public static async Task<(HttpResponseMessage First, HttpResponseMessage Second)> RaceAsync(
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
    public static async Task UntilWaitingOnLocksAsync(RunningOverseer overseer, int connections, CancellationToken cancellationToken)
    {
        await using var database = overseer.OpenDatabase();
        while (await database.Database.SqlQuery<int>(
            $"""SELECT count(*)::int AS "Value" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'""")
            .SingleAsync(cancellationToken) < connections)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }
}
