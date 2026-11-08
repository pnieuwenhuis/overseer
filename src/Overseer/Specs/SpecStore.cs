using Microsoft.EntityFrameworkCore;
using Overseer.GitHub;
using Overseer.Persistence;

namespace Overseer.Specs;

/// <summary>
/// The Spec versions of each Intent, kept in PostgreSQL: the Analyst's Spec is version 1, and each later change adds a version,
/// keeping those before it.
/// </summary>
public sealed class SpecStore(IDbContextFactory<OverseerDbContext> databases)
{
    /// <summary>The latest version of the Intent's Spec; <c>null</c> when it has none.</summary>
    public async Task<SpecVersion?> LatestOfAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return await ReadFirstAsync(
            database.SpecVersions.Where(version => version.IntentId == intentId).OrderByDescending(version => version.Number), cancellationToken);
    }

    /// <summary>Version <paramref name="number"/> of the Intent's Spec; <c>null</c> when it has no such version.</summary>
    public async Task<SpecVersion?> VersionOfAsync(Guid intentId, int number, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return await ReadFirstAsync(
            database.SpecVersions.Where(version => version.IntentId == intentId && version.Number == number), cancellationToken);
    }

    /// <summary>Every version of the Intent's Spec, in order; empty when it has none.</summary>
    public async Task<IReadOnlyList<SpecVersionHeading>> VersionsOfAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return await database.SpecVersions
            .Where(version => version.IntentId == intentId)
            .OrderBy(version => version.Number)
            .Select(version => new SpecVersionHeading(version.Number, version.WrittenBy))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The number of the latest version of the Intent's Spec, as <paramref name="database"/> sees it; <c>null</c> when it has none.</summary>
    internal static Task<int?> LatestNumberAsync(OverseerDbContext database, Guid intentId, CancellationToken cancellationToken) =>
        database.SpecVersions.Where(version => version.IntentId == intentId).MaxAsync(version => (int?)version.Number, cancellationToken);

    /// <summary>
    /// Adds <paramref name="spec"/> as the Intent's next Spec version in <paramref name="database"/>, to be saved with its
    /// transaction. Its Tasks are numbered in their order.
    /// </summary>
    internal static async Task AddVersionAsync(
        OverseerDbContext database, Guid intentId, Spec spec, SpecAuthor writtenBy, DateTimeOffset writtenAt, CancellationToken cancellationToken)
    {
        // Under the lock on the Intent's row, which whoever adds a version holds, so that no other version is added meanwhile.
        var number = 1 + (await LatestNumberAsync(database, intentId, cancellationToken) ?? 0);
        database.SpecVersions.Add(new SpecVersionRecord
        {
            IntentId = intentId,
            Number = number,
            WrittenAt = writtenAt,
            WrittenBy = writtenBy,
            Summary = spec.Summary,
            Approach = spec.Approach,
            AcceptanceCriteria = spec.AcceptanceCriteria,
            Dependencies = spec.Dependencies,
            ApiEndpointChanges = spec.ApiEndpointChanges,
            DataModelChanges = spec.DataModelChanges,
            UiChanges = spec.UiChanges,
            Decisions = [.. spec.Decisions.Select((decision, index) => new SpecDecisionRecord
            {
                IntentId = intentId,
                SpecVersion = number,
                Number = index + 1,
                Text = decision.Text,
                IsAnalystsChoice = decision.IsAnalystsChoice,
            })],
            Tasks = [.. spec.Tasks.Select((task, index) => new SpecTaskRecord
            {
                IntentId = intentId,
                SpecVersion = number,
                Number = index + 1,
                Title = task.Title,
                Description = task.Description,
                Role = task.Role,
                Repositories = [.. task.Repositories.Select(repository => repository.ToString())],
            })],
        });
    }

    /// <summary>The first of <paramref name="versions"/>, with its decisions and Tasks; <c>null</c> when there is none.</summary>
    private static async Task<SpecVersion?> ReadFirstAsync(IQueryable<SpecVersionRecord> versions, CancellationToken cancellationToken)
    {
        var stored = await versions
            .Include(version => version.Decisions)
            .Include(version => version.Tasks)
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        return stored is null
            ? null
            : new SpecVersion(stored.Number, stored.WrittenBy, new Spec(
                stored.Summary,
                stored.Approach,
                [.. stored.Decisions.OrderBy(decision => decision.Number).Select(decision => new SpecDecision(decision.Text, decision.IsAnalystsChoice))],
                stored.AcceptanceCriteria,
                stored.Dependencies,
                stored.ApiEndpointChanges,
                stored.DataModelChanges,
                stored.UiChanges,
                [.. stored.Tasks.OrderBy(task => task.Number).Select(task => new SpecTask(
                    task.Number, task.Title, task.Description, task.Role, [.. task.Repositories.Select(RepositoryName.Parse)]))]));
    }
}
