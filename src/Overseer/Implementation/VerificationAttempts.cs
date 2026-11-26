using Microsoft.EntityFrameworkCore;
using Overseer.GitHub;
using Overseer.Persistence;

namespace Overseer.Implementation;

/// <summary>
/// Every verification attempt on a Task, with what was run and what came out of it, kept in PostgreSQL. Attempts are counted per
/// run of a Task, one agent session on it: a new session starts a new count, and the earlier runs' attempts stay.
/// </summary>
public sealed class VerificationAttempts(IDbContextFactory<OverseerDbContext> databases)
{
    /// <summary>Every attempt on the Intent's Tasks, by Task, then in the order they were made.</summary>
    public async Task<IReadOnlyList<VerificationAttempt>> OfAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var stored = await database.VerificationAttempts.AsNoTracking()
            .Where(attempt => attempt.IntentId == intentId)
            .Include(attempt => attempt.Steps)
            .OrderBy(attempt => attempt.TaskNumber).ThenBy(attempt => attempt.Run).ThenBy(attempt => attempt.Number)
            .ToListAsync(cancellationToken);
        return [.. stored.Select(attempt => new VerificationAttempt(
            attempt.TaskNumber, attempt.Run, attempt.Number,
            [.. attempt.Steps.OrderBy(step => step.Number).Select(step => new VerificationStep(
                step.Repository is { } repository ? RepositoryName.Parse(repository) : null, step.What, step.Command, step.Failure,
                step.Output))]))];
    }

    /// <summary>The number of the Task's next run that makes an attempt, from 1.</summary>
    public async Task<int> NextRunAsync(Guid intentId, int taskNumber, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var last = await database.VerificationAttempts
            .Where(attempt => attempt.IntentId == intentId && attempt.TaskNumber == taskNumber)
            .MaxAsync(attempt => (int?)attempt.Run, cancellationToken);
        return (last ?? 0) + 1;
    }

    /// <param name="alongside">What else the attempt records, in the same transaction; <c>null</c> for nothing.</param>
    public async Task RecordAsync(
        Guid intentId, VerificationAttempt attempt, Func<OverseerDbContext, CancellationToken, Task>? alongside,
        CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        database.VerificationAttempts.Add(new VerificationAttemptRecord
        {
            IntentId = intentId,
            TaskNumber = attempt.TaskNumber,
            Run = attempt.Run,
            Number = attempt.Number,
            Steps = [.. attempt.Steps.Select((step, index) => new VerificationStepRecord
            {
                Number = index + 1,
                Repository = step.Repository?.ToString(),
                What = StorableText.Of(step.What),
                Command = StorableText.Of(step.Command),
                Failure = StorableText.Of(step.Failure),
                Output = StorableText.Of(step.Output),
            })],
        });
        await database.SaveChangesAsync(cancellationToken);
        if (alongside is not null)
        {
            await alongside(database, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>One verification of a Task's work: the <paramref name="Number"/>th, from 1, of the Task's <paramref name="Run"/>th run.</summary>
/// <param name="Steps">
/// What was run to verify it, in order. A Task's verification stops at the first step that fails; a full one, of the fix-up Task,
/// goes on with the other repositories, so that several can have failed.
/// </param>
public sealed record VerificationAttempt(int TaskNumber, int Run, int Number, IReadOnlyList<VerificationStep> Steps)
{
    /// <summary>The steps that failed, in order; none when the attempt passed.</summary>
    public IReadOnlyList<VerificationStep> FailedSteps => [.. Steps.Where(step => !step.Passed)];
}

public static class VerificationAttemptRuns
{
    /// <summary>The number of the Task's last run that made one of these attempts; 0 when none did.</summary>
    public static int LastRunOf(this IEnumerable<VerificationAttempt> attempts, int taskNumber) =>
        attempts.Where(attempt => attempt.TaskNumber == taskNumber).Select(attempt => attempt.Run).DefaultIfEmpty().Max();
}

/// <summary>A verification attempt, as PostgreSQL holds it.</summary>
public sealed class VerificationAttemptRecord
{
    public required Guid IntentId { get; init; }
    public required int TaskNumber { get; init; }
    public required int Run { get; init; }
    public required int Number { get; init; }
    public required List<VerificationStepRecord> Steps { get; init; }
}

/// <summary>A step of a verification attempt, as PostgreSQL holds it.</summary>
public sealed class VerificationStepRecord
{
    public Guid IntentId { get; init; }
    public int TaskNumber { get; init; }
    public int Run { get; init; }
    public int AttemptNumber { get; init; }
    public required int Number { get; init; }

    /// <summary><c>owner/name</c> of the repository it verifies; <c>null</c> when it verifies none.</summary>
    public required string? Repository { get; init; }

    public required string What { get; init; }
    public required string? Command { get; init; }
    public required string? Failure { get; init; }
    public required string Output { get; init; }
}
