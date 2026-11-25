using Overseer.Intents;
using Overseer.Sandboxes;
using Overseer.Specs;

namespace Overseer.Implementation;

/// <summary>
/// The user's Edit &amp; retry of a Failed Task: changes the Intent's Task, checked as a Task of an edited Spec is, and has it carried
/// out again (<see cref="TaskRunner.RetryTaskAsync"/>). The Approved Spec stays as approved.
/// </summary>
public sealed class TaskEdits(IntentLifecycle lifecycle, IntentTasks tasks, TaskRunner runner)
{
    /// <summary>
    /// Why the Task of <paramref name="intent"/> with this number can't be edited and retried at the moment: only a Failed Task can,
    /// once the implementation has stopped at it. <c>null</c> when it can.
    /// </summary>
    public async Task<string?> WhyNotEditableAsync(Intent? intent, int number, CancellationToken cancellationToken)
    {
        if (intent is null)
        {
            return "There is no such Intent: it may have been deleted.";
        }
        if (intent.State != IntentState.Implementing)
        {
            return $"This Intent is {intent.State.InWords()}, and only a Failed Task of an Implementing Intent can be edited and retried.";
        }
        if ((await tasks.OfAsync(intent.Id, cancellationToken)).SingleOrDefault(task => task.Number == number) is not { } task)
        {
            return $"The Intent has no Task {number}.";
        }
        if (task.State != IntentTaskState.Failed)
        {
            return $"Task {number} is {task.State}, and only a Failed Task can be edited and retried.";
        }
        return await runner.ProgressOfAsync(intent, cancellationToken) is { Phase: ImplementationPhase.Failed }
            ? null
            : $"The implementation is still stopping at Task {number}: try again in a moment.";
    }

    /// <summary>
    /// Saves <paramref name="edit"/> as the Intent's Task with this number and retries it. Refused, changing nothing, unless the
    /// Task can be edited, hasn't failed again since <paramref name="lastRun"/>, and the edit is a valid Task.
    /// </summary>
    /// <param name="lastRun">As for <see cref="TaskRunner.RetryTaskAsync"/>.</param>
    /// <returns>Why nothing was saved; empty once the edit is saved and the Task retried.</returns>
    public async Task<IReadOnlyList<string>> SaveAndRetryAsync(
        Guid intentId, int number, int lastRun, EditedTask edit, CancellationToken cancellationToken)
    {
        var intent = await lifecycle.FindAsync(intentId, cancellationToken);
        if (await WhyNotEditableAsync(intent, number, cancellationToken) is { } notEditable)
        {
            return [notEditable];
        }
        // The one Task is all that is checked, so no missing Repository Manifest asks anything of it.
        switch (new SpecHandIn(intent!.RepositoriesInScope, lackingManifest: []).CheckTask(number, edit))
        {
            case HandedIn<SpecTask>.Invalid(var errors):
                return errors;
            case HandedIn<SpecTask>.Valid(var task):
                return await runner.RetryTaskAsync(intentId, number, lastRun, task, cancellationToken)
                    ? []
                    : [await WhyNotRetriedAsync(intentId, number, cancellationToken)];
            default:
                throw new InvalidOperationException("An edit is always handed in.");
        }
    }

    /// <summary>Why a valid edit of a Task that could be edited a moment ago was refused.</summary>
    private async Task<string> WhyNotRetriedAsync(Guid intentId, int number, CancellationToken cancellationToken) =>
        await WhyNotEditableAsync(await lifecycle.FindAsync(intentId, cancellationToken), number, cancellationToken)
        // Still a Failed Task, so a failure since the one edited after.
        ?? $"Task {number} has run and failed again since this page was opened. Open Edit & retry again from the Intent's page, "
            + "to change the Task as it is now.";
}
