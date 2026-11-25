using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Overseer.Intents;
using Overseer.Persistence;
using Overseer.Sandboxes;
using Overseer.Settings;
using static Overseer.Tests.IntentPortal;

namespace Overseer.Tests;

public class IntentLightTests(PostgresServer postgres)
{
    [Fact]
    public async Task A_Draft_has_no_light_and_starting_it_turns_it_green_with_a_label_in_the_Backlog_and_on_its_detail_page()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        Assert.Null(LightOn(await GetAsync(overseer, "/")));
        Assert.Null(LightOn(await GetAsync(overseer, address)));

        using var started = await overseer.SubmitFormAsync(address, "start-intent", []);

        // The label says it without the colour.
        Assert.Equal(("Green", "Working"), LightOn(await GetAsync(overseer, "/")));
        Assert.Equal(("Green", "Working"), LightOn(await GetAsync(overseer, address)));
    }

    [Fact]
    public async Task A_failed_Refinement_and_a_failed_implementation_each_turn_their_Intents_light_red()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var implementing = await ImplementationTests.AwaitingApprovalAsync(overseer, "Export data", "acme/web");
        overseer.Sandboxes.FailNextCreation("QuotaExceeded");
        using var approved = await ImplementationTests.ApproveAsync(overseer, implementing, start: true);
        await ImplementationTests.FailedAsync(overseer, implementing);
        overseer.Sandboxes.FailNextCreation("QuotaExceeded");
        using var saved = await SaveAsync(overseer, "Import data", "acme/web");
        var refining = await AddressAsync(overseer, "Import data");

        using var started = await StartAsync(overseer, refining);

        await RefinementTests.FailedAsync(overseer, refining);
        Assert.Equal(("Red", "Failed"), LightOn(await GetAsync(overseer, refining)));
        Assert.Equal(("Red", "Failed"), LightOn(await GetAsync(overseer, implementing)));
        Assert.Equal(2, (await GetAsync(overseer, "/")).Split("data-light=\"Red\"").Length - 1);
    }

    // Only Refinement asks for the human's attention so far, so the modules here are the test's own.
    [Fact]
    public async Task What_any_module_reports_as_failed_or_as_needing_attention_reaches_the_Intents_light_and_a_failure_outweighs_it()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        string[] texts = ["First fails", "Second fails", "First asks", "Second asks", "Fails and asks", "Neither"];
        foreach (var text in texts)
        {
            using var saved = await SaveAsync(overseer, text, "acme/web");
        }
        Dictionary<string, Guid> ids;
        await using (var database = overseer.OpenDatabase())
        {
            ids = await database.Intents.ToDictionaryAsync(intent => intent.Text, intent => intent.Id, TestContext.Current.CancellationToken);
        }
        var lifecycle = new IntentLifecycle(
            overseer.Services.GetRequiredService<IDbContextFactory<OverseerDbContext>>(),
            overseer.Services.GetRequiredService<SettingsStore>(),
            TimeProvider.System,
            [new Reporting(ids["First fails"]), new Reporting(ids["Second fails"], ids["Fails and asks"])],
            [new Reporting(ids["First asks"], ids["Fails and asks"]), new Reporting(ids["Second asks"])]);

        var intents = await lifecycle.ListBacklogAsync(BacklogFilter.Open, TestContext.Current.CancellationToken);

        Assert.Equal(
            new IntentLight?[] { IntentLight.Red, IntentLight.Red, IntentLight.Orange, IntentLight.Orange, IntentLight.Red, null },
            texts.Select(text => IntentLifecycle.LightOf(intents.Single(intent => intent.Text == text))));
    }

    // Only the states in which nothing runs. The tickets that make the others reachable decide their light, and test it.
    [Theory]
    [InlineData(IntentState.Draft)]
    [InlineData(IntentState.Approved)]
    [InlineData(IntentState.Paused)]
    [InlineData(IntentState.Done)]
    [InlineData(IntentState.Cancelled)]
    public async Task An_Intent_in_a_state_in_which_nothing_runs_shows_no_light_in_the_Backlog_or_on_its_detail_page(IntentState state)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        await SetStateAsync(overseer, "Export data", state);
        var backlog = await GetAsync(overseer, state is IntentState.Done or IntentState.Cancelled ? "/?filter=closed" : "/");

        Assert.Equal("Export data", Assert.Single(Backlog(backlog)).FirstLine);
        Assert.Null(LightOn(backlog));
        Assert.Null(LightOn(await GetAsync(overseer, address)));
    }

    /// <summary>A module that reports these Intents, as one that fails them or one that waits for the human on them.</summary>
    private sealed class Reporting(params Guid[] reported) : IIntentFailures, IIntentAttention
    {
        public IQueryable<Guid> FailedIn(OverseerDbContext database) => Reported(database);

        public IQueryable<Guid> NeedingAttentionIn(OverseerDbContext database) => Reported(database);

        private IQueryable<Guid> Reported(OverseerDbContext database) =>
            database.Intents.Where(intent => reported.Contains(intent.Id)).Select(intent => intent.Id);
    }
}
