using System.Diagnostics;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Persistence;
using Overseer.Sandboxes;
using Overseer.Specs;

namespace Overseer.Implementation;

/// <summary>
/// The Task Runner: when the Intent Lifecycle moves an Intent to Implementing, it gives the Intent its Task list, prepares the
/// Intent's Sandbox for the Squad (<see cref="SandboxPreparation"/>), has the Squad carry out the Tasks there
/// (<see cref="TaskLoop"/>), runs the final verification once all are Done (<see cref="FinalVerification"/>), which, should it
/// fail, adds a fix-up Task whose verification is the final one, and once it is passed opens the pull requests
/// (<see cref="PullRequests"/>) and has the Intent move to In Review, all in the background. Where each
/// implementation stands, and the Sandbox it was prepared in, is kept in PostgreSQL. A Task's agent that asks a question leaves the
/// implementation waiting, its Intent orange, until the human answers (<see cref="AnswerAsync"/>). An implementation that fails
/// leaves the Intent Implementing, in its slot, and red until a Retry, which goes on in the prepared Sandbox when there is one, and
/// otherwise prepares a fresh one; once the final verification has passed, a Retry only opens the pull requests still missing, which
/// needs no Sandbox. One with a Failed Task has no such Retry: the human retries that Task instead
/// (<see cref="RetryTaskAsync"/>). No Sandbox outlives a restart of serve: every implementation that hadn't failed carries on by
/// itself, in a fresh one.
/// </summary>
public sealed class TaskRunner(
    IntentLifecycle lifecycle, SandboxRegistry sandboxes, SandboxPreparation preparation, IntentTasks tasks,
    TaskLoop loop, TaskQuestions questions, FinalVerification finalVerification, PullRequests pullRequests,
    IDbContextFactory<OverseerDbContext> databases, ILogger<TaskRunner> logger)
    : IHostedService
{
    private readonly SandboxedWork _work = new(logger);

    /// <summary>
    /// Raised whenever an implementation changes phase, one of its Tasks changes or is verified, its agent asks a question or gets
    /// the answer, or one of its pull requests is opened, on the thread that changed it, once the change is committed. Handlers must
    /// not throw; read again with <see cref="ProgressOfAsync"/>, <see cref="IntentTasks.OfAsync"/>,
    /// <see cref="VerificationAttempts.OfAsync"/>, <see cref="TaskQuestions.OfAsync"/> and <see cref="PullRequests.OfAsync"/>.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Carries on with each implementation that a stop of serve interrupted, which is that of every Implementing Intent that
    /// hadn't failed; from then on implements each Intent that moves to Implementing. Its Sandbox goes with the restart
    /// (<see cref="LeftOverSandboxes"/>), and with it any work not pushed, so unless only pull requests are left to open it goes on
    /// in a fresh one, from its first Task that isn't Done. One that stopped at a Failed Task, before that was recorded, is recorded
    /// failed instead. Runs as serve starts, and records where each one stands before the Portal takes requests, so that no page
    /// shows a Task that runs, or a question that awaits its answer, in a session that is gone.
    /// </summary>
    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        List<(Guid IntentId, bool AwaitingPullRequests, int? FailedTask)> interrupted;
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            // Also an Implementing Intent without a record: one started as serve stopped. A failed one waits for its Retry.
            interrupted = [.. (await database.Intents
                .Where(intent => intent.State == IntentState.Implementing
                    && !database.Implementations.Any(implementation => implementation.IntentId == intent.Id && implementation.Phase == ImplementationPhase.Failed))
                .Select(intent => new
                {
                    intent.Id,
                    AwaitingPullRequests = database.Implementations.Any(
                        implementation => implementation.IntentId == intent.Id && implementation.AwaitingPullRequests != null),
                    FailedTask = database.IntentTasks
                        .Where(task => task.IntentId == intent.Id && task.State == IntentTaskState.Failed)
                        .Select(task => (int?)task.Number)
                        .FirstOrDefault(),
                })
                .ToListAsync(cancellationToken))
                .Select(intent => (intent.Id, intent.AwaitingPullRequests, intent.FailedTask))];
            // No Sandbox outlives a restart, so no implementation has one to go on in, nor work to push.
            await database.Implementations.ExecuteUpdateAsync(
                implementation => implementation.SetProperty(record => record.SandboxResourceId, (string?)null), cancellationToken);
        }
        await tasks.ForgetAllUnpushedAsync(cancellationToken);
        var carryingOn = new List<Guid>();
        foreach (var (intentId, awaitingPullRequests, failedTask) in interrupted)
        {
            var (phase, error) = failedTask is { } number ? (ImplementationPhase.Failed, TaskLoop.StoppedAtFailedTask(number))
                : awaitingPullRequests ? (ImplementationPhase.OpeningPullRequests, null)
                : (ImplementationPhase.ProvisioningSandbox, (string?)null);
            await EnterPhaseAsync(intentId, phase, repository: null, error, cancellationToken);
            if (phase != ImplementationPhase.Failed)
            {
                carryingOn.Add(intentId);
            }
        }
        lifecycle.Transitioned += OnTransitioned;
        foreach (var intentId in carryingOn)
        {
            Run(intentId, takenUpAgain: true);
        }
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
        if (stored is null)
        {
            return new ImplementationProgress(
                ImplementationPhase.ProvisioningSandbox, Repository: null, Error: null,
                ResumesAt: ImplementationPhase.ProvisioningSandbox, HasFailedTask: false);
        }
        var hasFailedTask = await database.IntentTasks
            .AnyAsync(task => task.IntentId == intent.Id && task.State == IntentTaskState.Failed, cancellationToken);
        return new ImplementationProgress(
            stored.Phase, stored.Repository is { } repository ? RepositoryName.Parse(repository) : null, stored.Error,
            ResumesAt(stored), hasFailedTask);
    }

    private static readonly Func<ImplementationRecord, ImplementationPhase> ResumesAt = ImplementationRecord.ResumesAt.Compile();

    /// <summary>
    /// Takes up the implementation of an Implementing Intent again after it failed. One that failed once the final verification had
    /// passed, as when a pull request couldn't be opened, only opens the pull requests still missing. One that failed once its Sandbox
    /// was prepared, as when a Task's session broke off or its work couldn't be pushed, goes on in that Sandbox, with its first Task
    /// that isn't Done, and the final verification once all are. Otherwise, as when preparing failed or serve has restarted since, or
    /// when the prepared Sandbox is gone, a fresh Sandbox is prepared, once what is left of the old one is destroyed. The Intent stays
    /// Implementing throughout.
    /// </summary>
    /// <returns>
    /// Whether it started again: not when there is no such Intent, its implementation hasn't failed, or one of its Tasks is a Failed
    /// Task, which stops the Intent.
    /// </returns>
    public async Task<bool> RetryAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            var withoutFailedTask = FailedImplementationOf(database, intentId)
                .Where(implementation => !database.IntentTasks.Any(task => task.IntentId == intentId && task.State == IntentTaskState.Failed));
            if (!await MarkTakenUpAgainAsync(withoutFailedTask, cancellationToken))
            {
                return false;
            }
        }
        TakeUpAgain(intentId);
        return true;
    }

    /// <summary>
    /// Has a Failed Task of an Implementing Intent carried out again, which takes up its implementation as <see cref="RetryAsync"/>
    /// takes up any other: the Task goes back to Pending, changed as <paramref name="edit"/> has it when there is one, and a new
    /// agent session works on it, with all its verification attempts again, in the prepared Sandbox, on what the earlier attempts
    /// left there, or in a fresh one when that one is gone. The earlier attempts are kept.
    /// </summary>
    /// <param name="lastRun">
    /// The number of the Task's last run that made a verification attempt, as the human saw it who asks for this: once a later run
    /// has made one, what they saw is no longer how the Task failed.
    /// </param>
    /// <param name="edit">What the Task is from now on; <c>null</c> to leave it as it is.</param>
    /// <returns>
    /// Whether it started again: not unless the Intent is Implementing, its implementation has failed, and that Task is a Failed
    /// Task whose last run is <paramref name="lastRun"/>. Nothing changes then.
    /// </returns>
    public async Task<bool> RetryTaskAsync(Guid intentId, int taskNumber, int lastRun, SpecTask? edit, CancellationToken cancellationToken)
    {
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var withThatFailedTask = FailedImplementationOf(database, intentId)
                .Where(implementation => database.IntentTasks.Any(
                        task => task.IntentId == intentId && task.Number == taskNumber && task.State == IntentTaskState.Failed)
                    && !database.VerificationAttempts.Any(
                        attempt => attempt.IntentId == intentId && attempt.TaskNumber == taskNumber && attempt.Run > lastRun));
            // First, as it holds the implementation's row: another of these then waits for this one, and finds it no longer failed.
            if (!await MarkTakenUpAgainAsync(withThatFailedTask, cancellationToken))
            {
                return false;
            }
            await IntentTasks.PutBackToPendingAsync(database, intentId, taskNumber, edit, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        TakeUpAgain(intentId);
        return true;
    }

    /// <summary>
    /// Answers the question a Task's agent asked: the answer goes to the same session, which goes on with the Task.
    /// </summary>
    /// <param name="question">The question's number among the Intent's.</param>
    /// <returns>
    /// Why the answer was refused, as when that question doesn't await one, because it has its answer or its session has ended;
    /// <c>null</c> when it is recorded and on its way to the agent.
    /// </returns>
    public async Task<string?> AnswerAsync(Guid intentId, int question, string answer, CancellationToken cancellationToken)
    {
        if (await questions.AnswerAsync(intentId, question, answer, cancellationToken) is { } refusal)
        {
            return refusal;
        }
        Changed?.Invoke();
        return null;
    }

    /// <summary>The implementation of the Intent, when it is Implementing and that has failed.</summary>
    private static IQueryable<ImplementationRecord> FailedImplementationOf(OverseerDbContext database, Guid intentId) =>
        database.Implementations.Where(implementation => implementation.IntentId == intentId
            && implementation.Phase == ImplementationPhase.Failed
            && database.Intents.Any(intent => intent.Id == intentId && intent.State == IntentState.Implementing));

    /// <summary>
    /// Records that the implementation <paramref name="failed"/> finds, should it find one, is no longer failed: of two Retries
    /// pressed at once, only one finds it.
    /// </summary>
    private static async Task<bool> MarkTakenUpAgainAsync(IQueryable<ImplementationRecord> failed, CancellationToken cancellationToken) =>
        await failed.ExecuteUpdateAsync(implementation => implementation
            .SetProperty(record => record.Phase, ImplementationRecord.ResumesAt)
            .SetProperty(record => record.Repository, (string?)null)
            .SetProperty(record => record.Error, (string?)null), cancellationToken) > 0;

    /// <summary>Goes on with an implementation just recorded as taken up again.</summary>
    private void TakeUpAgain(Guid intentId)
    {
        Changed?.Invoke();
        Run(intentId, takenUpAgain: true);
    }

    private void OnTransitioned(Guid intentId, IntentState to)
    {
        if (to == IntentState.Implementing)
        {
            Run(intentId, takenUpAgain: false);
        }
    }

    /// <summary>Implements the Intent in the background.</summary>
    /// <param name="takenUpAgain">
    /// Whether it had started before, as on a Retry or after a restart of serve, so that a Sandbox of it may be left.
    /// </param>
    private void Run(Guid intentId, bool takenUpAgain) => _work.Run(
        $"the implementation of the Intent {intentId}",
        stopping => ImplementAsync(intentId, takenUpAgain, stopping),
        error => error is null
            ? Task.CompletedTask
            : EnterPhaseAsync(intentId, ImplementationPhase.Failed, repository: null, error, CancellationToken.None));

    /// <returns>Why the implementation failed; <c>null</c> when it hasn't.</returns>
    private async Task<string?> ImplementAsync(Guid intentId, bool takenUpAgain, CancellationToken cancellationToken)
    {
        if (await lifecycle.FindAsync(intentId, cancellationToken) is not { State: IntentState.Implementing } intent)
        {
            return null;
        }
        await tasks.CreateFromApprovedSpecAsync(intent, cancellationToken);
        if (await AwaitingPullRequestsOfAsync(intentId, cancellationToken) is { } changed)
        {
            return await OpenPullRequestsAsync(intent, changed, cancellationToken);
        }
        return await ImplementInSandboxAsync(intent, takenUpAgain, cancellationToken);
    }

    /// <summary>
    /// Has the Intent's work done and verified in its Sandbox, which is prepared first where there is none, until the final
    /// verification passes, and then opens the pull requests.
    /// </summary>
    /// <returns>Why the implementation failed; <c>null</c> when it hasn't.</returns>
    private async Task<string?> ImplementInSandboxAsync(Intent intent, bool takenUpAgain, CancellationToken cancellationToken)
    {
        var intentId = intent.Id;
        var sandbox = await PreparedSandboxOfAsync(intentId, cancellationToken);
        if (sandbox is null)
        {
            Task EnterAsync(ImplementationPhase phase, RepositoryName? repository) =>
                EnterPhaseAsync(intentId, phase, repository, error: null, cancellationToken);
            // Verified work that wasn't pushed went with the Sandbox that held it.
            await tasks.ForgetUnpushedAsync(intentId, cancellationToken);
            if (takenUpAgain)
            {
                // Also when a Retry set out to go on in a Sandbox that turns out to be gone.
                await EnterAsync(ImplementationPhase.ProvisioningSandbox, null);
                if (await DestroyOldSandboxAsync(intentId, cancellationToken) is { } notDestroyed)
                {
                    return notDestroyed;
                }
            }
            switch (await preparation.PrepareAsync(intent, EnterAsync, cancellationToken))
            {
                case SandboxPreparationResult.Failed failed:
                    return failed.Error;
                case SandboxPreparationResult.Ready ready:
                    sandbox = ready.Sandbox;
                    break;
                default:
                    throw new UnreachableException();
            }
            await EnterPhaseAsync(intentId, ImplementationPhase.Ready, repository: null, error: null, cancellationToken, sandbox);
        }
        // Until the final verification passes: should it fail, it adds the fix-up Task, which is carried out like any other, and
        // passes it by being verified.
        while (true)
        {
            if (await loop.RunAsync(
                intent, sandbox, () => Changed?.Invoke(),
                (database, changed, stopping) => AwaitPullRequestsAsync(database, intentId, changed, stopping),
                cancellationToken) is { } unfinished)
            {
                return unfinished;
            }
            if (await AwaitingPullRequestsOfAsync(intentId, cancellationToken) is { } fixedUp)
            {
                return await OpenPullRequestsAsync(intent, fixedUp, cancellationToken);
            }
            await EnterPhaseAsync(intentId, ImplementationPhase.FinalVerification, repository: null, error: null, cancellationToken);
            switch (await finalVerification.RunAsync(intent, sandbox, cancellationToken))
            {
                case FinalVerificationResult.FixUpAdded:
                    await EnterPhaseAsync(intentId, ImplementationPhase.Ready, repository: null, error: null, cancellationToken, sandbox);
                    break;
                case FinalVerificationResult.Failed failed:
                    return failed.Error;
                case FinalVerificationResult.Passed passed:
                    await using (var database = await databases.CreateDbContextAsync(cancellationToken))
                    {
                        await AwaitPullRequestsAsync(database, intentId, passed.Changed, cancellationToken);
                    }
                    return await OpenPullRequestsAsync(intent, passed.Changed, cancellationToken);
                default:
                    throw new UnreachableException();
            }
        }
    }

    /// <summary>Opens the pull requests still missing, and once each repository has its own, has the Intent move to In Review.</summary>
    /// <param name="changed">The repositories that changed, each to have a pull request.</param>
    /// <returns>Why a pull request couldn't be opened; <c>null</c> once none is missing.</returns>
    private async Task<string?> OpenPullRequestsAsync(
        Intent intent, IReadOnlyList<RepositoryName> changed, CancellationToken cancellationToken)
    {
        await EnterPhaseAsync(intent.Id, ImplementationPhase.OpeningPullRequests, repository: null, error: null, cancellationToken);
        if (await pullRequests.OpenMissingAsync(intent, changed, () => Changed?.Invoke(), cancellationToken) is { } notOpened)
        {
            return notOpened;
        }
        // Refused only when the Intent is no longer Implementing, and then it isn't this implementation's to move.
        await lifecycle.AwaitReviewAsync(
            intent.Id,
            // Work that changes after the review is verified anew.
            (database, stopping) => database.Implementations
                .Where(implementation => implementation.IntentId == intent.Id)
                .ExecuteUpdateAsync(
                    implementation => implementation
                        .SetProperty(record => record.Phase, ImplementationPhase.Ready)
                        .SetProperty(record => record.AwaitingPullRequests, (List<RepositoryName>?)null),
                    stopping),
            cancellationToken);
        return null;
    }

    /// <summary>
    /// The repositories that changed and are each to have a pull request, once the Intent's final verification has passed; <c>null</c>
    /// until then.
    /// </summary>
    private async Task<IReadOnlyList<RepositoryName>?> AwaitingPullRequestsOfAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return await database.Implementations
            .Where(implementation => implementation.IntentId == intentId)
            .Select(implementation => implementation.AwaitingPullRequests)
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Records in <paramref name="database"/> that the Intent's work passed the final verification, so that what is left is a pull
    /// request for each of these repositories, the ones that changed: from here on a Retry only opens those.
    /// </summary>
    private static Task AwaitPullRequestsAsync(
        OverseerDbContext database, Guid intentId, IReadOnlyList<RepositoryName> changed, CancellationToken cancellationToken)
    {
        var repositories = changed.ToList();
        return database.Implementations
            .Where(implementation => implementation.IntentId == intentId)
            .ExecuteUpdateAsync(
                implementation => implementation.SetProperty(record => record.AwaitingPullRequests, repositories), cancellationToken);
    }

    /// <summary>
    /// The Sandbox the Intent's implementation was prepared in, to go on in; <c>null</c> when there is none to, as before it is
    /// prepared, after a restart of serve, or when it is gone.
    /// </summary>
    private async Task<Sandbox?> PreparedSandboxOfAsync(Guid intentId, CancellationToken cancellationToken)
    {
        string? resourceId;
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            resourceId = await database.Implementations
                .Where(implementation => implementation.IntentId == intentId)
                .Select(implementation => implementation.SandboxResourceId)
                .SingleOrDefaultAsync(cancellationToken);
        }
        if (resourceId is null)
        {
            return null;
        }
        var sandbox = new Sandbox(resourceId, new SandboxOwner(SandboxOwnerKind.Intent, intentId));
        try
        {
            return await sandboxes.ExistsAsync(sandbox, cancellationToken) ? sandbox : null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Not taken for gone: that would destroy the work in it.
            throw new InvalidOperationException(
                $"Overseer could not check whether the Sandbox is still there: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Destroys what is left of the implementation's old Sandbox, ahead of a fresh one: it went as preparing it failed, unless
    /// destroying it failed too, and after a restart of serve it may still be being destroyed (<see cref="LeftOverSandboxes"/>),
    /// which this then waits for. No other is created while it is there, costing money and perhaps taking the quota the fresh one
    /// needs.
    /// </summary>
    /// <returns>Why it couldn't be destroyed; <c>null</c> once nothing is left of it.</returns>
    private async Task<string?> DestroyOldSandboxAsync(Guid intentId, CancellationToken cancellationToken)
    {
        foreach (var sandbox in await sandboxes.ListRecordedAsync(new SandboxOwner(SandboxOwnerKind.Intent, intentId), cancellationToken))
        {
            try
            {
                await sandboxes.DestroyAsync(sandbox, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "The old Sandbox {Sandbox} of the Intent {IntentId} could not be destroyed.", sandbox.ResourceId, intentId);
                return $"The old Sandbox could not be destroyed, so no new one was created: {exception.Message}";
            }
        }
        return null;
    }

    /// <summary>
    /// Records where the Intent's implementation stands, whether or not it had a record before. In a failed one no Task runs, nor
    /// in one that is yet to have its Sandbox: the one that did, unless it is a Failed Task by now, waits to run again.
    /// </summary>
    /// <param name="sandbox">
    /// The Sandbox the implementation is prepared in, with <see cref="ImplementationPhase.Ready"/>. A failure keeps the one
    /// recorded, as do the phases after it; every other phase comes before there is one.
    /// </param>
    private async Task EnterPhaseAsync(
        Guid intentId, ImplementationPhase phase, RepositoryName? repository, string? error, CancellationToken cancellationToken,
        Sandbox? sandbox = null)
    {
        var name = repository?.ToString();
        var stored = StorableText.Of(error);
        var resourceId = sandbox?.ResourceId;
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            if (phase is ImplementationPhase.Failed or ImplementationPhase.ProvisioningSandbox)
            {
                await IntentTasks.StopRunningAsync(database, intentId, cancellationToken);
            }
            await database.UpsertAsync(
                new ImplementationRecord { IntentId = intentId, Phase = phase, Repository = name, Error = stored, SandboxResourceId = resourceId },
                implementation => implementation.IntentId == intentId,
                implementation =>
                {
                    implementation
                        .SetProperty(record => record.Phase, phase)
                        .SetProperty(record => record.Repository, name)
                        .SetProperty(record => record.Error, stored);
                    if (phase is not (ImplementationPhase.Failed or ImplementationPhase.FinalVerification
                        or ImplementationPhase.OpeningPullRequests))
                    {
                        implementation.SetProperty(record => record.SandboxResourceId, resourceId);
                    }
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        Changed?.Invoke();
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

    /// <summary>A Task's agent asked a question, and waits for the human's answer.</summary>
    AwaitingAnswer,

    /// <summary>Every Task is Done, and the Intent branch is built and tested as a whole.</summary>
    FinalVerification,

    /// <summary>The final verification has passed, and each repository that changed gets its pull request.</summary>
    OpeningPullRequests,

    /// <summary>The implementation failed; it can be retried.</summary>
    Failed,
}

/// <summary>Where an Intent's implementation stands, and why it failed when it did.</summary>
/// <param name="Repository">The repository the phase is about; <c>null</c> when it isn't about one.</param>
/// <param name="Error"><c>null</c> unless <paramref name="Phase"/> is <see cref="ImplementationPhase.Failed"/>.</param>
/// <param name="ResumesAt">
/// The phase a Retry takes it up in, which also says how far it had got when it failed:
/// <see cref="ImplementationPhase.OpeningPullRequests"/> once the final verification has passed, so that what is left is opening
/// the pull requests; else <see cref="ImplementationPhase.Ready"/> once its Sandbox is prepared, which a Retry goes on in unless
/// it is gone; else <see cref="ImplementationPhase.ProvisioningSandbox"/>.
/// </param>
/// <param name="HasFailedTask">Whether one of its Tasks is a Failed Task, which only retrying that Task takes up again.</param>
public sealed record ImplementationProgress(
    ImplementationPhase Phase, RepositoryName? Repository, string? Error, ImplementationPhase ResumesAt, bool HasFailedTask);

/// <summary>An Intent's implementation, as PostgreSQL holds it: one per Intent.</summary>
public sealed class ImplementationRecord
{
    /// <summary>
    /// The phase a Retry takes a failed implementation up in, from what it has got done: opening the pull requests once the final
    /// verification has passed, else going on in the prepared Sandbox when there is one, else preparing one.
    /// </summary>
    internal static readonly Expression<Func<ImplementationRecord, ImplementationPhase>> ResumesAt = record =>
        record.AwaitingPullRequests != null ? ImplementationPhase.OpeningPullRequests
        : record.SandboxResourceId != null ? ImplementationPhase.Ready
        : ImplementationPhase.ProvisioningSandbox;

    public required Guid IntentId { get; init; }
    public required ImplementationPhase Phase { get; set; }

    /// <summary><c>owner/name</c> of the repository the phase is about, when it is about one.</summary>
    public string? Repository { get; set; }

    /// <summary>Why the implementation failed, when it did.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// The resource ID of the Sandbox the implementation was prepared in; <c>null</c> until it is prepared, and again from a
    /// restart of serve, which destroys it.
    /// </summary>
    public string? SandboxResourceId { get; set; }

    /// <summary>
    /// The repositories that changed, each to have a pull request: set once the final verification has passed, until the Intent is
    /// In Review. <c>null</c> otherwise.
    /// </summary>
    public List<RepositoryName>? AwaitingPullRequests { get; set; }
}
