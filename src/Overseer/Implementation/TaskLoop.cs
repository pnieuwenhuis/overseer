using System.Diagnostics;
using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Persistence;
using Overseer.Sandboxes;
using Overseer.Settings;
using Overseer.Specs;

namespace Overseer.Implementation;

/// <summary>
/// The Squad at work: carries out an Intent's Tasks in its prepared Sandbox, one at a time (ADR 0001), in their order, each in a
/// new agent session for the Task's role. Once the agent says it is done, Overseer verifies the work (<see cref="Verification"/>,
/// the full one for the fix-up Task), and that a Designer handed in screenshots (<see cref="TaskScreenshots"/>), and tells the
/// agent what failed, for it to fix, until the work passes or the allowed attempts are used up, which makes the Task a Failed
/// Task. Work that passes is committed and pushed to the Intent branch
/// (<see cref="IntentBranches"/>); only then is the Task Done and the next one started. What each session reports is kept in its log
/// (<see cref="SessionLogs"/>).
/// </summary>
public sealed class TaskLoop(
    IntentTasks tasks, VerificationAttempts attempts, TaskQuestions questions, Verification verification, IntentBranches branches,
    SpecStore specs, SandboxRegistry sandboxes, SessionLogs logs, SettingsStore settings, RepositoryCommands commands,
    TaskScreenshots screenshots, ILogger<TaskLoop> logger)
{
    /// <summary>
    /// Carries out the Intent's Tasks that aren't Done until none is left, or one can't be finished. A Task whose verified work
    /// couldn't be pushed before is only pushed, unless the push was rejected: then an agent works on it again, told why.
    /// </summary>
    /// <param name="changed">Called whenever a Task changes, once the change is committed.</param>
    /// <param name="passedFinalVerification">
    /// Records, as the fix-up Task becomes Done and in the same transaction, that the work has passed its final verification, which
    /// that Task's verification is, with the repositories that are each to have a pull request.
    /// </param>
    /// <returns>
    /// Why a Task couldn't be finished, for the caller to fail the implementation with, which puts that Task, if still Running, back
    /// to Pending; <c>null</c> once every Task is Done.
    /// </returns>
    public async Task<string?> RunAsync(
        Intent intent, Sandbox sandbox, Action changed,
        Func<OverseerDbContext, IReadOnlyList<RepositoryName>, CancellationToken, Task> passedFinalVerification,
        CancellationToken cancellationToken)
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
            if (!task.IsFixUp)
            {
                await tasks.FinishAsync(intent.Id, task.Number, (_, _) => Task.CompletedTask, cancellationToken);
            }
            else
            {
                IReadOnlyList<RepositoryName> toPull;
                try
                {
                    toPull = await branches.ChangingDefaultBranchAsync(sandbox, intent.RepositoriesInScope, cancellationToken);
                }
                catch (InvalidOperationException exception)
                {
                    return await commands.RedactedAsync(exception.Message, cancellationToken);
                }
                await tasks.FinishAsync(
                    intent.Id, task.Number, (database, stopping) => passedFinalVerification(database, toPull, stopping),
                    cancellationToken);
            }
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
        // The token may have been replaced while the agent worked, or since the push failed.
        if (unpushed.Count > 0 && await RefreshGitHubTokenAsync(intent, sandbox, cancellationToken) is { } notRefreshed)
        {
            return notRefreshed;
        }
        foreach (var repository in unpushed)
        {
            if (await branches.PushAsync(sandbox, repository, intent.Branch, cancellationToken) is { } notPushed)
            {
                if (notPushed.Rejected)
                {
                    // Pushing the same again would be rejected again: the Task's next run has an agent deal with it.
                    await tasks.RejectPushAsync(
                        intent.Id, task.Number, await commands.RedactedAsync(notPushed.Error, cancellationToken), cancellationToken);
                    changed();
                }
                return notPushed.Error;
            }
        }
        return null;
    }

    /// <summary>
    /// Has a new agent session of the Task's role work on it, verifying its work each time its turn ends as done and telling it what
    /// failed, and stops the session once the work passes or the Task can't go on. A turn that ends in a question instead waits
    /// for the human's answer (<see cref="TaskQuestions"/>), which goes to the same session. The messages the human sends meanwhile
    /// (<see cref="SessionLogs.SendMessageAsync"/>) each become a turn of their own, in order, after a turn that ends as done and
    /// before its work is verified; work that passes with one waiting is verified again after the turns they start.
    /// </summary>
    private async Task<Work> CarryOutAsync(
        Intent intent, Sandbox sandbox, IntentTask task, Action changed, CancellationToken cancellationToken)
    {
        var approved = await specs.ApprovedOfAsync(intent, cancellationToken);
        var taskCount = (await tasks.OfAsync(intent.Id, cancellationToken)).Count;
        var failedLast = (await attempts.OfAsync(intent.Id, cancellationToken))
            .LastOrDefault(attempt => attempt.TaskNumber == task.Number)?.FailedSteps ?? [];
        var message = TaskInstructions.FirstMessage(intent, approved, task, taskCount, failedLast);
        var run = await attempts.NextRunAsync(intent.Id, task.Number, cancellationToken);
        // The agent works with GitHub too, as when it fetches the Intent branch.
        if (await RefreshGitHubTokenAsync(intent, sandbox, cancellationToken) is { } notRefreshed)
        {
            return new Work.Unfinished(notRefreshed);
        }
        IAgentSession session;
        try
        {
            if (task.Role == AgentRole.Designer)
            {
                await screenshots.DiscardHandedInAsync(sandbox, cancellationToken);
            }
            session = await sandboxes.StartAgentSessionAsync(sandbox, task.Role, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return SessionFailed(intent, task, exception);
        }
        await using var stopping = session;
        // Whether a Designer's session has handed in screenshots that still stand, which its Task needs to be Done.
        var screenshotsHandedIn = false;
        await using var log = await logs.OpenAsync(new TaskKey(intent.Id, task.Number), task.Role, cancellationToken);
        // The number of the last verification attempt this session made, and how many of them failed. A turn that ends in a
        // question, or with a message of the human's waiting, leads to none.
        var attemptNumber = 0;
        var failures = 0;
        // The human's message the next turn delivers; null when it says something else.
        SentMessage? delivering = null;
        void Deliver(SentMessage sent) => (delivering, message) = (sent, TaskInstructions.MessageOfTheHuman(task, sent.Text));
        while (true)
        {
            AgentOutcome outcome;
            try
            {
                outcome = await log.TakeTurnAsync(session, message, cancellationToken, delivering);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                return SessionFailed(intent, task, exception);
            }
            delivering = null;
            if (outcome is AgentOutcome.AskedQuestion asked)
            {
                // The agent has tokens to work with, so it can say one; a secret never goes into PostgreSQL.
                var question = await sandboxes.WithoutSecretsAsync(asked.Question, cancellationToken);
                // Ahead of the question that turns the Intent orange, so that the stream shows it by then.
                await log.AskedAsync(question);
                var answer = await questions.AskAsync(intent.Id, task.Number, question, changed, cancellationToken);
                await log.AnsweredAsync(answer);
                // The answer may be a long time coming, and the token replaced meanwhile.
                if (await RefreshGitHubTokenAsync(intent, sandbox, cancellationToken) is { } notRefreshedSince)
                {
                    return new Work.Unfinished(notRefreshedSince);
                }
                message = TaskInstructions.Answer(task, answer);
                continue;
            }
            // Before verification, which then sees what the turn it starts did.
            if (await log.Inbox.NextAsync(cancellationToken) is { } waiting)
            {
                Deliver(waiting);
                continue;
            }

            attemptNumber++;
            VerificationAttempt attempt;
            IReadOnlyList<RepositoryName> changedRepositories;
            try
            {
                // Ahead of the commands of the verification, so that what is taken is what the agent left.
                var withoutScreenshots = task.Role == AgentRole.Designer
                    ? await screenshots.TakeAsync(intent.Id, task.Number, sandbox, screenshotsHandedIn, cancellationToken)
                    : null;
                screenshotsHandedIn = withoutScreenshots is null;
                var verified = task.IsFixUp
                    ? await verification.VerifyAllAsync(sandbox, intent, cancellationToken)
                    : await verification.VerifyAsync(sandbox, intent, task.Repositories, cancellationToken);
                attempt = new VerificationAttempt(
                    task.Number, run, attemptNumber,
                    withoutScreenshots is null ? verified.Steps : [.. verified.Steps, withoutScreenshots]);
                changedRepositories = verified.Changed;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Task {Task} of the Intent {IntentId} could not be verified.", task.Number, intent.Id);
                return new Work.Unfinished($"Task {task.Number} could not be verified: {exception.Message}");
            }
            await attempts.RecordAsync(intent.Id, attempt, cancellationToken);
            changed();
            var failed = attempt.FailedSteps;
            if (failed.Count == 0)
            {
                if (await log.Inbox.NextOrTakeNoMoreAsync(cancellationToken) is not { } sentMeanwhile)
                {
                    return new Work.Verified(changedRepositories);
                }
                // The Task isn't Done yet: the work is verified again after the turn the message starts.
                Deliver(sentMeanwhile);
                continue;
            }
            // An attempt that passed isn't counted against the allowed ones, though a message had the work verified again after it.
            failures++;
            // As the Settings say now, so that raising the limit also helps a Task being worked on.
            var allowed = (await settings.GetOperatingLimitsAsync(cancellationToken)).VerificationAttemptsPerTask;
            if (failures >= allowed)
            {
                await tasks.EnterStateAsync(intent.Id, task.Number, IntentTaskState.Failed, cancellationToken);
                changed();
                return new Work.Unfinished(
                    $"Task {task.Number} is a Failed Task: its work didn't pass verification in {failures} "
                    + $"{(failures == 1 ? "attempt" : "attempts")}. In the last one, attempt {attemptNumber}: "
                    + string.Join(" ", failed.Select(step => step.Failure)));
            }
            // A message that waits by now follows this turn.
            message = TaskInstructions.VerificationFailed(task, failed, attemptNumber, failures, allowed);
        }
    }

    /// <summary>Gives the Sandbox the GitHub token in use, which may not be the one it was created with.</summary>
    /// <returns>Why it couldn't be given it; <c>null</c> once it has it.</returns>
    private async Task<string?> RefreshGitHubTokenAsync(Intent intent, Sandbox sandbox, CancellationToken cancellationToken)
    {
        try
        {
            await sandboxes.RefreshGitHubTokenAsync(sandbox, cancellationToken);
            return null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                exception, "The Sandbox {Sandbox} of the Intent {IntentId} could not be given the GitHub token.",
                sandbox.ResourceId, intent.Id);
            return $"The Sandbox could not be given the GitHub token: {exception.Message}";
        }
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
