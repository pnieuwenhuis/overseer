using Microsoft.EntityFrameworkCore;

namespace Overseer.Tests;

/// <summary>Holding up what the server writes with database locks, to have two requests meet at the moment that matters.</summary>
public static class DatabaseRaces
{
    /// <summary>
    /// Runs <paramref name="first"/>, held up by <paramref name="tableLock"/>, which lets the table's reads through but not its writes,
    /// then <paramref name="second"/>, which gets the chance to overtake it, and lets both finish.
    /// </summary>
    public static async Task<(HttpResponseMessage First, HttpResponseMessage Second)> RaceAsync(
        RunningOverseer overseer, FormattableString tableLock, Func<Task<HttpResponseMessage>> first, Func<Task<HttpResponseMessage>> second)
    {
        await using var holder = overseer.OpenDatabase();
        await using var transaction = await holder.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await holder.Database.ExecuteSqlAsync(tableLock, TestContext.Current.CancellationToken);
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watching.CancelAfter(TimeSpan.FromSeconds(30));

        var firstDone = first();
        await Task.WhenAny(firstDone, UntilWaitingOnLocksAsync(overseer, 1, watching.Token));
        var secondDone = second();
        await Task.WhenAny(secondDone, UntilWaitingOnLocksAsync(overseer, 2, watching.Token));
        await watching.CancelAsync();

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        return (await firstDone, await secondDone);
    }

    /// <summary>Returns once this many of the database's connections wait for a lock.</summary>
    public static async Task UntilWaitingOnLocksAsync(RunningOverseer overseer, int connections, CancellationToken cancellationToken)
    {
        await using var database = overseer.OpenDatabase();
        while (await database.Database.SqlQuery<int>(
            $"""SELECT count(*)::int AS "Value" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'""")
            .SingleAsync(cancellationToken) < connections)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }
}
