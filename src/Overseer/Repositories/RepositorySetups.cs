using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Overseer.GitHub;
using Overseer.Persistence;

namespace Overseer.Repositories;

/// <summary>
/// Repository Setups, kept in PostgreSQL: each creates a new GitHub repository through the GitHub Gateway and makes it a
/// Configured Repository, and keeps the purpose and tech stack the user stated for the agent that writes its starter
/// files. A Repository Setup is not an Intent, and takes no Active Intent slot.
/// </summary>
public sealed class RepositorySetups(IDbContextFactory<OverseerDbContext> databases, IGitHubGateway gitHub, TimeProvider time)
{
    /// <summary>Every Repository Setup, the latest first.</summary>
    public async Task<IReadOnlyList<RepositorySetup>> ListAsync(CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var stored = await database.RepositorySetups.OrderByDescending(setup => setup.StartedAt).ToListAsync(cancellationToken);
        return [.. stored.Select(setup => new RepositorySetup(
            setup.Id, RepositoryName.Parse(setup.Repository), setup.Purpose, setup.TechStack, setup.StartedAt))];
    }

    /// <summary>
    /// Starts a Repository Setup for the new repository the user described: GitHub creates it, and only then is it
    /// registered and the Repository Setup kept.
    /// </summary>
    public async Task<RepositorySetupStarting> StartAsync(
        string? owner, string? name, string? purpose, string? techStack, CancellationToken cancellationToken)
    {
        owner = owner?.Trim() ?? "";
        name = name?.Trim() ?? "";
        // Kept with the line breaks a browser submits, CRLF, as LF.
        purpose = purpose?.Trim().ReplaceLineEndings("\n") ?? "";
        techStack = techStack?.Trim().ReplaceLineEndings("\n") ?? "";
        if (owner == "" || name == "" || purpose == "" || techStack == "")
        {
            return new RepositorySetupStarting.Refused("Enter the new repository's owner, name, purpose and tech stack.");
        }
        if (!RepositoryName.IsValidOwner(owner))
        {
            return new RepositorySetupStarting.Refused(RepositoryName.InvalidOwnerError);
        }
        if (!RepositoryName.IsValidName(name))
        {
            return new RepositorySetupStarting.Refused(RepositoryName.InvalidNameError);
        }

        var repository = new RepositoryName(owner, name);
        switch (await gitHub.CreateRepositoryAsync(repository, cancellationToken))
        {
            case GitHubRepositoryCreation.Created created:
                var setup = new RepositorySetup(Guid.NewGuid(), created.Repository, purpose, techStack, time.GetUtcNow());
                try
                {
                    // The repository exists on GitHub now, so it is registered even if the user stops waiting.
                    await KeepAsync(setup, CancellationToken.None);
                }
                catch (Exception exception) when (exception is DbException or DbUpdateException)
                {
                    return new RepositorySetupStarting.Refused(
                        $"GitHub created {created.Repository}, but Overseer could not register it: {exception.Message} " +
                        "Add it as a Configured Repository to use it.");
                }
                return new RepositorySetupStarting.Started(setup);
            case null:
                return new RepositorySetupStarting.Refused("Set a GitHub token first: Overseer creates the repository on GitHub with it.");
            case GitHubRepositoryCreation.Fails fails:
                return new RepositorySetupStarting.Refused($"GitHub did not create {repository}: {fails.Error}");
            default:
                throw new InvalidOperationException("GitHub either creates the repository or fails.");
        }
    }

    /// <summary>Registers the Repository Setup's repository as a Configured Repository and keeps the Repository Setup, at once.</summary>
    private async Task KeepAsync(RepositorySetup setup, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // Already configured only if a repository of this name was configured, deleted on GitHub and now created again:
        // it is the same Configured Repository.
        await ConfiguredRepositories.InsertAsync(database, setup.Repository, cancellationToken);
        database.RepositorySetups.Add(new RepositorySetupRecord
        {
            Id = setup.Id,
            Repository = setup.Repository.ToString(),
            Purpose = setup.Purpose,
            TechStack = setup.TechStack,
            StartedAt = setup.StartedAt,
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>A Repository Setup: the new repository, as GitHub names it, and what the user stated it is for and built with.</summary>
public sealed record RepositorySetup(Guid Id, RepositoryName Repository, string Purpose, string TechStack, DateTimeOffset StartedAt);

/// <summary>What starting a Repository Setup did.</summary>
public abstract record RepositorySetupStarting
{
    private RepositorySetupStarting()
    {
    }

    /// <summary>GitHub created the repository, it is now a Configured Repository, and the Repository Setup is kept.</summary>
    public sealed record Started(RepositorySetup Setup) : RepositorySetupStarting;

    /// <summary>Nothing was created, registered or kept; <paramref name="Error"/> says why.</summary>
    public sealed record Refused(string Error) : RepositorySetupStarting;
}

/// <summary>A Repository Setup, as PostgreSQL holds it.</summary>
public sealed class RepositorySetupRecord
{
    public required Guid Id { get; init; }

    /// <summary><c>owner/name</c>, in GitHub's case.</summary>
    public required string Repository { get; init; }

    public required string Purpose { get; init; }
    public required string TechStack { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
}
