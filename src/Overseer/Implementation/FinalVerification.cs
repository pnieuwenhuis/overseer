using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Sandboxes;

namespace Overseer.Implementation;

/// <summary>
/// The final verification: once every Task of an Intent is Done, the full verification (<see cref="Verification.VerifyAllAsync"/>)
/// of the Intent branch in the Sandbox, so that pull requests are only opened for green code.
/// </summary>
public sealed class FinalVerification(
    Verification verification, IntentBranches branches, IntentTasks tasks, RepositoryCommands commands,
    ILogger<FinalVerification> logger)
{
    private const string FixUpTitle = "Fix what fails the final verification";

    /// <summary>
    /// Builds and tests every Repository in scope, and says which of them changed when all pass, or what to do about it when they
    /// don't.
    /// </summary>
    public async Task<FinalVerificationResult> RunAsync(Intent intent, Sandbox sandbox, CancellationToken cancellationToken)
    {
        try
        {
            var steps = (await verification.VerifyAllAsync(sandbox, intent, cancellationToken)).Steps;
            if (steps.Where(step => !step.Passed).ToList() is { Count: > 0 } failed)
            {
                var added = await tasks.AddFixUpAsync(
                    intent.Id, FixUpTitle, TaskInstructions.FixUp(failed), [.. failed.Select(step => step.Repository).Distinct()],
                    cancellationToken);
                // The fix-up Task has been carried out, and there is no second one.
                return added
                    ? new FinalVerificationResult.FixUpAdded()
                    : new FinalVerificationResult.Failed(
                        $"The final verification failed after the fix-up Task was carried out.\n\n{TaskInstructions.FailuresOf(failed)}");
            }
            var changed = new List<RepositoryName>();
            foreach (var repository in intent.RepositoriesInScope)
            {
                if (await branches.PushedWorkDiffersFromDefaultBranchAsync(sandbox, repository, cancellationToken))
                {
                    changed.Add(repository);
                }
            }
            return new FinalVerificationResult.Passed(changed);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "The final verification of the Intent {IntentId} could not be run.", intent.Id);
            return new FinalVerificationResult.Failed(
                await commands.RedactedAsync($"The final verification could not be run: {exception.Message}", cancellationToken));
        }
    }
}

/// <summary>What a final verification came to.</summary>
public abstract record FinalVerificationResult
{
    private FinalVerificationResult()
    {
    }

    /// <summary>Every Repository in scope passed.</summary>
    /// <param name="Changed">The ones whose Intent branch changes their default branch, in the Intent's order.</param>
    public sealed record Passed(IReadOnlyList<RepositoryName> Changed) : FinalVerificationResult;

    /// <summary>It failed, and the Intent has a fix-up Task now, to be carried out.</summary>
    public sealed record FixUpAdded : FinalVerificationResult;

    /// <summary>The implementation can't go on; <paramref name="Error"/> says why.</summary>
    public sealed record Failed(string Error) : FinalVerificationResult;
}
