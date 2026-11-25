using Microsoft.EntityFrameworkCore;
using Npgsql;
using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Persistence;
using Overseer.Sandboxes;

namespace Overseer.Implementation;

/// <summary>
/// The Task Runner: when the Intent Lifecycle moves an Intent to Implementing, it prepares the Intent's Sandbox for the Squad in
/// the background (<see cref="SandboxPreparation"/>). Where each implementation stands is kept in PostgreSQL. An implementation
/// that fails leaves the Intent Implementing, in its slot, and red until a Retry, which prepares a fresh Sandbox. So does one
/// that a restart of serve interrupts, which for now is every implementation that hadn't failed: its Sandbox goes with the restart.
/// </summary>
public sealed class TaskRunner(
    IntentLifecycle lifecycle, SandboxRegistry sandboxes, SandboxPreparation preparation, IDbContextFactory<OverseerDbContext> databases,
    ILogger<TaskRunner> logger)
    : IHostedService
{
    private const string Interrupted = "Overseer restarted, which interrupted the implementation. Retry to prepare a fresh Sandbox.";

    private readonly SandboxedWork _work = new(sandboxes, logger);

    /// <summary>
    /// Raised whenever an implementation changes phase, on the thread that changed it, once the change is committed. Handlers must
    /// not throw; read the implementation again with <see cref="ProgressOfAsync"/>.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Marks the implementations that a stop of serve interrupted as failed, so they can be retried; from then on prepares a
    /// Sandbox for each Intent that moves to Implementing. Runs as serve starts, before the Portal takes requests, so that no
    /// Intent shows as working while nothing works on it.
    /// </summary>
    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        List<Guid> interrupted;
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            // Also an Implementing Intent without a record: one started before implementations were recorded, or as serve stopped.
            interrupted = await database.Intents
                .Where(intent => intent.State == IntentState.Implementing
                    && !database.Implementations.Any(implementation => implementation.IntentId == intent.Id && implementation.Phase == ImplementationPhase.Failed))
                .Select(intent => intent.Id)
                .ToListAsync(cancellationToken);
        }
        foreach (var intentId in interrupted)
        {
            await EnterPhaseAsync(intentId, ImplementationPhase.Failed, repository: null, Interrupted, cancellationToken);
        }
        lifecycle.Transitioned += OnTransitioned;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        lifecycle.Transitioned -= OnTransitioned;
        return _work.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Where the implementation of <paramref name="intent"/> stands; <c>null</c> unless it is Implementing. An Implementing Intent
    /// whose implementation hasn't recorded anything yet, as just after Start, is about to provision its Sandbox.
    /// </summary>
    public async Task<ImplementationProgress?> ProgressOfAsync(Intent intent, CancellationToken cancellationToken)
    {
        if (intent.State != IntentState.Implementing)
        {
            return null;
        }
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var stored = await database.Implementations.AsNoTracking()
            .SingleOrDefaultAsync(implementation => implementation.IntentId == intent.Id, cancellationToken);
        return stored is null
            ? new ImplementationProgress(ImplementationPhase.ProvisioningSandbox, Repository: null, Error: null)
            : new ImplementationProgress(
                stored.Phase, stored.Repository is { } repository ? RepositoryName.Parse(repository) : null, stored.Error);
    }

    /// <summary>
    /// Prepares the Sandbox of an Implementing Intent whose implementation failed again, a fresh one, destroying what is left of
    /// the failed one. The Intent stays Implementing throughout.
    /// </summary>
    /// <returns>Whether it started again: not when there is no such Intent, or its implementation hasn't failed.</returns>
    public async Task<bool> RetryAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            // Only one of two Retries pressed at once finds it failed.
            var retried = await database.Implementations
                .Where(implementation => implementation.IntentId == intentId && implementation.Phase == ImplementationPhase.Failed
                    && database.Intents.Any(intent => intent.Id == intentId && intent.State == IntentState.Implementing))
                .ExecuteUpdateAsync(implementation => implementation
                    .SetProperty(record => record.Phase, ImplementationPhase.ProvisioningSandbox)
                    .SetProperty(record => record.Repository, (string?)null)
                    .SetProperty(record => record.Error, (string?)null), cancellationToken);
            if (retried == 0)
            {
                return false;
            }
        }
        Changed?.Invoke();
        Run(intentId, retrying: true);
        return true;
    }

    private void OnTransitioned(Guid intentId, IntentState to)
    {
        if (to == IntentState.Implementing)
        {
            Run(intentId, retrying: false);
        }
    }

    /// <summary>Prepares the Intent's Sandbox in the background.</summary>
    private void Run(Guid intentId, bool retrying) => _work.Run(
        $"the implementation of the Intent {intentId}",
        stopping => PrepareAsync(intentId, retrying, stopping),
        error => error is null
            ? Task.CompletedTask
            : EnterPhaseAsync(intentId, ImplementationPhase.Failed, repository: null, error, CancellationToken.None));

    /// <returns>Why the implementation failed; <c>null</c> when it hasn't.</returns>
    private async Task<string?> PrepareAsync(Guid intentId, bool retrying, CancellationToken cancellationToken)
    {
        if (await lifecycle.FindAsync(intentId, cancellationToken) is not { State: IntentState.Implementing } intent)
        {
            return null;
        }
        if (retrying)
        {
            // The failed Sandbox went as the implementation failed, unless destroying it failed too. It goes first, as it may be
            // what takes the quota the fresh one needs.
            foreach (var leftOver in await sandboxes.ListRecordedAsync(new SandboxOwner(SandboxOwnerKind.Intent, intentId), cancellationToken))
            {
                await sandboxes.DestroyOrLeaveAsync(leftOver);
            }
        }
        Task EnterAsync(ImplementationPhase phase, RepositoryName? repository) =>
            EnterPhaseAsync(intentId, phase, repository, error: null, cancellationToken);
        if (await preparation.PrepareAsync(intent, EnterAsync, cancellationToken) is { } error)
        {
            return error;
        }
        await EnterAsync(ImplementationPhase.Ready, null);
        return null;
    }

    /// <summary>
    /// Records where the Intent's implementation stands, whether or not it had a record before: updated when it has, and inserted
    /// when it hasn't. Should another insert come first meanwhile, this one is applied after it, as an update.
    /// </summary>
    private async Task EnterPhaseAsync(
        Guid intentId, ImplementationPhase phase, RepositoryName? repository, string? error, CancellationToken cancellationToken)
    {
        var name = repository?.ToString();
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            if (await UpdateAsync(database) == 0)
            {
                database.Implementations.Add(new ImplementationRecord { IntentId = intentId, Phase = phase, Repository = name, Error = error });
                try
                {
                    await database.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    database.ChangeTracker.Clear();
                    await UpdateAsync(database);
                }
            }
        }
        Changed?.Invoke();

        Task<int> UpdateAsync(OverseerDbContext database) => database.Implementations
            .Where(implementation => implementation.IntentId == intentId)
            .ExecuteUpdateAsync(implementation => implementation
                .SetProperty(record => record.Phase, phase)
                .SetProperty(record => record.Repository, name)
                .SetProperty(record => record.Error, error), cancellationToken);
    }
}

/// <summary>
/// Turns the light of an Intent whose implementation failed red, for the Intent Lifecycle, which derives the light: a failed
/// implementation leaves the Intent Implementing, until a Retry.
/// </summary>
public sealed class ImplementationFailures : IIntentFailures
{
    public IQueryable<Guid> FailedIn(OverseerDbContext database) =>
        database.Implementations
            .Where(implementation => implementation.Phase == ImplementationPhase.Failed
                && database.Intents.Any(intent => intent.Id == implementation.IntentId && intent.State == IntentState.Implementing))
            .Select(implementation => implementation.IntentId);
}

/// <summary>Where an Intent's implementation stands, in the order the phases come.</summary>
public enum ImplementationPhase
{
    /// <summary>Creating the Sandbox the Squad works in, which takes minutes in Azure, or about to.</summary>
    ProvisioningSandbox,

    /// <summary>Putting a Repository in scope on the Intent branch, and reading its Repository Manifest.</summary>
    CheckingOutIntentBranch,

    /// <summary>Running a repository's <c>setup</c> command.</summary>
    RunningSetup,

    /// <summary>Starting the services of a repository's Compose file, and waiting for them.</summary>
    StartingDependencies,

    /// <summary>The Sandbox is ready for the Squad.</summary>
    Ready,

    /// <summary>The implementation failed; it can be retried.</summary>
    Failed,
}

/// <summary>Where an Intent's implementation stands, and why it failed when it did.</summary>
/// <param name="Repository">The repository the phase is about; <c>null</c> when it isn't about one.</param>
/// <param name="Error"><c>null</c> unless <paramref name="Phase"/> is <see cref="ImplementationPhase.Failed"/>.</param>
public sealed record ImplementationProgress(ImplementationPhase Phase, RepositoryName? Repository, string? Error);

/// <summary>An Intent's implementation, as PostgreSQL holds it: one per Intent.</summary>
public sealed class ImplementationRecord
{
    public required Guid IntentId { get; init; }
    public required ImplementationPhase Phase { get; set; }

    /// <summary><c>owner/name</c> of the repository the phase is about, when it is about one.</summary>
    public string? Repository { get; set; }

    /// <summary>Why the implementation failed, when it did.</summary>
    public string? Error { get; set; }
}
