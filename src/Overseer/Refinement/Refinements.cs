using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Overseer.Intents;
using Overseer.Persistence;
using Overseer.Sandboxes;

namespace Overseer.Refinement;

/// <summary>
/// Refinement: when the Intent Lifecycle moves an Intent to Refining, the Analyst works on it in a fresh Sandbox the Intent
/// owns, holding its Repositories in scope. Where each Refinement stands is kept in PostgreSQL. It never sets an Intent's
/// state: a Refinement that fails leaves the Intent Refining, in its slot, and red until a Retry.
/// </summary>
public sealed class Refinements : IHostedService
{
    private readonly IntentLifecycle _lifecycle;
    private readonly SandboxRegistry _sandboxes;
    private readonly IDbContextFactory<OverseerDbContext> _databases;
    private readonly ILogger<Refinements> _logger;

    // Cancelled when serve stops: the Refinements running then are interrupted, and the next start marks them failed.
    private readonly CancellationTokenSource _stopping = new();

    // What runs in the background, which stopping waits for.
    private readonly ConcurrentDictionary<Task, bool> _running = new();

    public Refinements(
        IntentLifecycle lifecycle, SandboxRegistry sandboxes, IDbContextFactory<OverseerDbContext> databases, ILogger<Refinements> logger)
    {
        _lifecycle = lifecycle;
        _sandboxes = sandboxes;
        _databases = databases;
        _logger = logger;
        _lifecycle.RefinementRequested += Run;
    }

    /// <summary>
    /// Raised whenever a Refinement changes phase, on the thread that changed it, once the change is committed. Handlers must
    /// not throw; read the Intent again with <see cref="IntentLifecycle.FindAsync"/>.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Marks the Refinements that a stop of serve interrupted as failed, so they can be retried, and destroys the Sandboxes
    /// they left behind. Runs as serve starts, before the Portal takes requests, so no Start or Retry comes first.
    /// </summary>
    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        await using (var database = await _databases.CreateDbContextAsync(cancellationToken))
        {
            // Also a Refining Intent without a Refinement: one started before Refinement existed, or as serve stopped.
            var interrupted = await database.Intents
                .Where(intent => intent.State == IntentState.Refining
                    && (intent.Refinement == null || intent.Refinement.Phase != RefinementPhase.Failed))
                .Select(intent => new { intent.Id, Phase = intent.Refinement == null ? (RefinementPhase?)null : intent.Refinement.Phase })
                .ToListAsync(cancellationToken);
            foreach (var intent in interrupted)
            {
                await EnterPhaseAsync(database, intent.Id, RefinementPhase.Failed, intent.Phase == RefinementPhase.AnalystWorking
                    ? "Overseer restarted while the Analyst was working. Retry to start the Analyst again."
                    : "Overseer restarted while a Sandbox was being provisioned for the Analyst. Retry to start the Analyst again.",
                    cancellationToken);
            }
        }

        // No Refinement runs yet, so every Sandbox an Intent owns is left over, from a crash or a failed destruction. Those
        // are listed now, before a Start or Retry creates a new one, but destroyed in the background: in Azure that takes minutes.
        var leftOver = await _sandboxes.ListRecordedAsync(SandboxOwnerKind.Intent, cancellationToken);
        if (leftOver.Count > 0)
        {
            Track(Task.Run(async () =>
            {
                foreach (var sandbox in leftOver)
                {
                    await DestroyAsync(sandbox);
                }
            }));
        }
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        try
        {
            await Task.WhenAll(_running.Keys).WaitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or AggregateException)
        {
            // Stopping no longer waits; the next start marks what was running failed and destroys its Sandboxes.
        }
    }

    /// <summary>
    /// Launches the Analyst again for a Refining Intent whose Refinement failed, in a new Sandbox. The Intent stays Refining
    /// throughout.
    /// </summary>
    /// <returns>Whether it launched again: not when there is no such Intent, or its Refinement hasn't failed.</returns>
    public async Task<bool> RetryAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using (var database = await _databases.CreateDbContextAsync(cancellationToken))
        {
            // Only one of two Retries pressed at once finds it failed.
            var retried = await database.Set<RefinementRecord>()
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

    private void Track(Task task)
    {
        _running[task] = true;
        task.ContinueWith(done => _running.TryRemove(done, out _), TaskScheduler.Default);
    }

    /// <summary>Runs the Intent's Refinement in the background, from provisioning its Sandbox on.</summary>
    private void Run(Guid intentId) => Track(Task.Run(async () =>
    {
        string? error;
        try
        {
            error = await RefineAsync(intentId, _stopping.Token);
        }
        catch (Exception exception) when (!_stopping.IsCancellationRequested)
        {
            _logger.LogError(exception, "The Refinement of the Intent {IntentId} failed.", intentId);
            error = exception.Message;
        }
        catch (Exception)
        {
            error = "";
        }
        // A failure as serve stops, such as an agent session the stop broke off, is the stop's doing: the phase is left as
        // it is, and the next start marks the Refinement failed because of the restart.
        if (error is null || _stopping.IsCancellationRequested)
        {
            return;
        }
        try
        {
            await EnterPhaseAsync(intentId, RefinementPhase.Failed, error, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // The next start of serve marks it failed.
            _logger.LogError(exception, "The Refinement of the Intent {IntentId} could not be marked failed.", intentId);
        }
    }));

    /// <summary>
    /// Provisions the Intent's Sandbox and has the Analyst work in it, destroying the Sandbox once it is no longer used.
    /// </summary>
    /// <returns>Why the Refinement failed; <c>null</c> when it is no longer the Intent's to run.</returns>
    private async Task<string?> RefineAsync(Guid intentId, CancellationToken cancellationToken)
    {
        if (await _lifecycle.FindAsync(intentId, cancellationToken) is not { State: IntentState.Refining } intent)
        {
            return null;
        }
        await EnterPhaseAsync(intentId, RefinementPhase.ProvisioningSandbox, error: null, cancellationToken);

        Sandbox sandbox;
        try
        {
            sandbox = await _sandboxes.CreateAsync(
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
                started = await _sandboxes.StartAgentSessionAsync(sandbox, AgentRole.Analyst, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(exception, "The Analyst's session for the Intent {IntentId} could not be started.", intentId);
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
            // Also when serve stops, so a stopped Overseer leaves no Sandbox running.
            await DestroyAsync(sandbox);
        }
    }

    private async Task DestroyAsync(Sandbox sandbox)
    {
        try
        {
            await _sandboxes.DestroyAsync(sandbox, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // The next start of serve destroys it, as a left-over.
            _logger.LogError(exception, "The Sandbox {Sandbox} of the Intent {IntentId} could not be destroyed.", sandbox.ResourceId, sandbox.Owner.Id);
        }
    }

    private async Task EnterPhaseAsync(Guid intentId, RefinementPhase phase, string? error, CancellationToken cancellationToken)
    {
        await using (var database = await _databases.CreateDbContextAsync(cancellationToken))
        {
            await EnterPhaseAsync(database, intentId, phase, error, cancellationToken);
        }
        Changed?.Invoke();
    }

    /// <summary>Records where the Intent's Refinement stands, whether or not it had one before.</summary>
    private static Task EnterPhaseAsync(OverseerDbContext database, Guid intentId, RefinementPhase phase, string? error, CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlAsync($"""
            INSERT INTO "Refinements" ("IntentId", "Phase", "Error") VALUES ({intentId}, {phase.ToString()}, {error})
            ON CONFLICT ("IntentId") DO UPDATE SET "Phase" = EXCLUDED."Phase", "Error" = EXCLUDED."Error"
            """, cancellationToken);
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
