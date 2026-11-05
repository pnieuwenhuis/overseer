using Microsoft.EntityFrameworkCore;
using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Persistence;

namespace Overseer.Repositories;

/// <summary>
/// The Configured Repositories, kept in PostgreSQL as <c>owner/name</c> in GitHub's case, unique regardless of case. A repository is added only once
/// the GitHub Gateway has confirmed that the GitHub token can reach it.
/// </summary>
public sealed class ConfiguredRepositories(IDbContextFactory<OverseerDbContext> databases, IGitHubGateway gitHub)
{
    /// <summary>Every Configured Repository, by owner and name.</summary>
    public async Task<IReadOnlyList<RepositoryName>> ListAsync(CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return ParseSortedByName(await database.ConfiguredRepositories.Select(repository => repository.FullName).ToListAsync(cancellationToken));
    }

    /// <summary>Stored repositories in the order the Portal lists them: by owner and name, regardless of case.</summary>
    internal static IReadOnlyList<RepositoryName> ParseSortedByName(IEnumerable<string> stored) =>
        [.. stored.Select(RepositoryName.Parse).OrderBy(repository => repository.ToString(), StringComparer.OrdinalIgnoreCase)];

    /// <summary>Adds the repository the user entered, as <c>owner/name</c> or a GitHub URL, once GitHub confirms the token can reach it.</summary>
    public async Task<RepositoryAdding> AddAsync(string? input, CancellationToken cancellationToken)
    {
        if (!RepositoryName.TryParse(input, out var entered))
        {
            return new RepositoryAdding.Refused(RepositoryName.NotARepositoryError);
        }
        switch (await gitHub.CheckRepositoryAsync(entered, cancellationToken))
        {
            case null:
                return new RepositoryAdding.Refused(
                    "Set a GitHub token first: Overseer adds a repository only once GitHub confirms the token can reach it.");
            case GitHubRepositoryCheck.NotFound:
                return new RepositoryAdding.Refused(
                    $"GitHub has no repository {entered} that the GitHub token can reach: it doesn't exist, or the token has no access to it.");
            case GitHubRepositoryCheck.Fails fails:
                return new RepositoryAdding.Refused($"GitHub could not confirm that the GitHub token can reach {entered}: {fails.Error}");
            case GitHubRepositoryCheck.Reachable reachable:
                await using (var database = await databases.CreateDbContextAsync(cancellationToken))
                {
                    return await InsertAsync(database, reachable.Repository, cancellationToken)
                        ? new RepositoryAdding.Added(reachable.Repository)
                        : new RepositoryAdding.Refused($"{reachable.Repository} is already a Configured Repository.");
                }
            default:
                throw new InvalidOperationException("GitHub either reaches the repository, doesn't find it, or fails.");
        }
    }

    /// <summary>
    /// Makes <paramref name="repository"/>, as GitHub names it, a Configured Repository, in <paramref name="database"/>'s
    /// transaction if it has one.
    /// </summary>
    /// <returns>Whether it was added; <c>false</c> when it was already configured, in any case.</returns>
    internal static async Task<bool> InsertAsync(OverseerDbContext database, RepositoryName repository, CancellationToken cancellationToken)
    {
        var fullName = repository.ToString();
        // The key ignores case, as GitHub does, so it refuses the same repository in any case, and settles two adds at once.
        return await database.Database.ExecuteSqlAsync(
            $"""INSERT INTO "ConfiguredRepositories" ("FullName") VALUES ({fullName}) ON CONFLICT ("FullName") DO NOTHING""",
            cancellationToken) > 0;
    }

    /// <summary>
    /// Which of <paramref name="repositories"/>, in any case, are configured, each once as it is configured. They stay configured until
    /// <paramref name="database"/>'s transaction ends, for <see cref="RemoveAsync"/> waits for it; a removal under way is waited for
    /// instead, and its repository not found.
    /// </summary>
    internal static Task<IReadOnlyList<RepositoryName>> HoldThoseConfiguredAsync(OverseerDbContext database, IEnumerable<RepositoryName> repositories, CancellationToken cancellationToken) =>
        LockThoseConfiguredAsync(database, repositories, $"{SelectThoseConfigured} FOR SHARE", cancellationToken);

    private const string SelectThoseConfigured = """SELECT "FullName" AS "Value" FROM "ConfiguredRepositories" WHERE "FullName" = ANY({0})""";

    /// <summary>Which of <paramref name="repositories"/>, in any case, are configured, each once as it is configured, their rows locked.</summary>
    /// <param name="lockingQuery"><see cref="SelectThoseConfigured"/> with its locking clause, as a constant: this runs it as it is.</param>
    private static async Task<IReadOnlyList<RepositoryName>> LockThoseConfiguredAsync(
        OverseerDbContext database, IEnumerable<RepositoryName> repositories, string lockingQuery, CancellationToken cancellationToken)
    {
        string[] fullNames = [.. repositories.Select(repository => repository.ToString())];
        var locked = await database.Database.SqlQueryRaw<string>(lockingQuery, [fullNames]).ToListAsync(cancellationToken);
        return [.. locked.Select(RepositoryName.Parse)];
    }

    /// <summary>Says that <paramref name="repositories"/> have gone from the Configured Repositories, without a full stop at the end.</summary>
    internal static string NoLongerConfigured(IReadOnlyList<string> repositories) =>
        Prose.Agreeing(repositories,
            one: repository => $"{repository} is no longer a Configured Repository",
            many: list => $"{list} are no longer Configured Repositories");

    /// <summary>
    /// Removes <paramref name="repository"/> from the Configured Repositories, unless an open Intent has it in scope. An Intent that is
    /// Done or Cancelled doesn't hold it back, and keeps its history.
    /// </summary>
    public async Task<RepositoryRemoval> RemoveAsync(RepositoryName repository, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // Locked before looking at the Intents: a capture that holds the repository has committed by the time this gets the lock, so
        // the Intent is seen, and one that comes later waits for this transaction and doesn't find the repository (see HoldThoseConfiguredAsync).
        if (await LockThoseConfiguredAsync(database, [repository], $"{SelectThoseConfigured} FOR UPDATE", cancellationToken) is not [var configured])
        {
            return new RepositoryRemoval.NotConfigured();
        }

        if (await IntentLifecycle.DescribeOpenWithInScopeAsync(database, configured, cancellationToken) is { } holdingBack)
        {
            return new RepositoryRemoval.Refused($"{configured} can't be removed: {holdingBack}.");
        }

        var fullName = configured.ToString();
        await database.ConfiguredRepositories.Where(record => record.FullName == fullName).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RepositoryRemoval.Removed(configured);
    }
}

/// <summary>What removing a Configured Repository did.</summary>
public abstract record RepositoryRemoval
{
    private RepositoryRemoval()
    {
    }

    /// <summary>The repository is no longer configured, and this is how it was named.</summary>
    public sealed record Removed(RepositoryName Repository) : RepositoryRemoval;

    /// <summary>There is no such Configured Repository, for instance because another tab removed it first.</summary>
    public sealed record NotConfigured : RepositoryRemoval;

    /// <summary>The repository stays configured; <paramref name="Error"/> says why.</summary>
    public sealed record Refused(string Error) : RepositoryRemoval;
}

/// <summary>What adding a Configured Repository did.</summary>
public abstract record RepositoryAdding
{
    private RepositoryAdding()
    {
    }

    /// <summary>The repository is now configured, as GitHub names it.</summary>
    public sealed record Added(RepositoryName Repository) : RepositoryAdding;

    /// <summary>Nothing was added; <paramref name="Error"/> says why.</summary>
    public sealed record Refused(string Error) : RepositoryAdding;
}

/// <summary>A Configured Repository, as PostgreSQL holds it.</summary>
public sealed class ConfiguredRepositoryRecord
{
    /// <summary><c>owner/name</c>, in GitHub's case.</summary>
    public required string FullName { get; init; }
}
