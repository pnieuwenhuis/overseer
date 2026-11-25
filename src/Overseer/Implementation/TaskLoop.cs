using System.Diagnostics;
using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Sandboxes;
using Overseer.Settings;
using Overseer.Specs;

namespace Overseer.Implementation;

/// <summary>
/// The Squad at work: carries out an Intent's Tasks in its prepared Sandbox, one at a time (ADR 0001), in their order, each in a
/// new agent session for the Task's role. Once the agent says it is done, Overseer verifies the work (<see cref="Verification"/>)
/// and tells the agent what failed, for it to fix, until the work passes or the allowed attempts are used up, which makes the Task
/// a Failed Task. Work that passes is committed and pushed to the Intent branch (<see cref="IntentBranches"/>); only then is the
/// Task Done and the next one started.
/// </summary>
public sealed class TaskLoop(
    IntentTasks tasks, VerificationAttempts attempts, Verification verification, IntentBranches branches, SpecStore specs,
    SandboxRegistry sandboxes, SettingsStore settings, RepositoryCommands commands, ILogger<TaskLoop> logger)
{
    /// <summary>
    /// Carries out the Intent's Tasks that aren't Done until none is left, or one can't be finished. A Task whose verified work
    /// couldn't be pushed before is only pushed.
    /// </summary>
    /// <param name="changed">Called whenever a Task changes, once the change is committed.</param>
    /// <returns>
    /// Why a Task couldn't be finished, for the caller to fail the implementation with, which puts that Task, if still Running, back
    /// to Pending; <c>null</c> once every Task is Done.
    /// </returns>
    public async Task<string?> RunAsync(Intent intent, Sandbox sandbox, Action changed, CancellationToken cancellationToken)
    {
        while (await tasks.NextAsync(intent.Id, cancellationToken) is { } task)
        {
            if (task.State == IntentTaskState.Failed)
            {
                return $"Task {task.Number} is a Failed Task: its work didn't pass verification.";
            }
            await tasks.EnterStateAsync(intent.Id, task.Number, IntentTaskState.Running, cancellationToken);
            changed();
            if (await FinishAsync(intent, sandbox, task, changed, cancellationToken) is { } error)
            {
                // The Sandbox holds the GitHub token, so an agent can say it; a secret never goes into PostgreSQL.
                return await commands.RedactedAsync(error, cancellationToken);
            }
            await tasks.FinishAsync(intent.Id, task.Number, cancellationToken);
            changed();
        }
        return null;
    }

    /// <summary>Has the Task's work done, verified and committed, unless it already is, and pushes it.</summary>
    /// <returns>Why the Task couldn't be finished; <c>null</c> once its work is pushed.</returns>
    private async Task<string?> FinishAsync(
        Intent intent, Sandbox sandbox, IntentTask task, Action changed, CancellationToken cancellationToken)
    {
        if (task.AwaitingPush is not { } unpushed)
        {
            switch (await CarryOutAsync(intent, sandbox, task, changed, cancellationToken))
            {
                case Work.Unfinished unfinished:
                    return unfinished.Error;
                case Work.Verified verified:
                    unpushed = verified.Changed;
                    break;
                default:
                    throw new UnreachableException();
            }
            foreach (var repository in unpushed)
            {
                if (await branches.CommitAsync(sandbox, repository, intent.Branch, task.Title, cancellationToken) is { } notCommitted)
                {
                    return notCommitted;
                }
            }
            // From here on a Retry only pushes: the work is verified, and no agent touches it again.
            await tasks.AwaitPushAsync(intent.Id, task.Number, unpushed, cancellationToken);
            changed();
        }
        foreach (var repository in unpushed)
        {
            if (await branches.PushAsync(sandbox, repository, intent.Branch, cancellationToken) is { } notPushed)
            {
                return notPushed;
            }
        }
        return null;
    }

    /// <summary>
    /// Has a new agent session of the Task's role work on it, verifying its work each time its turn ends as done and telling it what
    /// failed, and stops the session once the work passes or the Task can't go on.
    /// </summary>
    private async Task<Work> CarryOutAsync(
        Intent intent, Sandbox sandbox, IntentTask task, Action changed, CancellationToken cancellationToken)
    {
        // An Intent has Tasks only once it has an Approved Spec, which they were created from.
        var approved = await specs.VersionOfAsync(intent.Id, intent.ApprovedSpecVersion!.Value, cancellationToken)
            ?? throw new InvalidOperationException($"The Approved Spec of the Intent {intent.Id} is gone.");
        var taskCount = (await tasks.OfAsync(intent.Id, cancellationToken)).Count;
        var message = TaskInstructions.FirstMessage(intent, approved.Spec, task, taskCount);
        var run = await attempts.NextRunAsync(intent.Id, task.Number, cancellationToken);
        IAgentSession session;
        try
        {
            session = await sandboxes.StartAgentSessionAsync(sandbox, task.Role, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return SessionFailed(intent, task, exception);
        }
        await using var stopping = session;
        for (var number = 1; ; number++)
        {
            try
            {
                if (await session.TakeTurnAsync(message, cancellationToken) is AgentOutcome.AskedQuestion asked)
                {
                    return new Work.Unfinished(
                        $"The {task.Role} asked a question during Task {task.Number}, which can't be answered yet: {asked.Question}");
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                return SessionFailed(intent, task, exception);
            }

            VerificationAttempt attempt;
            IReadOnlyList<RepositoryName> changedRepositories;
            try
            {
                var changes = await ChangesAsync(intent, sandbox, cancellationToken);
                var steps = await verification.VerifyAsync(
                    sandbox, intent.RepositoriesInScope, [.. task.Repositories.Union(changes.Keys)], changes, cancellationToken);
                attempt = new VerificationAttempt(task.Number, run, number, steps);
                changedRepositories = [.. intent.RepositoriesInScope.Where(changes.ContainsKey)];
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Task {Task} of the Intent {IntentId} could not be verified.", task.Number, intent.Id);
                return new Work.Unfinished($"Task {task.Number} could not be verified: {exception.Message}");
            }
            await attempts.RecordAsync(intent.Id, attempt, cancellationToken);
            changed();
            if (attempt.FailedStep is not { } failed)
            {
                return new Work.Verified(changedRepositories);
            }
            // As the Settings say now, so that raising the limit also helps a Task being worked on.
            var allowed = (await settings.GetOperatingLimitsAsync(cancellationToken)).VerificationAttemptsPerTask;
            if (number >= allowed)
            {
                await tasks.EnterStateAsync(intent.Id, task.Number, IntentTaskState.Failed, cancellationToken);
                changed();
                return new Work.Unfinished(
                    $"Task {task.Number} is a Failed Task: its work didn't pass verification in {number} "
                    + $"{(number == 1 ? "attempt" : "attempts")}. In the last one: {failed.Failure}");
            }
            message = TaskInstructions.VerificationFailed(task, failed, number, allowed);
        }
    }

    /// <summary>
    /// The files changed since the last verified work, in each Repository in scope that has any: all are asked, as an agent may have
    /// changed any.
    /// </summary>
    private async Task<IReadOnlyDictionary<RepositoryName, IReadOnlyList<string>>> ChangesAsync(
        Intent intent, Sandbox sandbox, CancellationToken cancellationToken)
    {
        var changes = new Dictionary<RepositoryName, IReadOnlyList<string>>();
        foreach (var repository in intent.RepositoriesInScope)
        {
            var files = await branches.ChangedFilesAsync(sandbox, repository, cancellationToken);
            if (files.Count > 0)
            {
                changes.Add(repository, files);
            }
        }
        return changes;
    }

    private Work.Unfinished SessionFailed(Intent intent, IntentTask task, Exception exception)
    {
        logger.LogError(
            exception, "The {Role}'s session for Task {Task} of the Intent {IntentId} failed.", task.Role, task.Number, intent.Id);
        return new Work.Unfinished($"The {task.Role}'s session for Task {task.Number} failed: {exception.Message}");
    }

    /// <summary>What an agent's session on a Task came to.</summary>
    private abstract record Work
    {
        private Work()
        {
        }

        /// <summary>The work passed verification.</summary>
        /// <param name="Changed">The repositories it changed, in the Intent's order.</param>
        public sealed record Verified(IReadOnlyList<RepositoryName> Changed) : Work;

        /// <summary><paramref name="Error"/> says why there is no verified work.</summary>
        public sealed record Unfinished(string Error) : Work;
    }
}
