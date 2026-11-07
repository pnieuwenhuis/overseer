using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Overseer.GitHub;
using Overseer.Persistence;
using Overseer.Sandboxes;

namespace Overseer.Repositories;

/// <summary>
/// Repository Setups, kept in PostgreSQL: each creates a new GitHub repository through the GitHub Gateway and makes it a
/// Configured Repository, then has an agent write its starter files in the background (<see cref="StarterFiles"/>). A
/// Repository Setup is not an Intent, and takes no Active Intent slot.
/// </summary>
public sealed class RepositorySetups(
    IDbContextFactory<OverseerDbContext> databases, IGitHubGateway gitHub, StarterFiles starterFiles, SandboxRegistry sandboxes,
    TimeProvider time, ILogger<RepositorySetups> logger) : IHostedService
{
    private readonly SandboxedWork _work = new(sandboxes, SandboxOwnerKind.RepositorySetup, logger);

    /// <summary>
    /// Marks the Repository Setups that a stop of serve interrupted as failed, so they can be retried, and destroys the
    /// Sandboxes they left behind. Runs as serve starts, before the Portal takes requests, so no Retry comes first.
    /// </summary>
    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            var interrupted = await database.RepositorySetups
                .Where(setup => setup.Phase == RepositorySetupPhase.ProvisioningSandbox || setup.Phase == RepositorySetupPhase.AgentWorking)
                .ToListAsync(cancellationToken);
            foreach (var setup in interrupted)
            {
                setup.Error = setup.Phase == RepositorySetupPhase.ProvisioningSandbox
                    ? "Overseer restarted while a Sandbox was being provisioned for the agent. Retry to start the agent again."
                    : "Overseer restarted while the agent was writing the starter files. Retry to start the agent again.";
                setup.Phase = RepositorySetupPhase.Failed;
            }
            await database.SaveChangesAsync(cancellationToken);
        }

        // No Repository Setup runs yet, so every Sandbox one owns is left over.
        await _work.DestroyLeftOverAsync(cancellationToken);
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => _work.StopAsync(cancellationToken);

    /// <summary>
    /// Raised whenever a Repository Setup starts or changes phase, on the thread that changed it. Handlers must not
    /// throw; read the Repository Setups again with <see cref="ListAsync"/>.
    /// </summary>
    public event Action? Changed;

    /// <summary>Every Repository Setup, the latest first.</summary>
    public async Task<IReadOnlyList<RepositorySetup>> ListAsync(CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var stored = await database.RepositorySetups.OrderByDescending(setup => setup.StartedAt).ToListAsync(cancellationToken);
        return [.. stored.Select(ToSetup)];
    }

    private static RepositorySetup ToSetup(RepositorySetupRecord stored) => new(
        stored.Id, RepositoryName.Parse(stored.Repository), stored.Purpose, stored.TechStack, stored.StartedAt, stored.Phase, stored.Error);

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
                var setup = new RepositorySetup(
                    Guid.NewGuid(), created.Repository, purpose, techStack, time.GetUtcNow(), RepositorySetupPhase.ProvisioningSandbox, Error: null);
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
                Changed?.Invoke();
                Run(setup);
                return new RepositorySetupStarting.Started(setup);
            case null:
                return new RepositorySetupStarting.Refused("Set a GitHub token first: Overseer creates the repository on GitHub with it.");
            case GitHubRepositoryCreation.Fails fails:
                return new RepositorySetupStarting.Refused($"GitHub did not create {repository}: {fails.Error}");
            default:
                throw new InvalidOperationException("GitHub either creates the repository or fails.");
        }
    }

    /// <summary>
    /// Starts the agent part of a failed Repository Setup again, in a new Sandbox. The repository GitHub created is kept,
    /// with whatever the agent pushed to it.
    /// </summary>
    /// <returns>Whether it started again: not when no such Repository Setup exists, or it hasn't failed.</returns>
    public async Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken)
    {
        RepositorySetup setup;
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            // Only one of two Retries pressed at once finds it failed.
            var retried = await database.RepositorySetups
                .Where(record => record.Id == id && record.Phase == RepositorySetupPhase.Failed)
                .ExecuteUpdateAsync(record => record
                    .SetProperty(r => r.Phase, RepositorySetupPhase.ProvisioningSandbox)
                    .SetProperty(r => r.Error, (string?)null), cancellationToken);
            if (retried == 0)
            {
                return false;
            }
            var stored = await database.RepositorySetups.AsNoTracking().SingleAsync(record => record.Id == id, CancellationToken.None);
            setup = ToSetup(stored);
        }
        Changed?.Invoke();
        Run(setup);
        return true;
    }

    /// <summary>Runs the Repository Setup's agent part in the background, from the phase of provisioning the Sandbox.</summary>
    private void Run(RepositorySetup setup) => _work.Run(
        $"the Repository Setup of {setup.Repository}",
        stopping => starterFiles.WriteAsync(
            setup, () => EnterPhaseAsync(setup.Id, RepositorySetupPhase.AgentWorking, error: null, stopping), stopping),
        error => EnterPhaseAsync(setup.Id, error is null ? RepositorySetupPhase.Done : RepositorySetupPhase.Failed, error, CancellationToken.None));

    private async Task EnterPhaseAsync(Guid id, RepositorySetupPhase phase, string? error, CancellationToken cancellationToken)
    {
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            await database.RepositorySetups
                .Where(setup => setup.Id == id)
                .ExecuteUpdateAsync(setup => setup.SetProperty(s => s.Phase, phase).SetProperty(s => s.Error, error), cancellationToken);
        }
        Changed?.Invoke();
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
            Phase = setup.Phase,
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>
/// A Repository Setup: the new repository, as GitHub names it, what the user stated it is for and built with, and how far
/// the agent part has come.
/// </summary>
/// <param name="Error">Why it failed; <c>null</c> unless <paramref name="Phase"/> is <see cref="RepositorySetupPhase.Failed"/>.</param>
public sealed record RepositorySetup(
    Guid Id, RepositoryName Repository, string Purpose, string TechStack, DateTimeOffset StartedAt, RepositorySetupPhase Phase, string? Error);

/// <summary>
/// Where a Repository Setup is, once GitHub has created its repository: a Repository Setup is kept only from then on.
/// </summary>
public enum RepositorySetupPhase
{
    /// <summary>Creating the Sandbox the agent works in, which takes minutes in Azure.</summary>
    ProvisioningSandbox,

    /// <summary>The agent is writing the starter files and pushing them to main.</summary>
    AgentWorking,

    /// <summary>The starter files, including the Repository Manifest, are on main.</summary>
    Done,

    /// <summary>The agent part failed; it can be retried.</summary>
    Failed,
}

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
    public required RepositorySetupPhase Phase { get; set; }

    /// <summary>Why the Repository Setup failed, when it did.</summary>
    public string? Error { get; set; }
}
