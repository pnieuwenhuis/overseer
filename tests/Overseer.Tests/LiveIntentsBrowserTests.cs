using Microsoft.Extensions.Time.Testing;
using Microsoft.Playwright;
using Overseer.Intents;
using Overseer.Sandboxes;
using static Microsoft.Playwright.Assertions;
using static Overseer.Tests.IntentPortal;

namespace Overseer.Tests;

public class LiveIntentsBrowserTests(PostgresServer postgres, PortalChromium chromium) : IClassFixture<PortalChromium>
{
    [Fact]
    public async Task Starting_an_Intent_in_one_tab_updates_an_open_Backlog_in_another_without_a_reload()
    {
        await using var overseer = await StartOverseerAsync();
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        await using var session = await chromium.LogInAsync(overseer);
        var backlog = await OpenAsync(session, "/");
        await AwaitBacklogConnectedAsync(overseer, backlog);

        var other = await OpenAsync(session, address);
        await StartButton(other).ClickAsync();
        await Expect(other.Locator(".intent-state")).ToHaveTextAsync("Refining");

        var intent = BacklogIntent(backlog, "Export data");
        await Expect(intent.Locator(".intent-state")).ToHaveTextAsync("Refining");
        await Expect(intent.Locator(".intent-light")).ToHaveTextAsync("Working");
        await Expect(intent.Locator(".intent-light")).ToHaveAttributeAsync("data-light", "Green");
        Assert.True(await WasNotReloadedAsync(backlog));
    }

    [Fact]
    public async Task Starting_an_Intent_from_its_live_detail_page_updates_the_page_in_another_tab_which_stops_offering_what_a_Draft_can_do()
    {
        await using var overseer = await StartOverseerAsync();
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        await using var session = await chromium.LogInAsync(overseer);
        var watching = await OpenAsync(session, address);
        var starting = await OpenAsync(session, address);
        await AwaitDetailConnectedAsync(overseer, address, "Export data", watching, starting);
        await Expect(watching.Locator(".intent-state")).ToHaveTextAsync("Draft");
        await Expect(watching.Locator(".intent-light")).ToHaveCountAsync(0);
        await Expect(StartButton(watching)).ToBeEnabledAsync();
        await Expect(EditLink(watching)).ToBeVisibleAsync();
        await Expect(DeleteLink(watching)).ToBeVisibleAsync();

        // The Start button of a page gone live still posts the form the page handles, and its antiforgery token still holds.
        await StartButton(starting).ClickAsync();
        await Expect(starting.Locator(".intent-state")).ToHaveTextAsync("Refining");

        await Expect(watching.Locator(".intent-state")).ToHaveTextAsync("Refining");
        await Expect(watching.Locator(".intent-light")).ToHaveTextAsync("Working");
        await Expect(StartButton(watching)).ToHaveCountAsync(0);
        await Expect(EditLink(watching)).ToHaveCountAsync(0);
        await Expect(DeleteLink(watching)).ToHaveCountAsync(0);
        await Expect(watching.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
        Assert.True(await WasNotReloadedAsync(watching));
    }

    [Fact]
    public async Task A_Drafts_live_page_disables_Start_and_says_why_when_the_last_slot_is_taken()
    {
        await using var overseer = await StartOverseerAsync();
        await SetMaxActiveIntentsAsync(overseer, 1);
        using var first = await SaveAsync(overseer, "Export data", "acme/web");
        using var second = await SaveAsync(overseer, "Import data", "acme/web");
        var firstAddress = await AddressAsync(overseer, "Export data");
        var secondAddress = await AddressAsync(overseer, "Import data");
        await using var session = await chromium.LogInAsync(overseer);
        var draft = await OpenAsync(session, secondAddress);
        await AwaitDetailConnectedAsync(overseer, secondAddress, "Import data", draft);
        await Expect(StartButton(draft)).ToBeEnabledAsync();

        using var started = await overseer.SubmitFormAsync(firstAddress, "start-intent", []);

        await Expect(StartButton(draft)).ToBeDisabledAsync();
        await Expect(draft.GetByRole(AriaRole.Alert)).ToContainTextAsync("All Active Intent slots are taken, by \"Export data\".");
        await Expect(draft.Locator(".intent-state")).ToHaveTextAsync("Draft");
        Assert.True(await WasNotReloadedAsync(draft));
    }

    [Fact]
    public async Task Deleting_a_Draft_says_so_on_its_live_page_and_takes_it_out_of_the_Backlog_in_other_tabs()
    {
        await using var overseer = await StartOverseerAsync();
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        await using var session = await chromium.LogInAsync(overseer);
        var detail = await OpenAsync(session, address);
        var backlog = await OpenAsync(session, "/");
        await AwaitDetailConnectedAsync(overseer, address, "Export data", detail);
        await AwaitBacklogConnectedAsync(overseer, backlog);

        using var deleted = await DeleteAsync(overseer, address);

        await Expect(detail.GetByRole(AriaRole.Alert)).ToHaveTextAsync("There is no such Intent: it may have been deleted.");
        await Expect(StartButton(detail)).ToHaveCountAsync(0);
        await Expect(EditLink(detail)).ToHaveCountAsync(0);
        await Expect(DeleteLink(detail)).ToHaveCountAsync(0);
        await Expect(BacklogIntent(backlog, "Export data")).ToHaveCountAsync(0);
        Assert.True(await WasNotReloadedAsync(detail));
        Assert.True(await WasNotReloadedAsync(backlog));
    }

    [Fact]
    public async Task Capturing_an_Intent_adds_it_to_an_open_Backlog_without_a_reload()
    {
        await using var overseer = await StartOverseerAsync();
        await using var session = await chromium.LogInAsync(overseer);
        var backlog = await OpenAsync(session, "/");
        await AwaitBacklogConnectedAsync(overseer, backlog);

        using var captured = await SaveAsync(overseer, "Export data", "acme/web");

        await Expect(BacklogIntent(backlog, "Export data").Locator(".intent-state")).ToHaveTextAsync("Draft");
        Assert.True(await WasNotReloadedAsync(backlog));
    }

    [Fact]
    public async Task Editing_a_Draft_updates_its_open_detail_page_and_an_open_Backlog_without_a_reload()
    {
        await using var overseer = await StartOverseerAsync();
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        await using var session = await chromium.LogInAsync(overseer);
        var detail = await OpenAsync(session, address);
        var backlog = await OpenAsync(session, "/");
        await AwaitDetailConnectedAsync(overseer, address, "Export data", detail);
        await AwaitBacklogConnectedAsync(overseer, backlog);

        using var edited = await EditAsync(overseer, address, "save", "Import data");

        await Expect(detail.GetByLabel("Intent text")).ToHaveTextAsync("Import data");
        await Expect(detail.Locator(".intent-repositories")).ToHaveTextAsync("No Repositories in scope");
        await Expect(BacklogIntent(backlog, "Import data")).ToContainTextAsync("No Repositories in scope");
        await Expect(BacklogIntent(backlog, "Export data")).ToHaveCountAsync(0);
        Assert.True(await WasNotReloadedAsync(detail));
        Assert.True(await WasNotReloadedAsync(backlog));
    }

    [Fact]
    public async Task A_refused_capture_edit_delete_or_Start_changes_nothing_on_an_open_Backlog()
    {
        await using var overseer = await StartOverseerAsync();
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        var detailTab = await overseer.OpenAsync(address);
        var editor = await overseer.OpenAsync(address + "/edit");
        var deletePage = await overseer.OpenAsync(address + "/delete");
        await using var session = await chromium.LogInAsync(overseer);
        var backlog = await OpenAsync(session, "/");
        await AwaitBacklogConnectedAsync(overseer, backlog);
        var state = BacklogIntent(backlog, "Export data").Locator(".intent-state");
        await Expect(state).ToHaveTextAsync("Draft");

        // Setting a state directly announces nothing, so the Backlog still shows Draft and reads Paused only when something announces a change.
        await SetStateAsync(overseer, "Export data", IntentState.Paused);
        await AssertRefusedAsync("This Intent is Paused, and Start moves a Draft to Refining, or an Approved Intent to Implementing.", detailTab.SubmitFormAsync("start-intent", []));
        await AssertRefusedAsync("This Intent is Paused, and only a Draft can be edited.", editor.SubmitFormAsync("edit-intent", EditorFields("Import data", "save", "acme/web")));
        await AssertRefusedAsync("This Intent is Paused, and only a Draft can be deleted.", deletePage.SubmitFormAsync("delete-intent", Confirmed));
        await AssertRefusedAsync("An Intent needs text: write what you want changed.", SaveAsync(overseer, " ", "acme/web"));

        // There is nothing to wait for when nothing is announced, so this gives an announcement the time to have arrived.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await Expect(state).ToHaveTextAsync("Draft");

        // The Backlog was listening all along: what this capture announces does bring the Paused Intent in.
        using var captured = await SaveAsync(overseer, "Another Draft", "acme/web");
        await Expect(state).ToHaveTextAsync("Paused");
    }

    private static async Task AssertRefusedAsync(string refusal, Task<HttpResponseMessage> submitted)
    {
        using var response = await submitted;
        Assert.Equal(refusal, Alert(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task The_filter_links_change_the_address_and_the_live_list_which_then_keeps_to_that_filter()
    {
        await using var overseer = await StartOverseerAsync();
        using var open = await SaveAsync(overseer, "Export data", "acme/web");
        using var closed = await SaveAsync(overseer, "Import data", "acme/web");
        await SetStateAsync(overseer, "Import data", IntentState.Done);
        await using var session = await chromium.LogInAsync(overseer);
        var backlog = await OpenAsync(session, "/");
        await AwaitBacklogConnectedAsync(overseer, backlog);
        await Expect(BacklogIntent(backlog, "Export data")).ToBeVisibleAsync();
        await Expect(BacklogIntent(backlog, "Import data")).ToHaveCountAsync(0);

        await backlog.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Closed", Exact = true }).ClickAsync();

        await backlog.WaitForURLAsync("/?filter=closed");
        await Expect(backlog.Locator("[data-intent]")).ToHaveCountAsync(1);
        await Expect(BacklogIntent(backlog, "Import data").Locator(".intent-state")).ToHaveTextAsync("Done");

        // Setting a state directly announces nothing, so what this capture announces is what brings the Cancelled Intent in, and the new
        // Draft doesn't come with it.
        await SetStateAsync(overseer, "Export data", IntentState.Cancelled);
        using var captured = await SaveAsync(overseer, "Another Draft", "acme/web");
        await Expect(BacklogIntent(backlog, "Export data").Locator(".intent-state")).ToHaveTextAsync("Cancelled");
        await Expect(backlog.Locator("[data-intent]")).ToHaveCountAsync(2);

        await backlog.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Open", Exact = true }).ClickAsync();

        await backlog.WaitForURLAsync("/?filter=open");
        await Expect(BacklogIntent(backlog, "Another Draft")).ToBeVisibleAsync();
        await Expect(BacklogIntent(backlog, "Import data")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task The_Working_Time_of_an_Active_Intent_keeps_advancing_on_an_open_Backlog_and_detail_page_without_a_reload()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var overseer = await StartOverseerAsync(time);
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        await using var session = await chromium.LogInAsync(overseer);
        var detail = await OpenAsync(session, address);
        var backlog = await OpenAsync(session, "/");
        await AwaitDetailConnectedAsync(overseer, address, "Export data", detail);
        await AwaitBacklogConnectedAsync(overseer, backlog);
        using var started = await StartAsync(overseer, address);
        var detailWorkingTime = detail.Locator(".intent-working-time");
        var backlogWorkingTime = BacklogIntent(backlog, "Export data").Locator(".intent-working-time");
        await Expect(detailWorkingTime).ToHaveTextAsync("0 min");
        await Expect(backlogWorkingTime).ToHaveTextAsync("0 min");

        time.Advance(TimeSpan.FromMinutes(135));

        await Expect(detailWorkingTime).ToHaveTextAsync("2 h 15 min");
        await Expect(backlogWorkingTime).ToHaveTextAsync("2 h 15 min");

        time.Advance(TimeSpan.FromMinutes(1));

        await Expect(detailWorkingTime).ToHaveTextAsync("2 h 16 min");
        await Expect(backlogWorkingTime).ToHaveTextAsync("2 h 16 min");
        Assert.True(await WasNotReloadedAsync(detail));
        Assert.True(await WasNotReloadedAsync(backlog));
    }

    [Fact]
    public async Task A_page_going_live_keeps_showing_what_the_server_rendered_rather_than_blanking_it_first()
    {
        await using var overseer = await StartOverseerAsync();
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        await using var session = await chromium.LogInAsync(overseer);

        var backlog = await OpenWatchingForBlankingAsync(session, "/", "[data-intent]");
        await AwaitBacklogConnectedAsync(overseer, backlog);
        Assert.False(await backlog.EvaluateAsync<bool>("window.blanked"));

        var detail = await OpenWatchingForBlankingAsync(session, address, ".intent-facts");
        await AwaitDetailConnectedAsync(overseer, address, "Export data", detail);
        Assert.False(await detail.EvaluateAsync<bool>("window.blanked"));
    }

    /// <summary>
    /// Opens a page in a new tab that remembers, in <c>window.blanked</c>, any moment the selector matched nothing after it had matched.
    /// </summary>
    private static async Task<IPage> OpenWatchingForBlankingAsync(IBrowserContext session, string address, string selector)
    {
        var tab = await session.NewPageAsync();
        await tab.AddInitScriptAsync($$"""
            window.blanked = false;
            let seen = false;
            new MutationObserver(() => {
                const shown = document.querySelector('{{selector}}') !== null;
                window.blanked ||= seen && !shown;
                seen ||= shown;
            }).observe(document, { childList: true, subtree: true });
            """);
        await tab.GotoAsync(address);
        return tab;
    }

    [Fact]
    public async Task An_open_detail_page_follows_the_Refinement_live_through_provisioning_a_failure_and_a_Retry_to_the_Analyst_working()
    {
        var time = new FakeTimeProvider();
        await using var overseer = await StartOverseerAsync(time);
        overseer.Sandboxes.ProvisioningDelay = TimeSpan.FromMinutes(4);
        overseer.Sandboxes.FailNextCreation("Allocation failed.");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        await using var session = await chromium.LogInAsync(overseer);
        var detail = await OpenAsync(session, address);
        await AwaitDetailConnectedAsync(overseer, address, "Export data", detail);

        using var started = await overseer.SubmitFormAsync(address, "start-intent", []);

        var refinement = detail.Locator(".refinement");
        await Expect(refinement).ToHaveAttributeAsync("data-phase", "ProvisioningSandbox");
        await Expect(refinement.GetByRole(AriaRole.Status)).ToHaveTextAsync("Provisioning a Sandbox for the Analyst. A new Sandbox takes a few minutes.");
        await Expect(detail.Locator(".intent-light")).ToHaveAttributeAsync("data-light", "Green");
        await AdvanceUntilAsync(time, refinement, "Failed");
        await Expect(refinement.GetByRole(AriaRole.Alert)).ToHaveTextAsync("The Sandbox could not be created: Allocation failed.");
        await Expect(detail.Locator(".intent-light")).ToHaveAttributeAsync("data-light", "Red");
        await Expect(detail.Locator(".intent-light")).ToHaveTextAsync("Failed");
        Assert.True(await WasNotReloadedAsync(detail));

        await detail.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Retry" }).ClickAsync();

        await AdvanceUntilAsync(time, refinement, "AnalystWorking");
        await Expect(refinement.GetByRole(AriaRole.Status)).ToHaveTextAsync("The Analyst is working on the Intent.");
        await Expect(detail.Locator(".intent-light")).ToHaveAttributeAsync("data-light", "Green");
        await Expect(detail.Locator(".intent-state")).ToHaveTextAsync("Refining");
    }

    [Fact]
    public async Task An_open_detail_page_shows_a_Round_as_it_comes_in_and_its_answers_once_sent_with_the_light_back_to_green()
    {
        var time = new FakeTimeProvider();
        await using var overseer = await StartOverseerAsync(time);
        overseer.Sandboxes.ProvisioningDelay = TimeSpan.FromMinutes(4);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(
            RoundsTests.HandsInRound(("Which format?", "CSV."), ("Which page offers it?", "The orders page."), ("Up to how many rows?", "All of them.")),
            FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        await using var session = await chromium.LogInAsync(overseer);
        var detail = await OpenAsync(session, address);
        var backlog = await OpenAsync(session, "/");
        await AwaitDetailConnectedAsync(overseer, address, "Export data", detail);
        await AwaitBacklogConnectedAsync(overseer, backlog);

        using var started = await overseer.SubmitFormAsync(address, "start-intent", []);

        var refinement = detail.Locator(".refinement");
        await AdvanceUntilAsync(time, refinement, "AwaitingAnswers");
        await Expect(refinement.GetByRole(AriaRole.Status)).ToHaveTextAsync("The Analyst asks you the questions of Round 1 below.");
        await Expect(detail.Locator(".intent-light")).ToHaveAttributeAsync("data-light", "Orange");
        await Expect(detail.Locator(".intent-light")).ToHaveTextAsync("Needs Attention");
        await Expect(BacklogIntent(backlog, "Export data").Locator(".intent-light")).ToHaveTextAsync("Needs Attention");
        var round = detail.Locator("article.round");
        await Expect(round.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Round 1");
        await Expect(round.Locator(".question-text")).ToHaveTextAsync(["Which format?", "Which page offers it?", "Up to how many rows?"]);
        await Expect(round.Locator(".question-suggestion")).ToHaveTextAsync(["CSV.", "The orders page.", "All of them."]);
        Assert.True(await WasNotReloadedAsync(detail));

        await detail.GetByLabel("Your answer to question 1").FillAsync("Excel.");
        await round.Locator("[data-question='2']").GetByLabel("As suggested").CheckAsync();
        await detail.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Send answers" }).ClickAsync();

        await Expect(refinement).ToHaveAttributeAsync("data-phase", "AnalystWorking");
        await Expect(detail.Locator(".intent-light")).ToHaveAttributeAsync("data-light", "Green");
        await Expect(BacklogIntent(backlog, "Export data").Locator(".intent-light")).ToHaveTextAsync("Working");
        await Expect(round.Locator(".question-answer")).ToHaveTextAsync(["Excel.", "The orders page.", "All of them."]);
        await Expect(round.Locator(".question-answer-label")).ToHaveTextAsync(["Your answer", "Analyst's Choice", "Analyst's Choice"]);
        await Expect(detail.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Send answers" })).ToHaveCountAsync(0);
        var sent = overseer.Sandboxes.MessagesSent[1].Message;
        Assert.Contains("Question 1: Which format?\nThe human's answer: Excel.", sent);
        Assert.Contains("Question 2: Which page offers it?\nAnalyst's Choice", sent);
        Assert.Contains("Question 3: Up to how many rows?\nAnalyst's Choice", sent);
    }

    [Fact]
    public async Task Answers_refused_for_a_question_given_both_an_own_answer_and_as_suggested_stay_in_the_live_form_as_typed()
    {
        var time = new FakeTimeProvider();
        await using var overseer = await StartOverseerAsync(time);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(
            RoundsTests.HandsInRound(("Which format?", "CSV."), ("Which page offers it?", "The orders page.")), FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await overseer.SubmitFormAsync(address, "start-intent", []);
        await RoundsTests.AwaitingAnswersAsync(overseer, address);
        await using var session = await chromium.LogInAsync(overseer);
        var detail = await OpenAsync(session, address);
        var round = detail.Locator("article.round");
        await AwaitWorkingTimeLiveAsync(time, detail);

        await detail.GetByLabel("Your answer to question 1").FillAsync("Excel.");
        await round.Locator("[data-question='1']").GetByLabel("As suggested").CheckAsync();
        await detail.GetByLabel("Your answer to question 2").FillAsync("The reports page.");
        await detail.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Send answers" }).ClickAsync();

        await Expect(detail.GetByRole(AriaRole.Alert)).ToContainTextAsync("Question 1 has an answer of your own and As suggested ticked.");
        // The page that came back goes live, and the form it then renders still holds the answers.
        await AwaitWorkingTimeLiveAsync(time, detail);
        await Expect(detail.GetByLabel("Your answer to question 1")).ToHaveValueAsync("Excel.");
        await Expect(round.Locator("[data-question='1']").GetByLabel("As suggested")).ToBeCheckedAsync();
        await Expect(detail.GetByLabel("Your answer to question 2")).ToHaveValueAsync("The reports page.");
        await Expect(round.Locator("[data-question='2']").GetByLabel("As suggested")).Not.ToBeCheckedAsync();
        Assert.Single(overseer.Sandboxes.MessagesSent);
    }

    [Fact]
    public async Task An_open_detail_page_shows_the_Spec_as_it_comes_in_awaiting_approval_with_the_transcript_still_below()
    {
        var time = new FakeTimeProvider();
        await using var overseer = await StartOverseerAsync(time);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(
            RoundsTests.HandsInRound(("Which format?", "CSV.")), SpecTests.HandsInSpec(SpecTests.Spec())));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await overseer.SubmitFormAsync(address, "start-intent", []);
        await RoundsTests.AwaitingAnswersAsync(overseer, address);
        await using var session = await chromium.LogInAsync(overseer);
        var detail = await OpenAsync(session, address);
        await AwaitWorkingTimeLiveAsync(time, detail);

        using var answered = await RoundsTests.AnswerAsync(overseer, address, 1, ("1", RoundsTests.AsSuggested));

        var spec = detail.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "Spec", Exact = true });
        await Expect(spec.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Level = 2 })).ToHaveTextAsync("Spec version 1");
        await Expect(detail.Locator(".intent-state")).ToHaveTextAsync("Awaiting Spec Approval");
        await Expect(detail.Locator(".intent-light")).ToHaveTextAsync("Needs Attention");
        await Expect(spec.Locator(".spec-task-title")).ToHaveTextAsync(["Add the export endpoint"]);
        await Expect(spec.Locator(".analysts-choice")).ToHaveTextAsync(["Analyst's Choice"]);
        await Expect(detail.Locator("article.round .question-answer")).ToHaveTextAsync(["CSV."]);
        Assert.True(await WasNotReloadedAsync(detail));
    }

    /// <summary>
    /// Waits until the detail page of an Active Intent is live, which its Working Time going on as the clock advances shows. Until then
    /// its live rendering may still replace what is typed into it.
    /// </summary>
    private static async Task AwaitWorkingTimeLiveAsync(FakeTimeProvider time, IPage detail)
    {
        var workingTime = detail.Locator(".intent-working-time");
        var shown = await workingTime.TextContentAsync();
        await RepositorySetupPortal.EventuallyAsync(async () =>
        {
            time.Advance(TimeSpan.FromMinutes(1));
            return await workingTime.TextContentAsync() != shown ? "" : null;
        }, "the detail page to go live");
    }

    /// <summary>
    /// Advances the clock until the Refinement shows this phase: until the fake Sandbox Provider has started waiting, advancing it doesn't
    /// end its wait, so it is advanced on each look.
    /// </summary>
    private static Task AdvanceUntilAsync(FakeTimeProvider time, ILocator refinement, string phase) =>
        RepositorySetupPortal.EventuallyAsync(async () =>
        {
            if (await refinement.GetAttributeAsync("data-phase") == phase)
            {
                return "";
            }
            time.Advance(TimeSpan.FromMinutes(4));
            return null;
        }, $"the Refinement to show {phase}");

    private async Task<RunningOverseer> StartOverseerAsync(TimeProvider? time = null) =>
        await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time, PortalChromium.ServerEnvironment);

    /// <summary>Opens a page in a new tab and marks it, so that a test can tell whether the browser loaded it again.</summary>
    private static async Task<IPage> OpenAsync(IBrowserContext session, string address)
    {
        var tab = await session.NewPageAsync();
        await tab.GotoAsync(address);
        await tab.EvaluateAsync("window.notReloaded = true");
        return tab;
    }

    private static Task<bool> WasNotReloadedAsync(IPage tab) => tab.EvaluateAsync<bool>("window.notReloaded === true");

    private static ILocator BacklogIntent(IPage backlog, string firstLine) =>
        backlog.Locator("[data-intent]", new PageLocatorOptions { HasText = firstLine });

    private static ILocator StartButton(IPage detail) => detail.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Start" });

    private static ILocator EditLink(IPage detail) => detail.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Edit" });

    private static ILocator DeleteLink(IPage detail) => detail.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Delete" });

    /// <summary>
    /// Waits until the Backlog in this tab listens for changes, so that a change after this can only reach it live: the first thing a
    /// live connection does is listen and read the Backlog, so a new Intent showing means it has.
    /// </summary>
    private static async Task AwaitBacklogConnectedAsync(RunningOverseer overseer, IPage backlog)
    {
        using var saved = await SaveAsync(overseer, "Backlog connected");
        await Expect(BacklogIntent(backlog, "Backlog connected")).ToBeVisibleAsync();
    }

    /// <summary>As <see cref="AwaitBacklogConnectedAsync"/>, for detail pages of a Draft: its edited text showing means they listen.</summary>
    private static async Task AwaitDetailConnectedAsync(RunningOverseer overseer, string address, string firstLine, params IPage[] details)
    {
        using var edited = await EditAsync(overseer, address, "save", $"{firstLine}\n\nAs CSV.", "acme/web");
        foreach (var detail in details)
        {
            await Expect(detail.GetByLabel("Intent text")).ToContainTextAsync("As CSV.");
        }
    }
}
