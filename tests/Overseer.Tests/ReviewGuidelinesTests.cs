using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Overseer.Settings;

namespace Overseer.Tests;

public class ReviewGuidelinesTests(PostgresServer postgres)
{
    [Fact]
    public async Task Without_saved_Review_Guidelines_they_are_empty()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        var page = await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken);

        Assert.Contains("Review Guidelines", page);
        Assert.Equal("", Editor(page));
        Assert.Equal("", await SavedAsync(overseer));
    }

    [Fact]
    public async Task Saved_Review_Guidelines_are_kept_across_a_restart()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var first = await RunningOverseer.StartAsync(connectionString))
        {
            await first.LogInAsync();

            using var response = await SaveAsync(first, Guidelines);

            var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("The Review Guidelines are saved.", page);
            Assert.Equal(Guidelines, Editor(page));
        }

        await using var second = await RunningOverseer.StartAsync(connectionString);
        await second.LogInAsync();

        var settings = await second.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken);
        Assert.Equal(Guidelines, Editor(settings));
        Assert.Equal(RenderedGuidelines, Preview(settings));
        Assert.Equal(Guidelines, await SavedAsync(second));
    }

    [Fact]
    public async Task HTML_in_the_Review_Guidelines_is_previewed_as_text()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        const string guidelines = "Never <script>alert(1)</script> in a view.";

        using var response = await SaveAsync(overseer, guidelines);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("<p>Never &lt;script&gt;alert(1)&lt;/script&gt; in a view.</p>", Preview(page));
        Assert.Equal(guidelines, Editor(page));
        Assert.Equal(guidelines, await SavedAsync(overseer));
    }

    [Fact]
    public async Task Previewing_renders_the_Markdown_without_saving_it()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await SubmitAsync(overseer, Guidelines, "preview");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Guidelines, Editor(page));
        Assert.Equal(RenderedGuidelines, Preview(page));
        Assert.DoesNotContain("The Review Guidelines are saved.", page);
        Assert.Equal("", Editor(await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken)));
        Assert.Equal("", await SavedAsync(overseer));
    }

    [Fact]
    public async Task Saving_only_whitespace_leaves_the_Review_Guidelines_empty()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        using var saved = await SaveAsync(overseer, Guidelines);

        using var response = await SaveAsync(overseer, " \n\n  ");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The Review Guidelines are saved.", page);
        Assert.Equal("", Editor(page));
        Assert.Equal("", await SavedAsync(overseer));
    }

    private const string Guidelines = """
        # Every review

        - Every public method has a test.
        - No `TODO` without a ticket.
        """;

    private const string RenderedGuidelines = """
        <h1>Every review</h1>
        <ul>
        <li>Every public method has a test.</li>
        <li>No <code>TODO</code> without a ticket.</li>
        </ul>
        """;

    /// <summary>Writes the Review Guidelines in the Settings page's editor and saves them.</summary>
    private static Task<HttpResponseMessage> SaveAsync(RunningOverseer overseer, string guidelines) =>
        SubmitAsync(overseer, guidelines, "save");

    /// <summary>Writes the Review Guidelines in the Settings page's editor and presses the button with the given value.</summary>
    private static Task<HttpResponseMessage> SubmitAsync(RunningOverseer overseer, string guidelines, string action) =>
        overseer.SubmitFormAsync("/settings", "review-guidelines", new Dictionary<string, string>
        {
            // A browser sends a textarea's line breaks as CR LF.
            ["ReviewGuidelines"] = guidelines.ReplaceLineEndings("\r\n"),
            ["ReviewGuidelinesAction"] = action,
        });

    /// <summary>The Review Guidelines as server-side code reads them.</summary>
    private static Task<string> SavedAsync(RunningOverseer overseer) =>
        overseer.Services.GetRequiredService<SettingsStore>().GetReviewGuidelinesAsync(TestContext.Current.CancellationToken);

    /// <summary>The HTML of the Review Guidelines preview, without surrounding whitespace.</summary>
    private static string Preview(string page) =>
        Regex.Match(page, "<section[^>]*aria-label=\"Review Guidelines preview\"[^>]*>(.*?)</section>", RegexOptions.Singleline) is { Success: true } preview
            ? preview.Groups[1].Value.Trim().ReplaceLineEndings("\n")
            : throw new InvalidOperationException("The page has no Review Guidelines preview.");

    /// <summary>The text in the Review Guidelines editor, as a browser would read it.</summary>
    private static string Editor(string page) =>
        Regex.Match(page, "<textarea[^>]*name=\"ReviewGuidelines\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline) is { Success: true } editor
            ? WebUtility.HtmlDecode(editor.Groups[1].Value)
            : throw new InvalidOperationException("The page has no Review Guidelines editor.");
}
