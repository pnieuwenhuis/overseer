using System.Threading.Channels;
using Microsoft.Playwright;
using Overseer.Sandboxes;
using static Microsoft.Playwright.Assertions;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.SessionLogTests;

namespace Overseer.Tests;

/// <summary>Following a running Task in the browser, live, as its agent talks and calls its tools.</summary>
public class SessionLogBrowserTests(PostgresServer postgres, PortalChromium chromium) : IClassFixture<PortalChromium>
{
    [Fact]
    public async Task The_Task_list_and_the_Tasks_page_follow_its_agent_live_and_leaving_the_page_loses_nothing()
    {
        await using var overseer = await StartWithRepositoriesAsync(
            await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], environment: PortalChromium.ServerEnvironment);
        var live = Channel.CreateUnbounded<AgentUpdate>();
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Developer, new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done()) { Live = live.Reader }));
        var address = await ImplementationTests.AwaitingApprovalAsync(overseer, "Export data", "acme/web");
        await using var session = await chromium.LogInAsync(overseer);
        var tab = await session.NewPageAsync();
        await tab.GotoAsync(address);

        using var approved = await ImplementationTests.ApproveAsync(overseer, address, start: true);

        var activity = tab.Locator(".intent-task-activity");
        await Expect(tab.Locator(".intent-task-state")).ToHaveTextAsync(["Running"]);
        live.Writer.TryWrite(Says("I'll look at "));
        await Expect(activity).ToHaveTextAsync("I'll look at");
        live.Writer.TryWrite(Says("the orders."));
        await Expect(activity).ToHaveTextAsync("I'll look at the orders.");

        await tab.Locator("a.intent-task-title").ClickAsync();

        // From the session's start.
        var sessions = tab.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "Agent sessions", Exact = true });
        var entries = sessions.GetByRole(AriaRole.Listitem);
        await Expect(entries).ToHaveTextAsync(["I'll look at the orders."]);
        live.Writer.TryWrite(Calls("1", "bash"));
        await Expect(entries).ToHaveTextAsync(["I'll look at the orders.", "Tool call bash"]);
        live.Writer.TryWrite(Calls("1", "bash: npm test"));
        live.Writer.TryWrite(Says("The tests "));
        await Expect(entries).ToHaveTextAsync(["I'll look at the orders.", "Tool call bash: npm test", "The tests"]);
        live.Writer.TryWrite(Says("pass."));
        await Expect(entries).ToHaveTextAsync(["I'll look at the orders.", "Tool call bash: npm test", "The tests pass."]);

        await tab.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to the Intent" }).ClickAsync();
        await Expect(activity).ToHaveTextAsync("The tests pass.");
        live.Writer.TryWrite(Calls("2", "read: src/orders.ts"));
        await Expect(activity).ToHaveTextAsync("read: src/orders.ts");
        await tab.Locator("a.intent-task-title").ClickAsync();

        await Expect(entries).ToHaveTextAsync(
            ["I'll look at the orders.", "Tool call bash: npm test", "The tests pass.", "Tool call read: src/orders.ts"]);
        live.Writer.TryWrite(Says("Done."));
        await Expect(entries).ToHaveCountAsync(5);
        live.Writer.Complete();
        await Expect(tab.Locator(".intent-task-state")).ToHaveTextAsync(["Done"]);
        await Expect(entries).ToHaveCountAsync(5);
    }
}
