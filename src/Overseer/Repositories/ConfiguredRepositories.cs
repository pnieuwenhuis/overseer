using Microsoft.EntityFrameworkCore;
using Overseer.GitHub;
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
        var stored = await database.ConfiguredRepositories.Select(repository => repository.FullName).ToListAsync(cancellationToken);
        return [.. stored.Select(RepositoryName.Parse).OrderBy(repository => repository.ToString(), StringComparer.OrdinalIgnoreCase)];
    }

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

    /// <summary>Removes <paramref name="repository"/> from the Configured Repositories.</summary>
    /// <returns>Whether it was configured.</returns>
    public async Task<bool> RemoveAsync(RepositoryName repository, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var fullName = repository.ToString();
        return await database.ConfiguredRepositories.Where(configured => configured.FullName == fullName).ExecuteDeleteAsync(cancellationToken) > 0;
    }
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
