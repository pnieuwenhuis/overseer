using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Sandboxes;
using Overseer.Secrets;
using Overseer.Specs;

namespace Overseer.Implementation;

/// <summary>
/// The Squad at work: carries out an Intent's Tasks in its prepared Sandbox, one at a time (ADR 0001), always the first Pending
/// one, each in a new agent session for the Task's role.
/// </summary>
public sealed class TaskLoop(
    IntentTasks tasks, SpecStore specs, SandboxRegistry sandboxes, ISecretReader secrets, ILogger<TaskLoop> logger)
{
    /// <summary>Carries out the Intent's Pending Tasks until none is left, or the session on one fails.</summary>
    /// <param name="changed">Called whenever a Task changes state, once the change is committed.</param>
    /// <returns>
    /// Why the session on a Task failed, for the caller to fail the implementation with, which puts that Task, still Running, back
    /// to Pending; <c>null</c> once no Task is Pending.
    /// </returns>
    public async Task<string?> RunAsync(Intent intent, Sandbox sandbox, Action changed, CancellationToken cancellationToken)
    {
        while (await tasks.FirstPendingAsync(intent.Id, cancellationToken) is { } task)
        {
            await tasks.EnterStateAsync(intent.Id, task.Number, IntentTaskState.Running, cancellationToken);
            changed();
            if (await CarryOutAsync(intent, sandbox, task, cancellationToken) is { } error)
            {
                // The Sandbox holds the GitHub token, so an agent can say it; a secret never goes into PostgreSQL.
                var token = await secrets.ReadAsync(GitHubToken.SecretName, cancellationToken) ?? "";
                return Redaction.Without(error, token);
            }
            await tasks.EnterStateAsync(intent.Id, task.Number, IntentTaskState.Done, cancellationToken);
            changed();
        }
        return null;
    }

    /// <summary>Has a new agent session of the Task's role work on it, and stops the session once its turn ends.</summary>
    /// <returns>Why the session failed; <c>null</c> once its turn ended as done.</returns>
    private async Task<string?> CarryOutAsync(Intent intent, Sandbox sandbox, IntentTask task, CancellationToken cancellationToken)
    {
        // An Intent has Tasks only once it has an Approved Spec, which they were created from.
        var approved = await specs.VersionOfAsync(intent.Id, intent.ApprovedSpecVersion!.Value, cancellationToken)
            ?? throw new InvalidOperationException($"The Approved Spec of the Intent {intent.Id} is gone.");
        var taskCount = (await tasks.OfAsync(intent.Id, cancellationToken)).Count;
        var message = TaskInstructions.FirstMessage(intent, approved.Spec, task, taskCount);
        try
        {
            await using var session = await sandboxes.StartAgentSessionAsync(sandbox, task.Role, cancellationToken);
            return await session.TakeTurnAsync(message, cancellationToken) switch
            {
                AgentOutcome.AskedQuestion asked =>
                    $"The {task.Role} asked a question during Task {task.Number}, which can't be answered yet: {asked.Question}",
                _ => null,
            };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                exception, "The {Role}'s session for Task {Task} of the Intent {IntentId} failed.", task.Role, task.Number, intent.Id);
            return $"The {task.Role}'s session for Task {task.Number} failed: {exception.Message}";
        }
    }
}
