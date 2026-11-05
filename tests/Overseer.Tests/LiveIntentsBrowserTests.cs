using Microsoft.Playwright;
using Overseer.Intents;
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
        var waiting = await OpenAsync(session, secondAddress);
        await AwaitDetailConnectedAsync(overseer, secondAddress, "Import data", waiting);
        await Expect(StartButton(waiting)).ToBeEnabledAsync();

        using var started = await overseer.SubmitFormAsync(firstAddress, "start-intent", []);

        await Expect(StartButton(waiting)).ToBeDisabledAsync();
        await Expect(waiting.GetByRole(AriaRole.Alert)).ToContainTextAsync("All Active Intent slots are taken, by \"Export data\".");
        await Expect(waiting.Locator(".intent-state")).ToHaveTextAsync("Draft");
        Assert.True(await WasNotReloadedAsync(waiting));
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

    private async Task<RunningOverseer> StartOverseerAsync() =>
        await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], environment: PortalChromium.ServerEnvironment);

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
