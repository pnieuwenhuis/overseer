using Microsoft.EntityFrameworkCore;

namespace Overseer.Tests;

public class ServeTests(PostgresServer postgres)
{
    [Fact]
    public async Task The_Portal_shows_the_running_version()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());

        var page = await overseer.Http.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains($"Overseer {OverseerVersion.Current}", page);
    }

    [Fact]
    public async Task Starting_on_an_empty_database_applies_the_schema()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());

        await using var database = overseer.OpenDatabase();
        Assert.NotEmpty(await database.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await database.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Starting_again_on_an_up_to_date_database_changes_nothing()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        IEnumerable<string> appliedAfterFirstStart;
        await using (var first = await RunningOverseer.StartAsync(connectionString))
        {
            await using var database = first.OpenDatabase();
            appliedAfterFirstStart = await database.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        }

        await using var second = await RunningOverseer.StartAsync(connectionString);

        await using var databaseAfterSecondStart = second.OpenDatabase();
        Assert.Equal(appliedAfterFirstStart, await databaseAfterSecondStart.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
    }
}
