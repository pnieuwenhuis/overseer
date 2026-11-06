using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Overseer.Intents;
using Overseer.Persistence;
using static Overseer.Tests.IntentPortal;

namespace Overseer.Tests;

public class WorkingTimeTests(PostgresServer postgres)
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_Intent_already_Active_before_transitions_were_recorded_counts_its_Working_Time_from_the_upgrade()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var database = new OverseerDbContext(new DbContextOptionsBuilder<OverseerDbContext>().UseNpgsql(connectionString).Options))
        {
            await database.GetService<IMigrator>().MigrateAsync("20261105131458_AddRepositoriesInScope", TestContext.Current.CancellationToken);
            await database.Database.ExecuteSqlAsync($"""
                INSERT INTO "Intents" ("Id", "Text", "State", "CreatedAt") VALUES
                    ({Guid.NewGuid()}, 'Export data', 'Refining', {DateTimeOffset.UtcNow.AddDays(-1)}),
                    ({Guid.NewGuid()}, 'Import data', 'Draft', {DateTimeOffset.UtcNow.AddDays(-1)})
                """, TestContext.Current.CancellationToken);
        }

        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var overseer = await RunningOverseer.StartAsync(connectionString, time: time);
        await overseer.LogInAsync();
        // The upgrade happened moments ago, by the database's clock. The half minute absorbs that, and any difference between the clocks.
        time.SetUtcNow(DateTimeOffset.UtcNow.AddMinutes(90.5));

        Assert.Equal("1 h 30 min", ElementText(await GetAsync(overseer, await AddressAsync(overseer, "Export data")), "intent-working-time"));
        Assert.DoesNotContain("Working Time", await GetAsync(overseer, await AddressAsync(overseer, "Import data")));
    }

    [Fact]
    public async Task An_Intent_shows_the_time_it_has_been_Active_on_the_Backlog_and_its_detail_page_but_not_the_time_it_was_a_Draft()
    {
        var time = new FakeTimeProvider(Morning);
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        time.Advance(TimeSpan.FromHours(1));
        using var started = await StartAsync(overseer, address);

        time.Advance(TimeSpan.FromMinutes(135));

        Assert.Equal("2 h 15 min", ElementText(await GetAsync(overseer, "/"), "intent-working-time"));
        Assert.Equal("2 h 15 min", ElementText(await GetAsync(overseer, address), "intent-working-time"));
    }

    [Fact]
    public async Task A_Draft_shows_no_Working_Time()
    {
        var time = new FakeTimeProvider(Morning);
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        time.Advance(TimeSpan.FromHours(3));

        Assert.DoesNotContain("Working Time", await GetAsync(overseer, "/"));
        Assert.DoesNotContain("Working Time", await GetAsync(overseer, address));
    }

    [Fact]
    public async Task The_transitions_survive_a_restart_and_an_Active_Intent_goes_on_counting_up_to_now()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        var time = new FakeTimeProvider(Morning);
        string address;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"], time))
        {
            using var saved = await SaveAsync(first, "Export data", "acme/web");
            address = await AddressAsync(first, "Export data");
            time.Advance(TimeSpan.FromMinutes(20));
            using var started = await StartAsync(first, address);
            time.Advance(TimeSpan.FromMinutes(40));
        }

        time.Advance(TimeSpan.FromMinutes(50));
        await using var second = await RunningOverseer.StartAsync(connectionString, time: time);
        await second.LogInAsync();

        Assert.Equal("1 h 30 min", ElementText(await GetAsync(second, address), "intent-working-time"));
    }

    [Fact]
    public async Task Working_Time_adds_up_the_periods_in_Active_states_and_leaves_out_awaiting_approval_Approved_and_Paused()
    {
        var time = new FakeTimeProvider(Morning);
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);

        time.Advance(TimeSpan.FromMinutes(40));
        await TransitionAsync(overseer, "Export data", IntentState.AwaitingSpecApproval, time);
        time.Advance(TimeSpan.FromHours(5));
        await TransitionAsync(overseer, "Export data", IntentState.Approved, time);
        time.Advance(TimeSpan.FromHours(16));
        await TransitionAsync(overseer, "Export data", IntentState.Implementing, time);
        time.Advance(TimeSpan.FromMinutes(50));
        await TransitionAsync(overseer, "Export data", IntentState.Paused, time);
        time.Advance(TimeSpan.FromDays(2));
        await TransitionAsync(overseer, "Export data", IntentState.Implementing, time);
        time.Advance(TimeSpan.FromMinutes(25));
        await TransitionAsync(overseer, "Export data", IntentState.InReview, time);
        time.Advance(TimeSpan.FromMinutes(10));
        await TransitionAsync(overseer, "Export data", IntentState.Done, time);
        time.Advance(TimeSpan.FromDays(1));

        // 40 min Refining, 50 + 25 min Implementing and 10 min In Review.
        Assert.Equal("2 h 5 min", ElementText(await GetAsync(overseer, "/?filter=closed"), "intent-working-time"));
        Assert.Equal("2 h 5 min", ElementText(await GetAsync(overseer, address), "intent-working-time"));
    }

    [Theory]
    [InlineData(0, "0 min")]
    [InlineData(59, "0 min")]
    [InlineData(45 * 60 + 59, "45 min")]
    [InlineData(3 * 3600, "3 h")]
    [InlineData(26 * 3600 + 5 * 60, "26 h 5 min")]
    public async Task Working_Time_reads_in_whole_hours_and_minutes(int seconds, string shown)
    {
        var time = new FakeTimeProvider(Morning);
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);

        time.Advance(TimeSpan.FromSeconds(seconds));

        Assert.Equal(shown, ElementText(await GetAsync(overseer, address), "intent-working-time"));
    }
}
