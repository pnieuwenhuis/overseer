using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Npgsql;

namespace Overseer.Persistence;

/// <summary>Writing a row whether or not it is there already, as a module does that records where each piece of its work stands.</summary>
public static class Upsert
{
    /// <summary>
    /// Updates the row that <paramref name="same"/> finds with <paramref name="update"/> when there is one, and inserts
    /// <paramref name="record"/> when there isn't. Should another insert come first meanwhile, this write is applied after it, as an
    /// update. Saves what else <paramref name="database"/> holds unsaved with the insert, and forgets it should another insert come
    /// first, so save that beforehand.
    /// </summary>
    /// <param name="record">The row as it should be, holding what <paramref name="update"/> sets.</param>
    public static async Task UpsertAsync<TRecord>(
        this OverseerDbContext database, TRecord record, Expression<Func<TRecord, bool>> same, Action<UpdateSettersBuilder<TRecord>> update,
        CancellationToken cancellationToken)
        where TRecord : class
    {
        Task<int> UpdateAsync() => database.Set<TRecord>().Where(same).ExecuteUpdateAsync(update, cancellationToken);

        if (await UpdateAsync() > 0)
        {
            return;
        }
        database.Set<TRecord>().Add(record);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            database.ChangeTracker.Clear();
            await UpdateAsync();
        }
    }
}
