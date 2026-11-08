using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Overseer.Intents;
using Overseer.Sandboxes;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;
using static Overseer.Tests.RoundsTests;

namespace Overseer.Tests;

/// <summary>The Analyst writes the Spec: once it has enough, it hands in a Spec instead of a Round, which then awaits the human's approval.</summary>
public class SpecTests(PostgresServer postgres)
{
    [Fact]
    public async Task A_turn_ending_with_a_valid_Spec_stores_it_as_version_1_and_the_Intent_awaits_Spec_approval_with_an_orange_light()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(
            HandsInRound(("Which format?", "CSV.")),
            HandsInSpec(Spec())));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingAnswersAsync(overseer, address);

        using var answered = await AnswerAsync(overseer, address, 1, ("1", AsSuggested));

        var page = await AwaitingSpecApprovalAsync(overseer, address);
        Assert.Equal(1, SpecVersionOn(page));
        Assert.Equal(("Orange", "Needs Attention"), LightOn(page));
        var backlog = await GetAsync(overseer, "/");
        Assert.Equal("Awaiting Spec Approval", ElementText(backlog, "intent-state"));
        Assert.Equal(("Orange", "Needs Attention"), LightOn(backlog));
        // The transcript stays readable alongside the Spec.
        Assert.Equal([new ShownRound(1, Open: false, [new ShownQuestion(1, "Which format?", "CSV.", "CSV.", AnalystsChoice: true)])], Rounds(page));
    }

    [Fact]
    public async Task Once_the_Spec_is_stored_the_Refinement_Sandbox_is_destroyed_and_the_Analyst_session_stopped()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(Spec())));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        await AwaitingSpecApprovalAsync(overseer, address);
        var session = Assert.Single(overseer.Sandboxes.AgentSessionsStarted);
        await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.AgentSessionsStopped.Contains(session) ? "" : null), "the Analyst session to stop");
        await EventuallyAsync(async () => (await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken)).Count == 0 ? "" : null, "the Sandbox to be destroyed");
        Assert.Single(overseer.Sandboxes.MessagesSent);
    }

    // Story 42, through the real route to AwaitingSpecApproval.
    [Fact]
    public async Task An_Intent_awaiting_Spec_approval_takes_no_Active_Intent_slot()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await SetMaxActiveIntentsAsync(overseer, 1);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst,
            new FakeAgentSession(HandsInSpec(Spec())),
            new FakeAgentSession(FakeAgentTurn.Endless()));
        using var first = await SaveAsync(overseer, "Export data", "acme/web");
        using var second = await SaveAsync(overseer, "Import data", "acme/web");
        var exportData = await AddressAsync(overseer, "Export data");
        var importData = await AddressAsync(overseer, "Import data");
        using var started = await StartAsync(overseer, exportData);
        await AwaitingSpecApprovalAsync(overseer, exportData);

        using var response = await StartAsync(overseer, importData);

        Assert.Equal("Refining", ElementText(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), "intent-state"));
        Assert.Equal("Awaiting Spec Approval", ElementText(await GetAsync(overseer, exportData), "intent-state"));
    }

    [Fact]
    public async Task Working_Time_stops_accruing_once_the_Analyst_hands_in_the_Spec()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInRound(("Which format?", "CSV.")), HandsInSpec(Spec())));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingAnswersAsync(overseer, address);
        time.Advance(TimeSpan.FromMinutes(40));

        using var answered = await AnswerAsync(overseer, address, 1, ("1", AsSuggested));
        await AwaitingSpecApprovalAsync(overseer, address);
        time.Advance(TimeSpan.FromHours(5));

        Assert.Equal("40 min", ElementText(await GetAsync(overseer, address), "intent-working-time"));
        Assert.Equal("40 min", ElementText(await GetAsync(overseer, "/"), "intent-working-time"));
    }

    [Fact]
    public async Task The_Spec_is_kept_in_PostgreSQL_and_shown_after_a_restart_with_the_Intent_still_awaiting_approval()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            first.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(Spec())));
            using var saved = await SaveAsync(first, "Export data", "acme/web");
            address = await AddressAsync(first, "Export data");
            using var started = await StartAsync(first, address);
            await AwaitingSpecApprovalAsync(first, address);
        }

        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        var page = await GetAsync(second, address);
        Assert.Equal("Awaiting Spec Approval", ElementText(page, "intent-state"));
        Assert.Equal(("Orange", "Needs Attention"), LightOn(page));
        Assert.Equal(1, SpecVersionOn(page));
        Assert.Equal("<p>Orders can be exported as <strong>CSV</strong>.</p>", SpecMarkdown(page, "Summary"));
        Assert.Empty(second.Sandboxes.AgentSessionsStarted);
    }

    [Fact]
    public async Task The_detail_page_shows_the_Specs_sections_as_Markdown_with_Analysts_Choices_marked_followed_by_its_Tasks_in_order()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api");
        var spec = Spec();
        spec["apiEndpointChanges"] = "`GET /orders/export` returns `text/csv`.";
        spec["dataModelChanges"] = "Orders get an `exported_at` column.";
        spec["uiChanges"] = "An *Export* button above the orders table.";
        spec["tasks"] = new JsonArray(
            TaskJson("Design the Export button", "Place it above the table.", ["acme/web"], role: "Designer"),
            TaskJson("Add the export endpoint & <button>", "Stream **every** order.\r\n\r\n- as CSV", ["ACME/API", "acme/web"]));
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(spec)));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web", "acme/api");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var page = await AwaitingSpecApprovalAsync(overseer, address);
        Assert.Equal(
            ["Summary", "Approach", "Decisions", "Acceptance criteria", "Dependencies", "API endpoint changes", "Data model changes", "UI changes", "Tasks"],
            SpecSections(page));
        Assert.Equal("<p>Orders can be exported as <strong>CSV</strong>.</p>", SpecMarkdown(page, "Summary"));
        Assert.Equal("<p>Add an export endpoint and a button on the orders page.</p>", SpecMarkdown(page, "Approach"));
        Assert.Equal([("<p>The export is <em>CSV</em>.</p>", true), ("<p>It holds every order.</p>", false)], Decisions(page));
        Assert.Equal("<ul>\n<li>The orders page offers Export</li>\n<li>The file opens in a spreadsheet</li>\n</ul>", SpecMarkdown(page, "Acceptance criteria"));
        Assert.Equal("<p>None.</p>", SpecMarkdown(page, "Dependencies"));
        Assert.Equal("<p><code>GET /orders/export</code> returns <code>text/csv</code>.</p>", SpecMarkdown(page, "API endpoint changes"));
        Assert.Equal("<p>Orders get an <code>exported_at</code> column.</p>", SpecMarkdown(page, "Data model changes"));
        Assert.Equal("<p>An <em>Export</em> button above the orders table.</p>", SpecMarkdown(page, "UI changes"));
        Assert.Equal(
            [
                new ShownTask(1, "Design the Export button", "Designer", "acme/web", "<p>Place it above the table.</p>"),
                // As the Intent has them in scope, whatever their case in the Spec.
                new ShownTask(2, "Add the export endpoint & <button>", "Developer", "acme/api, acme/web", "<p>Stream <strong>every</strong> order.</p>\n<ul>\n<li>as CSV</li>\n</ul>"),
            ],
            Tasks(page));
    }

    [Fact]
    public async Task The_sections_that_dont_apply_are_left_out_of_the_detail_page()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var spec = Spec();
        spec["dataModelChanges"] = null;
        spec["uiChanges"] = "  ";
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(spec)));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var page = await AwaitingSpecApprovalAsync(overseer, address);
        Assert.Equal(["Summary", "Approach", "Decisions", "Acceptance criteria", "Dependencies", "Tasks"], SpecSections(page));
        Assert.DoesNotContain("changes", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_Spec_may_record_no_decisions_and_the_detail_page_says_there_are_none()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var spec = Spec();
        spec["decisions"] = new JsonArray();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(spec)));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var page = await AwaitingSpecApprovalAsync(overseer, address);
        Assert.Equal(["Summary", "Approach", "Decisions", "Acceptance criteria", "Dependencies", "Tasks"], SpecSections(page));
        Assert.Empty(Decisions(page));
        Assert.Equal("None.", ElementText(page, "spec-no-decisions"));
    }

    [Fact]
    public async Task Links_the_Analyst_writes_in_the_Spec_are_clickable_only_when_their_scheme_is_safe()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var spec = Spec();
        spec["summary"] = "[Run](javascript:alert(1)), [shout](JaVaScRiPt:alert(1)) and <javascript:alert(1)>.";
        spec["approach"] = "See [the docs](https://example.com/docs), [plain](http://example.com), [mail](mailto:dev@example.com), "
            + "[the code](src/orders.cs), [below](#tasks) and https://example.com/auto";
        spec["decisions"]![0]!["decision"] = "[data](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==) and ![image](javascript:alert(1))";
        spec["dependencies"] = "[tab](java&#9;script:alert(1)) and [encoded](&#106;avascript:alert(1))";
        spec["tasks"]![0]!["description"] = "[vb](vbscript:msgbox(1)) and [ref][evil]\n\n[evil]: javascript:alert(1)";
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(spec)));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var page = await AwaitingSpecApprovalAsync(overseer, address);
        Assert.Equal("<p>Run, shout and javascript:alert(1).</p>", SpecMarkdown(page, "Summary"));
        Assert.Equal(
            "<p>See <a href=\"https://example.com/docs\">the docs</a>, <a href=\"http://example.com\">plain</a>, "
            + "<a href=\"mailto:dev@example.com\">mail</a>, <a href=\"src/orders.cs\">the code</a>, <a href=\"#tasks\">below</a> and "
            + "<a href=\"https://example.com/auto\">https://example.com/auto</a></p>",
            SpecMarkdown(page, "Approach"));
        Assert.Equal("<p>data and image</p>", Decisions(page)[0].Html);
        Assert.Equal("<p>tab and encoded</p>", SpecMarkdown(page, "Dependencies"));
        Assert.Equal("<p>vb and ref</p>", Assert.Single(Tasks(page)).Description);
        Assert.DoesNotMatch("(?i)(href|src)=\"\\s*(javascript|data|vbscript|java\\s)", page);
    }

    [Fact]
    public async Task HTML_the_Analyst_writes_in_the_Spec_shows_as_text()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var spec = Spec();
        spec["summary"] = "Drop the <img src=x onerror=alert(1)> from the view.";
        spec["approach"] = "<script>alert(1)</script>";
        spec["decisions"]![0]!["decision"] = "<b onclick=\"alert(1)\">Bold</b>";
        spec["tasks"]![0]!["description"] = "<a href=\"javascript:alert(1)\">Run</a>";
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(spec)));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var page = await AwaitingSpecApprovalAsync(overseer, address);
        Assert.Equal("<p>Drop the &lt;img src=x onerror=alert(1)&gt; from the view.</p>", SpecMarkdown(page, "Summary"));
        Assert.Equal("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>", SpecMarkdown(page, "Approach"));
        Assert.Equal("<p>&lt;b onclick=&quot;alert(1)&quot;&gt;Bold&lt;/b&gt;</p>", Decisions(page)[0].Html);
        Assert.Equal("<p>&lt;a href=&quot;javascript:alert(1)&quot;&gt;Run&lt;/a&gt;</p>", Assert.Single(Tasks(page)).Description);
    }

    [Fact]
    public async Task A_Spec_handed_in_for_an_Intent_no_longer_Refining_is_not_stored_and_the_refusal_is_logged()
    {
        var logs = new CapturedLogs();
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], logs: logs);
        var handingIn = new TaskCompletionSource();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(Spec()) with { EndsAfter = handingIn.Task }));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.SingleOrDefault()), "the Analyst to start its turn");
        // No route takes a Refining Intent elsewhere yet, so the test moves it while the Analyst works.
        var intentId = Guid.Parse(address.Split('/')[^1]);
        await using (var database = overseer.OpenDatabase())
        {
            await database.Intents.Where(intent => intent.Id == intentId)
                .ExecuteUpdateAsync(intent => intent.SetProperty(i => i.State, IntentState.Cancelled), TestContext.Current.CancellationToken);
        }

        handingIn.SetResult();

        var logged = await EventuallyAsync(
            () => Task.FromResult(logs.Entries.FirstOrDefault(entry => entry.StartsWith("Warning Overseer.Refinement.Refinements:"))),
            "the refusal to be logged");
        Assert.Equal(
            $"Warning Overseer.Refinement.Refinements: The Spec the Analyst handed in for the Intent {intentId} was not stored: "
            + "This Intent is Cancelled, and only a Refining Intent gets a Spec from the Analyst.",
            logged);
        await using var stored = overseer.OpenDatabase();
        Assert.False(await stored.SpecVersions.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_invalid_Spec_is_never_stored_the_Analyst_gets_the_validation_errors_and_the_Intent_stays_Refining()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var invalid = Spec();
        invalid.Remove("summary");
        invalid["tasks"]![0]!.AsObject().Remove("role");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(invalid), FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var rejection = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.ElementAtOrDefault(1)), "the Analyst to be told");
        Assert.Equal(
            $"""
            The Spec you wrote to {SpecPath} is not valid, so it isn't stored and the human hasn't seen it:

            - The Spec has no "summary": it must be Markdown text that isn't empty.
            - Task 1 has no "role": it must be "Designer" or "Developer".

            Write the whole Spec again at {SpecPath}, in the format of your instructions, then end your turn.
            """,
            rejection.Message);
        var page = await GetAsync(overseer, address);
        Assert.Equal("Refining", ElementText(page, "intent-state"));
        Assert.Equal(("Green", "Working"), LightOn(page));
        Assert.Null(SpecVersionOn(page));
        await using var database = overseer.OpenDatabase();
        Assert.False(await database.SpecVersions.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task After_an_invalid_Spec_the_Analysts_next_turn_is_handled_as_usual()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec("""{ "tasks": [] }"""), HandsInSpec(Spec())));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var page = await AwaitingSpecApprovalAsync(overseer, address);
        Assert.Equal(1, SpecVersionOn(page));
        Assert.Equal(2, overseer.Sandboxes.MessagesSent.Count);
    }

    [Theory]
    [InlineData("no summary", "The Spec has no \"summary\": it must be Markdown text that isn't empty.")]
    [InlineData("blank approach", "The Spec has no \"approach\": it must be Markdown text that isn't empty.")]
    [InlineData("no decisions", "The Spec has no \"decisions\": it must be a list of decisions, empty when there are none.")]
    [InlineData("decisions not a list", "The Spec has no \"decisions\": it must be a list of decisions, empty when there are none.")]
    [InlineData("decision without analystsChoice", "Decision 2 has no \"analystsChoice\": it must be true when the human left the decision to you, and false otherwise.")]
    [InlineData("no acceptance criteria", "The Spec has no \"acceptanceCriteria\": it must be Markdown text that isn't empty.")]
    [InlineData("no dependencies", "The Spec has no \"dependencies\": it must be Markdown text that isn't empty.")]
    [InlineData("UI changes not text", "The Spec's \"uiChanges\" must be Markdown text, or left out when there are none.")]
    [InlineData("no tasks", "The Spec has no Tasks: \"tasks\" must be a list of at least one Task.")]
    [InlineData("empty tasks", "The Spec has no Tasks: \"tasks\" must be a list of at least one Task.")]
    [InlineData("task without title", "Task 1 has no \"title\": it must be text that isn't empty.")]
    [InlineData("task without description", "Task 1 has no \"description\": it must be Markdown text that isn't empty.")]
    [InlineData("task without role", "Task 1 has no \"role\": it must be \"Designer\" or \"Developer\".")]
    [InlineData("Reviewer task", "Task 1 has the role \"Reviewer\": it must be \"Designer\" or \"Developer\".")]
    [InlineData("Analyst task", "Task 1 has the role \"Analyst\": it must be \"Designer\" or \"Developer\".")]
    [InlineData("task without repositories", "Task 1 touches no repository: \"repositories\" must list at least one of the Repositories in scope, acme/web.")]
    [InlineData("task touching no repository", "Task 1 touches no repository: \"repositories\" must list at least one of the Repositories in scope, acme/web.")]
    [InlineData("task touching a repository not in scope", "Task 1 touches acme/api, which isn't a Repository in scope: those are acme/web.")]
    [InlineData("not an object", "The file must hold a JSON object with the Spec's sections and its \"tasks\".")]
    [InlineData("not json", "The file is not valid JSON:")]
    public async Task A_Spec_is_invalid_when(string problem, string error)
    {
        // acme/api is configured, but not in this Intent's scope.
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api");
        var spec = Spec();
        var task = spec["tasks"]![0]!.AsObject();
        switch (problem)
        {
            case "no summary": spec.Remove("summary"); break;
            case "blank approach": spec["approach"] = " \n "; break;
            case "no decisions": spec.Remove("decisions"); break;
            case "decisions not a list": spec["decisions"] = "None."; break;
            case "decision without analystsChoice": spec["decisions"]![1]!.AsObject().Remove("analystsChoice"); break;
            case "no acceptance criteria": spec.Remove("acceptanceCriteria"); break;
            case "no dependencies": spec["dependencies"] = ""; break;
            case "UI changes not text": spec["uiChanges"] = new JsonArray("A button"); break;
            case "no tasks": spec.Remove("tasks"); break;
            case "empty tasks": spec["tasks"] = new JsonArray(); break;
            case "task without title": task.Remove("title"); break;
            case "task without description": task["description"] = " "; break;
            case "task without role": task.Remove("role"); break;
            case "Reviewer task": task["role"] = "Reviewer"; break;
            case "Analyst task": task["role"] = "Analyst"; break;
            case "task without repositories": task.Remove("repositories"); break;
            case "task touching no repository": task["repositories"] = new JsonArray(); break;
            case "task touching a repository not in scope": task["repositories"] = new JsonArray("acme/web", "acme/api"); break;
        }
        var handedIn = problem switch
        {
            "not an object" => HandsInSpec(new JsonArray(spec)),
            "not json" => HandsInSpec("{ \"summary\": "),
            _ => HandsInSpec(spec),
        };
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(handedIn, FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var rejection = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.ElementAtOrDefault(1)), "the Analyst to be told");
        Assert.Contains($"\n- {error}", rejection.Message);
        Assert.Single(Regex.Matches(rejection.Message, "^- ", RegexOptions.Multiline));
        var page = await GetAsync(overseer, address);
        Assert.Equal("Refining", ElementText(page, "intent-state"));
        Assert.Null(SpecVersionOn(page));
    }

    [Fact]
    public async Task The_Analysts_instructions_describe_how_to_hand_in_the_Spec_with_an_example_Overseer_accepts()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(FakeAgentTurn.Endless()), new FakeAgentSession(FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Export data"));

        var instructions = (await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.SingleOrDefault()), "the Analyst's first message")).Message;

        Assert.Contains($"To hand in the Spec, write it as JSON to {SpecPath}", instructions);
        Assert.Contains("You alone decide when you know enough to write it", instructions);
        Assert.Contains("Hand in either a Round or the Spec in a turn, never both.", instructions);
        Assert.Matches("\"analystsChoice\" true when it is an\\s+Analyst's Choice", instructions);
        Assert.Matches("Leave the list empty when there\\s+are none: don't make one up.", instructions);
        // The example it is given, the second in the instructions after the Round's, is a valid Spec for an Intent with its repository in scope.
        var example = Regex.Matches(instructions, @"^\{\n.*?^\}$", RegexOptions.Singleline | RegexOptions.Multiline)[1].Value;
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(example)));
        using var other = await SaveAsync(overseer, "Import data", "acme/web");
        var address = await AddressAsync(overseer, "Import data");
        using var startedOther = await StartAsync(overseer, address);
        var page = await AwaitingSpecApprovalAsync(overseer, address);
        Assert.Equal(
            ["Summary", "Approach", "Decisions", "Acceptance criteria", "Dependencies", "API endpoint changes", "UI changes", "Tasks"],
            SpecSections(page));
    }

    [Fact]
    public async Task The_Analyst_hears_that_it_may_hand_in_the_Spec_with_the_answers_to_each_Round()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInRound(("Which format?", "CSV.")), FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await AwaitingAnswersAsync(overseer, address);

        using var answered = await AnswerAsync(overseer, address, 1, ("1", "Excel."));

        var answers = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.ElementAtOrDefault(1)), "the answers to reach the Analyst");
        Assert.EndsWith($"or with the Spec at {SpecPath}, once you have enough to write it.", answers.Message);
    }

    [Fact]
    public async Task A_turn_handing_in_both_a_Round_and_a_Spec_shows_neither_and_the_Analyst_is_told_to_hand_in_one()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var both = new FakeAgentTurn([], new AgentOutcome.Done())
        {
            Writes = new Dictionary<string, string> { [RoundPath] = """{ "questions": [ { "question": "Which format?", "suggestedAnswer": "CSV." } ] }""", [SpecPath] = Spec().ToJsonString() },
        };
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(both, FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var rejection = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.ElementAtOrDefault(1)), "the Analyst to be told");
        Assert.StartsWith("You handed in both a Round and a Spec, so the human sees neither.", rejection.Message);
        var page = await GetAsync(overseer, address);
        Assert.Equal("Refining", ElementText(page, "intent-state"));
        Assert.Empty(Rounds(page));
        Assert.Null(SpecVersionOn(page));
        // Neither is left behind for a later turn.
        var sandbox = Assert.Single(overseer.Sandboxes.AgentSessionsStarted).Sandbox;
        Assert.Null(await overseer.Sandboxes.TakeFileAsync(sandbox, RoundPath, TestContext.Current.CancellationToken));
        Assert.Null(await overseer.Sandboxes.TakeFileAsync(sandbox, SpecPath, TestContext.Current.CancellationToken));
    }

    /// <summary>The fixed path in its Sandbox at which the Analyst hands in the Spec, as its instructions say.</summary>
    internal const string SpecPath = "/home/overseer/.overseer/spec.json";

    /// <summary>A valid Spec for an Intent with acme/web in scope, as JSON to change for a test.</summary>
    internal static JsonObject Spec() => new()
    {
        ["summary"] = "Orders can be exported as **CSV**.",
        ["approach"] = "Add an export endpoint and a button on the orders page.",
        ["decisions"] = new JsonArray(
            new JsonObject { ["decision"] = "The export is *CSV*.", ["analystsChoice"] = true },
            new JsonObject { ["decision"] = "It holds every order.", ["analystsChoice"] = false }),
        ["acceptanceCriteria"] = "- The orders page offers Export\n- The file opens in a spreadsheet",
        ["dependencies"] = "None.",
        ["tasks"] = new JsonArray(TaskJson("Add the export endpoint", "`GET /orders/export` returns every order as CSV.", ["acme/web"])),
    };

    /// <summary>A Task of a Spec, as JSON, touching these repositories, each as owner/name.</summary>
    internal static JsonObject TaskJson(string title, string description, string[] repositories, string role = "Developer") => new()
    {
        ["title"] = title,
        ["description"] = description,
        ["role"] = role,
        ["repositories"] = new JsonArray([.. repositories.Select(repository => (JsonNode)repository)]),
    };

    /// <summary>The valid Spec of <see cref="Spec"/> without these properties.</summary>
    internal static JsonObject SpecWithout(params string[] properties)
    {
        var spec = Spec();
        foreach (var property in properties)
        {
            spec.Remove(property);
        }
        return spec;
    }

    /// <summary>A turn in which the Analyst hands in this Spec.</summary>
    internal static FakeAgentTurn HandsInSpec(JsonNode spec) => HandsInSpec(spec.ToJsonString());

    /// <summary>A turn in which the Analyst writes this as its Spec.</summary>
    internal static FakeAgentTurn HandsInSpec(string spec) =>
        new([], new AgentOutcome.Done()) { Writes = new Dictionary<string, string> { [SpecPath] = spec } };

    /// <summary>Waits for the Intent's detail page to show it awaiting Spec approval, and gives the page.</summary>
    internal static Task<string> AwaitingSpecApprovalAsync(RunningOverseer overseer, string address) =>
        EventuallyAsync(async () => await GetAsync(overseer, address) is var page && ElementText(page, "intent-state") == "Awaiting Spec Approval" ? page : null,
            "the Intent to await Spec approval");

    /// <summary>The HTML of the Markdown the detail page renders under this label, such as a Spec section's heading.</summary>
    internal static string SpecMarkdown(string page, string label) =>
        Regex.Match(page, $"<section class=\"markdown-preview\" aria-label=\"{Regex.Escape(label)}\">(.*?)</section>", RegexOptions.Singleline) is { Success: true } markdown
            ? markdown.Groups[1].Value.Trim().ReplaceLineEndings("\n")
            : throw new InvalidOperationException($"The page renders no Markdown labelled {label}: {page}");

    /// <summary>The headings of the Spec's sections on the detail page, in order.</summary>
    internal static IReadOnlyList<string> SpecSections(string page) =>
        [.. Regex.Matches(page, "<section class=\"spec-section\" data-section=\"([^\"]*)\"").Select(section => WebUtility.HtmlDecode(section.Groups[1].Value))];

    /// <summary>The Spec's decisions on the detail page, in order: each one's HTML, and whether it is marked an Analyst's Choice.</summary>
    internal static IReadOnlyList<(string Html, bool AnalystsChoice)> Decisions(string page) =>
        [.. Regex.Matches(page, "<li class=\"spec-decision\"([^>]*)>(.*?)</li>\\s*(?=<li class=\"spec-decision\"|</ol>)", RegexOptions.Singleline).Select((decision, index) =>
        {
            // Marked in words, not only for styling.
            var marked = decision.Groups[2].Value.Contains("class=\"analysts-choice\"");
            if (marked)
            {
                Assert.Equal("Analyst's Choice", ElementText(decision.Groups[2].Value, "analysts-choice"));
            }
            Assert.Equal(marked, decision.Groups[1].Value.Contains("data-analysts-choice"));
            return (SpecMarkdown(decision.Groups[2].Value, $"Decision {index + 1}"), marked);
        })];

    /// <summary>The Spec's Tasks on the detail page, in order.</summary>
    internal static IReadOnlyList<ShownTask> Tasks(string page) =>
        [.. Regex.Matches(page, "<li class=\"spec-task\" data-task=\"(\\d+)\">(.*?)</li>\\s*(?=<li class=\"spec-task\"|</ol>)", RegexOptions.Singleline).Select(task => new ShownTask(
            int.Parse(task.Groups[1].Value),
            ElementText(task.Groups[2].Value, "spec-task-title"),
            ElementText(task.Groups[2].Value, "spec-task-role"),
            ElementText(task.Groups[2].Value, "spec-task-repositories"),
            SpecMarkdown(task.Groups[2].Value, $"Task {task.Groups[1].Value}")))];

    /// <summary>A Task of the Spec as the detail page shows it.</summary>
    /// <param name="Repositories">As the page lists them.</param>
    /// <param name="Description">The HTML of its rendered description.</param>
    internal sealed record ShownTask(int Number, string Title, string Role, string Repositories, string Description);

    /// <summary>The version of the Spec the detail page shows; <c>null</c> when it shows none.</summary>
    internal static int? SpecVersionOn(string page) =>
        Regex.Match(page, "<section class=\"spec\"[^>]*data-version=\"(\\d+)\"") is { Success: true } spec ? int.Parse(spec.Groups[1].Value) : null;
}
