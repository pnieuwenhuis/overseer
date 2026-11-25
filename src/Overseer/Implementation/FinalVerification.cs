using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Sandboxes;

namespace Overseer.Implementation;

/// <summary>
/// The final verification: once every Task of an Intent is Done, the full verification (<see cref="Verification.VerifyAllAsync"/>)
/// of the Intent branch in the Sandbox, so that pull requests are only opened for green code. When it fails, the fix-up Task is
/// added, which the full verification verifies: once that Task passes it, the work has passed its final verification.
/// </summary>
public sealed class FinalVerification(
    Verification verification, IntentBranches branches, IntentTasks tasks, RepositoryCommands commands,
    ILogger<FinalVerification> logger)
{
    private const string FixUpTitle = "Fix what fails the final verification";

    /// <summary>
    /// Builds and tests every Repository in scope, and says which of them changed when all pass, and adds the fix-up Task when they
    /// don't. Only for an Intent that has no fix-up Task yet.
    /// </summary>
    public async Task<FinalVerificationResult> RunAsync(Intent intent, Sandbox sandbox, CancellationToken cancellationToken)
    {
        try
        {
            var steps = (await verification.VerifyAllAsync(sandbox, intent, cancellationToken)).Steps;
            if (steps.Where(step => !step.Passed).ToList() is { Count: > 0 } failed)
            {
                await tasks.AddFixUpAsync(
                    intent.Id, FixUpTitle, TaskInstructions.FixUp(failed), [.. failed.Select(step => step.Repository).Distinct()],
                    cancellationToken);
                return new FinalVerificationResult.FixUpAdded();
            }
            return new FinalVerificationResult.Passed(
                await branches.ChangingDefaultBranchAsync(sandbox, intent.RepositoriesInScope, cancellationToken));
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
