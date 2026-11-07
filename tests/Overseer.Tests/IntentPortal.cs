using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Overseer.Intents;
using Overseer.Sandboxes;
using Overseer.Settings;
using static Overseer.Tests.RepositorySetupPortal;

namespace Overseer.Tests;

/// <summary>Working with Intents in the Portal, and reading back what its pages show of them.</summary>
public static class IntentPortal
{
    /// <summary>A running Overseer on an empty database, logged in, with these repositories configured.</summary>
    public static async Task<RunningOverseer> StartWithRepositoriesAsync(PostgresServer postgres, params string[] repositories) =>
        await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), repositories);

    /// <summary>A running Overseer, logged in, with these repositories configured.</summary>
    /// <param name="environment">Overrides the test defaults, as for <see cref="RunningOverseer.StartAsync"/>.</param>
    public static async Task<RunningOverseer> StartWithRepositoriesAsync(
        string connectionString, string[] repositories, TimeProvider? time = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var overseer = await RunningOverseer.StartAsync(connectionString, environment, time: time);
        // A started Intent's Analyst works until the test scripts it otherwise, such as to hand in a Round.
        AnalystKeepsWorking(overseer);
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

    /// <summary>Makes every Analyst session from now on work until it is stopped, as a started Intent's Analyst does while it refines.</summary>
    public static void AnalystKeepsWorking(RunningOverseer overseer) =>
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(FakeAgentTurn.Endless()));

    /// <summary>Saves this maximum of Active Intents on the Settings page, with the other limits at their defaults.</summary>
    public static async Task SetMaxActiveIntentsAsync(RunningOverseer overseer, int maximum)
    {
        using var response = await overseer.SubmitFormAsync("/settings", "operating-limits", new Dictionary<string, string>
        {
            ["MaxActiveIntents"] = maximum.ToString(CultureInfo.InvariantCulture),
            ["RefinementIdleTimeout"] = OperatingLimit.RefinementIdleTimeout.Default.ToString(CultureInfo.InvariantCulture),
            ["VerificationAttemptsPerTask"] = OperatingLimit.VerificationAttemptsPerTask.Default.ToString(CultureInfo.InvariantCulture),
            ["MaxSandboxLifetime"] = OperatingLimit.MaxSandboxLifetime.Default.ToString(CultureInfo.InvariantCulture),
        });
        Assert.Contains("The operating limits are saved.", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
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

    /// <summary>Sets an Intent's state directly, for the states the Portal can't take one to yet.</summary>
    public static async Task SetStateAsync(RunningOverseer overseer, string text, IntentState state)
    {
        await using var database = overseer.OpenDatabase();
        await database.Intents.Where(intent => intent.Text == text)
            .ExecuteUpdateAsync(intent => intent.SetProperty(record => record.State, state), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Moves an Intent to a state as the Intent Lifecycle does, recording the transition at the clock's time, for the transitions the
    /// Portal can't make yet. Like <see cref="SetStateAsync"/> it announces nothing.
    /// </summary>
    public static async Task TransitionAsync(RunningOverseer overseer, string text, IntentState to, TimeProvider time)
    {
        await using var database = overseer.OpenDatabase();
        var intent = await database.Intents.SingleAsync(intent => intent.Text == text, TestContext.Current.CancellationToken);
        database.Set<IntentTransitionRecord>().Add(new IntentTransitionRecord { IntentId = intent.Id, From = intent.State, To = to, At = time.GetUtcNow() });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        await SetStateAsync(overseer, text, to);
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

    public static Task<string> GetAsync(RunningOverseer overseer, string address) =>
        overseer.Http.GetStringAsync(address, TestContext.Current.CancellationToken);

    /// <summary>The address the Backlog links the Intent with this first line to, as a click on it goes to.</summary>
    public static async Task<string> AddressAsync(RunningOverseer overseer, string firstLine)
    {
        var backlog = await GetAsync(overseer, "/");
        var link = Regex.Match(backlog, $"<a[^>]*href=\"([^\"]*)\"[^>]*class=\"intent-first-line\"[^>]*>{Regex.Escape(WebUtility.HtmlEncode(firstLine))}</a>");
        return link.Success ? "/" + link.Groups[1].Value : throw new InvalidOperationException($"The Backlog has no link to \"{firstLine}\": {backlog}");
    }

    /// <summary>Opens the Intent's editor, writes this text, ticks exactly these repositories and presses the button with the given value.</summary>
    public static Task<HttpResponseMessage> EditAsync(RunningOverseer overseer, string address, string action, string text, params string[] repositories) =>
        overseer.SubmitFormAsync(address + "/edit", "edit-intent", EditorFields(text, action, repositories));

    /// <summary>Opens the Intent's detail page and presses Start, whether or not the page offers it.</summary>
    public static Task<HttpResponseMessage> StartAsync(RunningOverseer overseer, string address) =>
        overseer.SubmitFormAsync(address, "start-intent", []);

    /// <summary>Opens the Intent's delete page and confirms.</summary>
    public static Task<HttpResponseMessage> DeleteAsync(RunningOverseer overseer, string address) =>
        overseer.SubmitFormAsync(address + "/delete", "delete-intent", Confirmed);

    /// <summary>What a browser sends for the button that confirms deleting an Intent.</summary>
    public static readonly KeyValuePair<string, string>[] Confirmed = [new("DeleteConfirmation", "confirmed")];

    /// <summary>The text in the Intent's editor, as a browser would read it.</summary>
    public static string Editor(string page) =>
        Regex.Match(page, "<textarea[^>]*name=\"IntentText\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline) is { Success: true } editor
            ? WebUtility.HtmlDecode(editor.Groups[1].Value)
            : throw new InvalidOperationException($"The page has no Intent editor: {page}");

    /// <summary>The HTML of the Intent's preview, without surrounding whitespace.</summary>
    public static string Preview(string page) =>
        Regex.Match(page, "<section[^>]*aria-label=\"Intent preview\"[^>]*>(.*?)</section>", RegexOptions.Singleline) is { Success: true } preview
            ? preview.Groups[1].Value.Trim().ReplaceLineEndings("\n")
            : throw new InvalidOperationException($"The page has no Intent preview: {page}");

    /// <summary>The HTML of the Intent's rendered text, without surrounding whitespace.</summary>
    public static string RenderedText(string page) =>
        Regex.Match(page, "<section[^>]*aria-label=\"Intent text\"[^>]*>(.*?)</section>", RegexOptions.Singleline) is { Success: true } text
            ? text.Groups[1].Value.Trim().ReplaceLineEndings("\n")
            : throw new InvalidOperationException($"The page has no rendered Intent text: {page}");
}
