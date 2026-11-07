using Microsoft.EntityFrameworkCore;
using Npgsql;
using Overseer.Intents;
using Overseer.Persistence;
using Overseer.Sandboxes;

namespace Overseer.Refinement;

/// <summary>
/// Refinement: when the Intent Lifecycle moves an Intent to Refining, the Analyst works on it in a fresh Sandbox the Intent
/// owns, holding its Repositories in scope. Where each Refinement stands is kept in PostgreSQL. It never sets an Intent's
/// state: a Refinement that fails leaves the Intent Refining, in its slot, and red until a Retry.
/// </summary>
public sealed class Refinements(
    IntentLifecycle lifecycle, SandboxRegistry sandboxes, IDbContextFactory<OverseerDbContext> databases, ILogger<Refinements> logger)
    : IHostedService
{
    private readonly SandboxedWork _work = new(sandboxes, SandboxOwnerKind.Intent, logger);

    /// <summary>
    /// Raised whenever a Refinement changes phase, on the thread that changed it, once the change is committed. Handlers must
    /// not throw; read the Refinement again with <see cref="ProgressOfAsync"/>.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Marks the Refinements that a stop of serve interrupted as failed, so they can be retried, destroys the Sandboxes they
    /// left behind, and from then on launches the Analyst for each Intent that moves to Refining. Runs as serve starts, before
    /// the Portal takes requests, so no Start or Retry comes first.
    /// </summary>
    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        // A stopgap until Refinement resumes from its transcript (ticket 05): an interrupted Refinement fails, with Retry.
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            // Also a Refining Intent without a Refinement: one started before Refinement existed, or as serve stopped.
            var interrupted = await database.Intents
                .Where(intent => intent.State == IntentState.Refining)
                .Select(intent => new
                {
                    intent.Id,
                    Phase = database.Refinements.Where(refinement => refinement.IntentId == intent.Id).Select(refinement => (RefinementPhase?)refinement.Phase).SingleOrDefault(),
                })
                .Where(intent => intent.Phase != RefinementPhase.Failed)
                .ToListAsync(cancellationToken);
            foreach (var intent in interrupted)
            {
                await EnterPhaseAsync(database, intent.Id, RefinementPhase.Failed, intent.Phase == RefinementPhase.AnalystWorking
                    ? "Overseer restarted while the Analyst was working. Retry to start the Analyst again."
                    : "Overseer restarted while a Sandbox was being provisioned for the Analyst. Retry to start the Analyst again.",
                    cancellationToken);
            }
        }

        // No Refinement runs yet, so every Sandbox an Intent owns is left over.
        await _work.DestroyLeftOverAsync(cancellationToken);
        lifecycle.Transitioned += OnTransitioned;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        lifecycle.Transitioned -= OnTransitioned;
        return _work.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Where the Refinement of <paramref name="intent"/> stands; <c>null</c> unless it is Refining. A Refining Intent whose
    /// Refinement hasn't recorded anything yet is about to provision its Sandbox.
    /// </summary>
    public async Task<RefinementProgress?> ProgressOfAsync(Intent intent, CancellationToken cancellationToken)
    {
        if (intent.State != IntentState.Refining)
        {
            return null;
        }
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return await database.Refinements
            .Where(refinement => refinement.IntentId == intent.Id)
            .Select(refinement => new RefinementProgress(refinement.Phase, refinement.Error))
            .SingleOrDefaultAsync(cancellationToken)
            ?? new RefinementProgress(RefinementPhase.ProvisioningSandbox, Error: null);
    }

    /// <summary>
    /// Launches the Analyst again for a Refining Intent whose Refinement failed, in a new Sandbox. The Intent stays Refining
    /// throughout.
    /// </summary>
    /// <returns>Whether it launched again: not when there is no such Intent, or its Refinement hasn't failed.</returns>
    public async Task<bool> RetryAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            // Only one of two Retries pressed at once finds it failed.
            var retried = await database.Refinements
                .Where(refinement => refinement.IntentId == intentId && refinement.Phase == RefinementPhase.Failed
                    && database.Intents.Any(intent => intent.Id == intentId && intent.State == IntentState.Refining))
                .ExecuteUpdateAsync(refinement => refinement
                    .SetProperty(r => r.Phase, RefinementPhase.ProvisioningSandbox)
                    .SetProperty(r => r.Error, (string?)null), cancellationToken);
            if (retried == 0)
            {
                return false;
            }
        }
        Changed?.Invoke();
        Run(intentId);
        return true;
    }

    private void OnTransitioned(Guid intentId, IntentState to)
    {
        if (to == IntentState.Refining)
        {
            Run(intentId);
        }
    }

    /// <summary>Runs the Intent's Refinement in the background, from provisioning its Sandbox on.</summary>
    private void Run(Guid intentId) => _work.Run(
        $"the Refinement of the Intent {intentId}",
        stopping => RefineAsync(intentId, stopping),
        // Nothing to record when it is no longer the Intent's to run.
        error => error is null ? Task.CompletedTask : EnterPhaseAsync(intentId, RefinementPhase.Failed, error, CancellationToken.None));

    /// <summary>
    /// Provisions the Intent's Sandbox and has the Analyst work in it, destroying the Sandbox once it is no longer used.
    /// </summary>
    /// <returns>Why the Refinement failed; <c>null</c> when it is no longer the Intent's to run.</returns>
    private async Task<string?> RefineAsync(Guid intentId, CancellationToken cancellationToken)
    {
        if (await lifecycle.FindAsync(intentId, cancellationToken) is not { State: IntentState.Refining } intent)
        {
            return null;
        }
        await EnterPhaseAsync(intentId, RefinementPhase.ProvisioningSandbox, error: null, cancellationToken);

        Sandbox sandbox;
        try
        {
            sandbox = await sandboxes.CreateAsync(
                new SandboxOwner(SandboxOwnerKind.Intent, intent.Id), [.. intent.RepositoriesInScope.Select(repository => repository.ToString())],
                cancellationToken);
        }
        catch (SandboxCreationFailedException exception)
        {
            return $"The Sandbox could not be created: {exception.Message}";
        }
        try
        {
            await EnterPhaseAsync(intentId, RefinementPhase.AnalystWorking, error: null, cancellationToken);
            IAgentSession started;
            try
            {
                started = await sandboxes.StartAgentSessionAsync(sandbox, AgentRole.Analyst, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "The Analyst's session for the Intent {IntentId} could not be started.", intentId);
                return $"The Analyst's session failed: {exception.Message}";
            }
            await using var session = started;
            var turn = await session.SendAsync(AnalystInstructions.FirstMessage(intent), cancellationToken);
            await foreach (var _ in turn.Updates.WithCancellation(cancellationToken))
            {
            }
            AgentOutcome outcome;
            try
            {
                outcome = await turn.Outcome.WaitAsync(cancellationToken);
            }
            catch (AgentSessionFailedException exception)
            {
                return $"The Analyst's session failed: {exception.Message}";
            }
            // Until the Analyst can hand in a Round or a Spec, no outcome is one Overseer can act on.
            return outcome switch
            {
                AgentOutcome.AskedQuestion asked =>
                    $"The Analyst's session failed: it asked a question outside a Round, which Overseer can't act on: {asked.Question}",
                _ => "The Analyst's session failed: it ended its turn without a Round or a Spec, which Overseer can't act on.",
            };
        }
        finally
        {
            await sandboxes.DestroyOrLeaveAsync(sandbox);
        }
    }

    private async Task EnterPhaseAsync(Guid intentId, RefinementPhase phase, string? error, CancellationToken cancellationToken)
    {
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            await EnterPhaseAsync(database, intentId, phase, error, cancellationToken);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Records where the Intent's Refinement stands, whether or not it had one before: updated when it has, and inserted when
    /// it hasn't. Should another insert come first meanwhile, this one is applied after it, as an update.
    /// </summary>
    private static async Task EnterPhaseAsync(OverseerDbContext database, Guid intentId, RefinementPhase phase, string? error, CancellationToken cancellationToken)
    {
        if (await UpdateAsync() > 0)
        {
            return;
        }
        database.Refinements.Add(new RefinementRecord { IntentId = intentId, Phase = phase, Error = error });
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            database.ChangeTracker.Clear();
            await UpdateAsync();
        }

        Task<int> UpdateAsync() => database.Refinements
            .Where(refinement => refinement.IntentId == intentId)
            .ExecuteUpdateAsync(refinement => refinement.SetProperty(r => r.Phase, phase).SetProperty(r => r.Error, error), cancellationToken);
    }
}

/// <summary>
/// Turns the light of an Intent whose Refinement failed red, for the Intent Lifecycle, which derives the light: a failed
/// Refinement leaves the Intent Refining, until a Retry.
/// </summary>
public sealed class RefinementFailures : IIntentFailures
{
    public IQueryable<Guid> FailedIn(OverseerDbContext database) =>
        database.Refinements
            .Where(refinement => refinement.Phase == RefinementPhase.Failed
                && database.Intents.Any(intent => intent.Id == refinement.IntentId && intent.State == IntentState.Refining))
            .Select(refinement => refinement.IntentId);
}

/// <summary>Where an Intent's Refinement stands.</summary>
public enum RefinementPhase
{
    /// <summary>Creating the Sandbox the Analyst works in, which takes minutes in Azure.</summary>
    ProvisioningSandbox,

    /// <summary>The Analyst is working on the Intent.</summary>
    AnalystWorking,

    /// <summary>The Refinement failed; it can be retried.</summary>
    Failed,
}

/// <summary>Where an Intent's Refinement stands, and why it failed when it did.</summary>
/// <param name="Error"><c>null</c> unless <paramref name="Phase"/> is <see cref="RefinementPhase.Failed"/>.</param>
public sealed record RefinementProgress(RefinementPhase Phase, string? Error);

/// <summary>An Intent's Refinement, as PostgreSQL holds it: one per Intent, for the Refinement it has now.</summary>
public sealed class RefinementRecord
{
    public required Guid IntentId { get; init; }
    public required RefinementPhase Phase { get; set; }

    /// <summary>Why the Refinement failed, when it did.</summary>
    public string? Error { get; set; }
}
