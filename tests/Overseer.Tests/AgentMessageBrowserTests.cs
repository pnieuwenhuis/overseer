using System.Threading.Channels;
using Microsoft.Playwright;
using Overseer.Sandboxes;
using static Microsoft.Playwright.Assertions;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.SessionLogTests;

namespace Overseer.Tests;

/// <summary>Messaging the running agent in the browser, from the stream of its Task as it goes on.</summary>
public class AgentMessageBrowserTests(PostgresServer postgres, PortalChromium chromium) : IClassFixture<PortalChromium>
{
    [Fact]
    public async Task A_message_written_while_the_stream_goes_on_shows_as_waiting_then_as_delivered_and_the_box_goes_with_the_session()
    {
        await using var overseer = await StartWithRepositoriesAsync(
            await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], environment: PortalChromium.ServerEnvironment);
        var first = Channel.CreateUnbounded<AgentUpdate>();
        var second = Channel.CreateUnbounded<AgentUpdate>();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(
            new FakeAgentTurn([], new AgentOutcome.Done()) { Live = first.Reader },
            new FakeAgentTurn([], new AgentOutcome.Done()) { Live = second.Reader }));
        var address = await ImplementationTests.AwaitingApprovalAsync(overseer, "Export data", "acme/web");
        using var approved = await ImplementationTests.ApproveAsync(overseer, address, start: true);
        await TaskTests.StatesAsync(overseer, address, "Running");
        await using var session = await chromium.LogInAsync(overseer);
        var tab = await session.NewPageAsync();
        await tab.GotoAsync(address + "/tasks/1");
        var sessions = tab.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "Agent sessions", Exact = true });
        var entries = sessions.GetByRole(AriaRole.Listitem);
        var box = tab.GetByLabel("Message to the agent");
        first.Writer.TryWrite(Says("Adding it."));
        await Expect(entries).ToHaveTextAsync(["Adding it."]);

        // What is being written stays while the stream grows.
        await box.FillAsync("Call it Download.");
        first.Writer.TryWrite(Calls("1", "bash: npm test"));
        await Expect(entries).ToHaveTextAsync(["Adding it.", "Tool call bash: npm test"]);
        await Expect(box).ToHaveValueAsync("Call it Download.");
        await tab.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Send message" }).ClickAsync();

        await Expect(entries).ToHaveTextAsync(
            ["Adding it.", "Tool call bash: npm test", "You: waiting to be delivered Call it Download."]);
        await Expect(box).ToHaveValueAsync("");
        first.Writer.Complete();

        await Expect(entries).ToHaveTextAsync(["Adding it.", "Tool call bash: npm test", "You Call it Download."]);
        second.Writer.TryWrite(Says("Renamed it."));
        await Expect(entries).ToHaveTextAsync(
            ["Adding it.", "Tool call bash: npm test", "You Call it Download.", "Renamed it."]);
        await Expect(box).ToBeVisibleAsync();
        second.Writer.Complete();

        await Expect(tab.Locator(".intent-task-state")).ToHaveTextAsync(["Done"]);
        await Expect(box).ToHaveCountAsync(0);
        await Expect(entries).ToHaveCountAsync(4);
    }
}
