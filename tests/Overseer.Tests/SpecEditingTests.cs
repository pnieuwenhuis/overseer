using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Overseer.Intents;
using Overseer.Sandboxes;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.SpecTests;

namespace Overseer.Tests;

/// <summary>The user edits the Spec awaiting their approval, and reorders its Tasks, each save a new Spec version.</summary>
public class SpecEditingTests(PostgresServer postgres)
{
    [Fact]
    public async Task Saving_an_edit_makes_a_new_version_edited_by_the_user_and_the_Analysts_version_stays_readable()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var address = await AwaitingApprovalOfAsync(overseer, Spec(), "acme/web");
        var form = await SpecForm.OpenAsync(overseer, address);

        form.Set("Summary", "Orders can be exported as **Excel**.");
        var page = await form.SubmitAsync("save");

        Assert.Equal(address, "/" + page.Address);
        Assert.Equal(2, SpecVersionOn(page.Html));
        Assert.Equal("Edited by you", ElementText(page.Html, "spec-written-by"));
        Assert.Equal("<p>Orders can be exported as <strong>Excel</strong>.</p>", SpecMarkdown(page.Html, "Summary"));
        Assert.Equal("Awaiting Spec Approval", ElementText(page.Html, "intent-state"));
        Assert.Equal([(1, "written by the Analyst"), (2, "edited by you")], VersionsOn(page.Html));
        var first = await GetAsync(overseer, VersionAddress(page.Html, 1));
        Assert.Equal(1, SpecVersionOn(first));
        Assert.Equal("Written by the Analyst", ElementText(first, "spec-written-by"));
        Assert.Equal("<p>Orders can be exported as <strong>CSV</strong>.</p>", SpecMarkdown(first, "Summary"));
    }

    [Fact]
    public async Task The_user_edits_every_prose_section_and_each_Tasks_title_description_role_and_repositories_and_adds_and_removes_Tasks()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api");
        var spec = Spec();
        spec["uiChanges"] = "An Export button.";
        spec["tasks"] = new JsonArray(
            TaskJson("Add the export endpoint", "Stream every order.", ["acme/web"]),
            TaskJson("Add the button", "Above the table.", ["acme/web"]));
        var address = await AwaitingApprovalOfAsync(overseer, spec, "acme/web", "acme/api");
        var form = await SpecForm.OpenAsync(overseer, address);

        form.Set("Approach", "A *background job* writes the file.");
        form.Set("AcceptanceCriteria", "- The file arrives by email");
        form.Set("Dependencies", "A mail server.");
        form.Set("DataModelChanges", "Orders get an `exported_at` column.");
        form.Set("UiChanges", "");
        form.Set("Task.1.Title", "Add the export job");
        form.Set("Task.1.Description", "Write every order to a file.\r\n\r\n- as CSV");
        form.Set("Task.1.Role", "Designer");
        form.Set("Task.1.Repositories", "acme/web", "acme/api");
        await form.SubmitAsync("remove:2");
        await form.SubmitAsync("add");
        form.Set("Task.2.Title", "Email the file");
        form.Set("Task.2.Description", "Send it to the user.");
        form.Set("Task.2.Repositories", "acme/api");
        var page = (await form.SubmitAsync("save")).Html;

        Assert.Equal(2, SpecVersionOn(page));
        Assert.Equal(
            ["Summary", "Approach", "Decisions", "Acceptance criteria", "Dependencies", "Data model changes", "Tasks"],
            SpecSections(page));
        Assert.Equal("<p>Orders can be exported as <strong>CSV</strong>.</p>", SpecMarkdown(page, "Summary"));
        Assert.Equal("<p>A <em>background job</em> writes the file.</p>", SpecMarkdown(page, "Approach"));
        Assert.Equal("<ul>\n<li>The file arrives by email</li>\n</ul>", SpecMarkdown(page, "Acceptance criteria"));
        Assert.Equal("<p>A mail server.</p>", SpecMarkdown(page, "Dependencies"));
        Assert.Equal("<p>Orders get an <code>exported_at</code> column.</p>", SpecMarkdown(page, "Data model changes"));
        // The decisions are those of the version edited.
        Assert.Equal([("<p>The export is <em>CSV</em>.</p>", true), ("<p>It holds every order.</p>", false)], Decisions(page));
        Assert.Equal(
            [
                // In the order the editor offers them, the Intent's, as a browser sends ticked checkboxes.
                new ShownTask(1, "Add the export job", "Designer", "acme/api, acme/web", "<p>Write every order to a file.</p>\n<ul>\n<li>as CSV</li>\n</ul>"),
                new ShownTask(2, "Email the file", "Developer", "acme/api", "<p>Send it to the user.</p>"),
            ],
            Tasks(page));
    }

    [Fact]
    public async Task The_user_moves_Tasks_up_and_down_and_saving_keeps_that_order()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var spec = Spec();
        spec["tasks"] = new JsonArray(
            TaskJson("First", "One.", ["acme/web"]),
            TaskJson("Second", "Two.", ["acme/web"]),
            TaskJson("Third", "Three.", ["acme/web"], role: "Designer"));
        var address = await AwaitingApprovalOfAsync(overseer, spec, "acme/web");
        var form = await SpecForm.OpenAsync(overseer, address);

        var moved = await form.SubmitAsync("up:3");
        Assert.Equal(["First", "Third", "Second"], form.Get("Task").Select(number => form.Get($"Task.{number}.Title").Single()));
        // Moving only changes the form: nothing is saved until the user saves.
        Assert.Equal(1, SpecVersionOn(await GetAsync(overseer, address)));
        await form.SubmitAsync("down:1");
        var page = (await form.SubmitAsync("save")).Html;

        Assert.Equal(2, SpecVersionOn(page));
        Assert.Equal(
            [
                new ShownTask(1, "Third", "Designer", "acme/web", "<p>Three.</p>"),
                new ShownTask(2, "First", "Developer", "acme/web", "<p>One.</p>"),
                new ShownTask(3, "Second", "Developer", "acme/web", "<p>Two.</p>"),
            ],
            Tasks(page));
        Assert.Equal(["First", "Second", "Third"], Tasks(await GetAsync(overseer, VersionAddress(page, 1))).Select(task => task.Title));
    }

    [Fact]
    public async Task An_edit_that_isnt_a_valid_Spec_is_refused_with_every_reason_keeping_what_the_user_entered_and_no_version_is_made()
    {
        // acme/api is configured, but not in this Intent's scope.
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api");
        var address = await AwaitingApprovalOfAsync(overseer, Spec(), "acme/web");
        var form = await SpecForm.OpenAsync(overseer, address);

        form.Set("Summary", " ");
        form.Set("Approach", "Changed.");
        form.Set("Task.1.Repositories");
        await form.SubmitAsync("add");
        form.Set("Task.2.Description", "No title.");
        form.Set("Task.2.Role", "Reviewer");
        form.Set("Task.2.Repositories", "acme/api");
        var refused = await form.SubmitAsync("save");

        Assert.Equal(address + "/spec/edit", "/" + refused.Address);
        Assert.Equal(
            [
                "The Spec has no \"summary\": it must be Markdown text that isn't empty.",
                "Task 1 touches no repository: \"repositories\" must list at least one of the Repositories in scope, acme/web.",
                "Task 2 has no \"title\": it must be text that isn't empty.",
                "Task 2 has the role \"Reviewer\": it must be \"Designer\" or \"Developer\".",
                "Task 2 touches acme/api, which isn't a Repository in scope: those are acme/web.",
            ],
            RefusalOn(refused.Html));
        Assert.Equal(["Changed."], form.Get("Approach"));
        Assert.Equal(["No title."], form.Get("Task.2.Description"));
        var detail = await GetAsync(overseer, address);
        Assert.Equal(1, SpecVersionOn(detail));
        Assert.Empty(VersionsOn(detail));
        Assert.Equal("Awaiting Spec Approval", ElementText(detail, "intent-state"));
    }

    [Fact]
    public async Task An_edit_that_drops_the_Task_creating_a_missing_Repository_Manifest_is_refused_as_the_Analysts_Spec_would_be()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        WithoutManifest(overseer, "acme/web");
        var spec = Spec();
        spec["tasks"] = new JsonArray(
            TaskJson("Create .overseer.yml", "With the test command.", ["acme/web"]),
            TaskJson("Add the export endpoint", "Stream every order.", ["acme/web"]));
        var address = await AwaitingApprovalOfAsync(overseer, spec, "acme/web");
        var form = await SpecForm.OpenAsync(overseer, address);

        await form.SubmitAsync("remove:1");
        var refused = await form.SubmitAsync("save");

        Assert.Equal(
            ["No Task creates the Repository Manifest of acme/web, which has none on its default branch: add a Task that touches it and creates its `.overseer.yml`, naming that file in its title or description."],
            RefusalOn(refused.Html));
        Assert.Equal(1, SpecVersionOn(await GetAsync(overseer, address)));
    }

    [Theory]
    [InlineData(IntentState.Refining, "Refining")]
    [InlineData(IntentState.Approved, "Approved")]
    [InlineData(IntentState.Paused, "Paused")]
    [InlineData(IntentState.Cancelled, "Cancelled")]
    public async Task Editing_is_refused_once_the_Intent_no_longer_awaits_Spec_approval_also_from_a_page_opened_before(IntentState state, string shownAs)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var address = await AwaitingApprovalOfAsync(overseer, Spec(), "acme/web");
        var form = await SpecForm.OpenAsync(overseer, address);
        await SetStateAsync(overseer, "Export data", state);

        form.Set("Summary", "Changed.");
        var refused = await form.SubmitAsync("save");

        Assert.Equal([$"This Intent is {shownAs}, and only a Spec awaiting approval can be edited."], RefusalOn(refused.Html));
        var detail = await GetAsync(overseer, address);
        Assert.Equal(1, SpecVersionOn(detail));
        Assert.Null(EditLinkOn(detail));
        var editor = await GetAsync(overseer, address + "/spec/edit");
        Assert.Null(SpecForm.FieldsOf(editor));
        Assert.Equal($"This Intent is {shownAs}, and only a Spec awaiting approval can be edited.", Alert(editor));
    }

    [Fact]
    public async Task A_Draft_has_no_Spec_to_edit_even_when_the_editor_is_posted_to_directly()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var response = await overseer.SubmitFormAsync(address + "/spec/edit", SpecForm.Name,
        [
            new("BasedOn", "0"), new("Summary", "Mine."), new("Approach", "Mine."), new("AcceptanceCriteria", "Mine."), new("Dependencies", "None."),
            new("Task", "1"), new("Task.1.Title", "Mine"), new("Task.1.Description", "Mine."), new("Task.1.Role", "Developer"),
            new("Task.1.Repositories", "acme/web"), new("SpecAction", "save"),
        ]);

        Assert.Equal(["This Intent is Draft, and only a Spec awaiting approval can be edited."], RefusalOn(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Null(SpecVersionOn(await GetAsync(overseer, address)));
    }

    [Fact]
    public async Task Of_two_saves_based_on_the_same_version_the_second_is_refused_and_shows_the_newer_version_keeping_the_users_changes()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var address = await AwaitingApprovalOfAsync(overseer, Spec(), "acme/web");
        var first = await SpecForm.OpenAsync(overseer, address);
        var second = await SpecForm.OpenAsync(overseer, address);
        first.Set("Summary", "From the first tab.");
        second.Set("Approach", "From the second tab.");

        await first.SubmitAsync("save");
        var refused = await second.SubmitAsync("save");

        Assert.StartsWith("Your changes were not saved: version 2 of the Spec was saved meanwhile", Alert(refused.Html));
        Assert.Equal(2, SpecVersionOn(refused.Html));
        Assert.Equal("<p>From the first tab.</p>", SpecMarkdown(refused.Html, "Summary"));
        Assert.Equal(["From the second tab."], second.Get("Approach"));
        Assert.Equal(["2"], second.Get("BasedOn"));
        var detail = await GetAsync(overseer, address);
        Assert.Equal([(1, "written by the Analyst"), (2, "edited by you")], VersionsOn(detail));

        // Once seen, the user can save their changes on top of the newer version.
        var saved = (await second.SubmitAsync("save")).Html;
        Assert.Equal(3, SpecVersionOn(saved));
        Assert.Equal("<p>Orders can be exported as <strong>CSV</strong>.</p>", SpecMarkdown(saved, "Summary"));
        Assert.Equal("<p>From the second tab.</p>", SpecMarkdown(saved, "Approach"));
    }

    [Fact]
    public async Task The_detail_page_offers_to_edit_the_Spec_awaiting_approval()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var address = await AwaitingApprovalOfAsync(overseer, Spec(), "acme/web");

        var detail = await GetAsync(overseer, address);

        Assert.Equal(address + "/spec/edit", EditLinkOn(detail));
        Assert.Equal("Written by the Analyst", ElementText(detail, "spec-written-by"));
        // The editor starts from the latest version.
        var form = await SpecForm.OpenAsync(overseer, address);
        Assert.Equal(["1"], form.Get("BasedOn"));
        Assert.Equal(["Orders can be exported as **CSV**."], form.Get("Summary"));
        Assert.Equal(["1"], form.Get("Task"));
        Assert.Equal(["Add the export endpoint"], form.Get("Task.1.Title"));
        Assert.Equal(["Developer"], form.Get("Task.1.Role"));
        Assert.Equal(["acme/web"], form.Get("Task.1.Repositories"));
    }

    /// <summary>The reasons the page gives for refusing to save the Spec, in order; empty when it gives none.</summary>
    private static IReadOnlyList<string> RefusalOn(string page) =>
        Regex.Match(page, "<ul class=\"spec-editor-errors\">(.*?)</ul>", RegexOptions.Singleline) is { Success: true } reasons
            ? [.. Regex.Matches(reasons.Groups[1].Value, "<li>(.*?)</li>", RegexOptions.Singleline).Select(reason => WebUtility.HtmlDecode(reason.Groups[1].Value).Trim())]
            : [];

    /// <summary>The address the page's Edit button for the Spec leads to; <c>null</c> when it has none.</summary>
    private static string? EditLinkOn(string page) =>
        Regex.Match(page, "<a href=\"([^\"]*/spec/edit)\"") is { Success: true } link ? "/" + WebUtility.HtmlDecode(link.Groups[1].Value) : null;

    /// <summary>Has the Analyst hand in <paramref name="spec"/> for a new Intent with these repositories in scope, and gives its address once it awaits approval.</summary>
    private static async Task<string> AwaitingApprovalOfAsync(RunningOverseer overseer, JsonObject spec, params string[] repositories)
    {
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(spec)));
        using var saved = await SaveAsync(overseer, "Export data", repositories);
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingSpecApprovalAsync(overseer, address);
        return address;
    }

    /// <summary>The Spec versions the page lists, in order, each with who wrote it as the page says.</summary>
    private static IReadOnlyList<(int Number, string WrittenBy)> VersionsOn(string page) =>
        [.. Regex.Matches(page, "<li class=\"spec-versions-entry\" data-version=\"(\\d+)\"[^>]*>(.*?)</li>", RegexOptions.Singleline).Select(entry => (
            int.Parse(entry.Groups[1].Value),
            ElementText(entry.Groups[2].Value, "spec-versions-author")))];

    /// <summary>The address the page links version <paramref name="number"/> of the Spec to.</summary>
    private static string VersionAddress(string page, int number) =>
        Regex.Match(page, $"<li class=\"spec-versions-entry\" data-version=\"{number}\"[^>]*>.*?<a[^>]*href=\"([^\"]*)\"", RegexOptions.Singleline) is { Success: true } link
            ? "/" + WebUtility.HtmlDecode(link.Groups[1].Value)
            : throw new InvalidOperationException($"The page links no version {number} of the Spec: {page}");
}

/// <summary>
/// The Spec's editor as a browser tab shows it: its fields as the page filled them in, which a test changes and submits with one of
/// its buttons, as a browser would.
/// </summary>
internal sealed class SpecForm
{
    /// <summary>The editor's form name.</summary>
    public const string Name = "edit-spec";

    private readonly PortalTab _tab;
    private List<KeyValuePair<string, string>> _fields;

    private SpecForm(PortalTab tab, List<KeyValuePair<string, string>> fields)
    {
        _tab = tab;
        _fields = fields;
    }

    /// <summary>Opens the editor of the Spec of the Intent at <paramref name="address"/>.</summary>
    public static async Task<SpecForm> OpenAsync(RunningOverseer overseer, string address)
    {
        var tab = await overseer.OpenAsync(address + "/spec/edit");
        var page = await GetAsync(overseer, address + "/spec/edit");
        return new SpecForm(tab, FieldsOf(page) ?? throw new InvalidOperationException($"The page has no Spec editor: {page}"));
    }

    /// <summary>The values of a field, in order.</summary>
    public IReadOnlyList<string> Get(string name) => [.. _fields.Where(field => field.Key == name).Select(field => field.Value)];

    /// <summary>Gives a field these values, in place of those it had, as typing into it, choosing from it or ticking its checkboxes would.</summary>
    public void Set(string name, params string[] values)
    {
        var at = _fields.FindIndex(field => field.Key == name);
        _fields.RemoveAll(field => field.Key == name);
        _fields.InsertRange(at < 0 ? _fields.Count : at, values.Select(value => KeyValuePair.Create(name, value)));
    }

    /// <summary>
    /// Presses the button with this value, and gives the page the browser then shows. When that page has the editor again, as after
    /// moving a Task or a refused save, this form holds its fields from then on.
    /// </summary>
    public async Task<ShownPage> SubmitAsync(string action)
    {
        using var response = await _tab.SubmitFormAsync(Name, [.. _fields, new("SpecAction", action)]);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, html);
        if (FieldsOf(html) is { } fields)
        {
            _fields = fields;
        }
        return new ShownPage(response.FinalPath().TrimStart('/'), html);
    }

    /// <summary>The fields a browser would send for the page's Spec editor, without its buttons; <c>null</c> when it has none.</summary>
    public static List<KeyValuePair<string, string>>? FieldsOf(string page)
    {
        if (Regex.Match(page, $"<form[^>]*>(?:(?!</form>).)*?name=\"_handler\" value=\"{Name}\".*?</form>", RegexOptions.Singleline) is not { Success: true } form)
        {
            return null;
        }
        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match control in Regex.Matches(form.Value, "<input[^>]*>|<textarea[^>]*>.*?</textarea>|<select[^>]*>.*?</select>", RegexOptions.Singleline))
        {
            var tag = control.Value;
            var name = Attribute(tag, "name");
            if (name is null or "_handler" or "__RequestVerificationToken")
            {
                continue;
            }
            if (tag.StartsWith("<textarea", StringComparison.Ordinal))
            {
                fields.Add(new(name, WebUtility.HtmlDecode(Regex.Match(tag, "<textarea[^>]*>(.*?)</textarea>", RegexOptions.Singleline).Groups[1].Value)));
            }
            else if (tag.StartsWith("<select", StringComparison.Ordinal))
            {
                var selected = Regex.Match(tag, "<option[^>]*\\sselected[^>]*>", RegexOptions.Singleline);
                fields.Add(new(name, selected.Success ? Attribute(selected.Value, "value") ?? "" : ""));
            }
            else if (Attribute(tag, "type") is not "checkbox" || Regex.IsMatch(tag, @"\schecked\b"))
            {
                fields.Add(new(name, Attribute(tag, "value") ?? ""));
            }
        }
        return fields;
    }

    private static string? Attribute(string tag, string attribute) =>
        Regex.Match(tag, $"\\s{attribute}=\"([^\"]*)\"") is { Success: true } value ? WebUtility.HtmlDecode(value.Groups[1].Value) : null;
}

/// <summary>A page a browser shows, at its address relative to the Portal's root.</summary>
internal sealed record ShownPage(string Address, string Html);
