using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Overseer.Intents;
using Overseer.Persistence;
using Overseer.Sandboxes;

namespace Overseer.Implementation;

/// <summary>
/// The questions the agents ask during an Intent's Tasks, with the human's answers, kept in PostgreSQL. While one waits for its
/// answer, the implementation is <see cref="ImplementationPhase.AwaitingAnswer"/>, which is what makes it open: one whose session
/// ended without an answer, as when Overseer restarted, stays unanswered.
/// </summary>
public sealed class TaskQuestions(SandboxRegistry sandboxes, IDbContextFactory<OverseerDbContext> databases, TimeProvider time)
{
    /// <summary>The sessions waiting for an answer, by Intent, each completed with it.</summary>
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> _waiting = new();

    /// <summary>
    /// Puts the question the Task's agent ended its turn with to the human, which leaves the implementation awaiting their answer,
    /// and waits for it, for as long as that takes: unlike a Refinement, an implementation has no idle timeout.
    /// </summary>
    /// <param name="question">As <see cref="SandboxRegistry.WithoutSecretsAsync"/> gives it.</param>
    /// <param name="changed">Called once the question is committed.</param>
    /// <returns>The human's answer, without any secret.</returns>
    public async Task<string> AskAsync(
        Guid intentId, int taskNumber, string question, Action changed, CancellationToken cancellationToken)
    {
        var answered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Ahead of the question, so that no answer comes before there is something to tell.
        _waiting[intentId] = answered;
        try
        {
            await using (var database = await databases.CreateDbContextAsync(cancellationToken))
            {
                await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
                // One Task runs per Intent at a time, so no other question is added meanwhile.
                var latest = await database.TaskQuestions.Where(asked => asked.IntentId == intentId)
                    .MaxAsync(asked => (int?)asked.Number, cancellationToken) ?? 0;
                database.TaskQuestions.Add(new TaskQuestionRecord
                {
                    IntentId = intentId,
                    Number = latest + 1,
                    TaskNumber = taskNumber,
                    Question = question,
                    AskedAt = time.GetUtcNow(),
                });
                await database.SaveChangesAsync(cancellationToken);
                await database.Implementations
                    .Where(implementation => implementation.IntentId == intentId)
                    .ExecuteUpdateAsync(
                        implementation => implementation.SetProperty(record => record.Phase, ImplementationPhase.AwaitingAnswer),
                        cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            changed();
            return await answered.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _waiting.TryRemove(KeyValuePair.Create(intentId, answered));
        }
    }

    /// <summary>
    /// Records the human's answer to the question a session waits on, which leaves the implementation ready again, and hands it to
    /// that session. Refused unless that question awaits its answer now, so an answer is taken only once. A secret in it is
    /// replaced by <c>***</c>, in what is kept and in what the agent gets.
    /// </summary>
    /// <param name="number">The question's number among the Intent's.</param>
    /// <returns>Why the answer was refused; <c>null</c> when it is recorded and on its way to the agent.</returns>
    public async Task<string?> AnswerAsync(Guid intentId, int number, string answer, CancellationToken cancellationToken)
    {
        var written = StorableText.OfWritten(answer);
        if (written.Length == 0)
        {
            return "An answer needs text: write what the agent should know.";
        }
        var stored = await sandboxes.WithoutSecretsAsync(written, cancellationToken);
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            // The implementation's row is where answers queue: of two submitted at once, the second finds it no longer awaiting one.
            var taken = await database.Implementations
                .Where(implementation => implementation.IntentId == intentId
                    && implementation.Phase == ImplementationPhase.AwaitingAnswer
                    && Open(database).Any(question => question.IntentId == intentId && question.Number == number))
                .ExecuteUpdateAsync(
                    implementation => implementation.SetProperty(record => record.Phase, ImplementationPhase.Ready), cancellationToken);
            var answeredAt = time.GetUtcNow();
            // Only while it has no answer, as read now rather than before the wait for that row.
            if (taken == 0 || await database.TaskQuestions
                    .Where(question => question.IntentId == intentId && question.Number == number && question.Answer == null)
                    .ExecuteUpdateAsync(
                        question => question
                            .SetProperty(record => record.Answer, stored)
                            .SetProperty(record => record.AnsweredAt, answeredAt),
                        cancellationToken) == 0)
            {
                return await WhyNotAnswerableAsync(database, intentId, number, cancellationToken);
            }
            // Not cancelled: an answer committed has to reach the session that waits for it.
            await transaction.CommitAsync(CancellationToken.None);
        }
        if (_waiting.TryGetValue(intentId, out var waiting))
        {
            waiting.TrySetResult(stored);
        }
        return null;
    }

    /// <summary>The questions asked during the Intent's Tasks, in the order they were asked.</summary>
    public async Task<IReadOnlyList<TaskQuestion>> OfAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        // In one query, so that which question is open fits the answers read with it.
        var open = Open(database);
        return await database.TaskQuestions
            .Where(question => question.IntentId == intentId)
            .OrderBy(question => question.Number)
            .Select(question => new TaskQuestion(
                question.TaskNumber, question.Number, question.Question, question.Answer,
                open.Any(waiting => waiting.IntentId == question.IntentId && waiting.Number == question.Number)))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Why an answer to the question can't be taken now, said for the human.</summary>
    private static async Task<string> WhyNotAnswerableAsync(
        OverseerDbContext database, Guid intentId, int number, CancellationToken cancellationToken)
    {
        var question = await database.TaskQuestions
            .Where(question => question.IntentId == intentId && question.Number == number)
            .Select(question => new { question.AnsweredAt })
            .SingleOrDefaultAsync(cancellationToken);
        return question switch
        {
            null => $"No agent asked a question {number}.",
            { AnsweredAt: not null } => $"Question {number} is already answered.",
            _ => $"Question {number} no longer awaits an answer: the session that asked it has ended.",
        };
    }

    /// <summary>The questions that wait for their answer: the latest of each implementation that awaits one.</summary>
    internal static IQueryable<TaskQuestionRecord> Open(OverseerDbContext database) =>
        database.TaskQuestions.Where(question => question.Answer == null
            && !database.TaskQuestions.Any(later => later.IntentId == question.IntentId && later.Number > question.Number)
            && database.Implementations.Any(implementation => implementation.IntentId == question.IntentId
                && implementation.Phase == ImplementationPhase.AwaitingAnswer
                && database.Intents.Any(intent => intent.Id == question.IntentId && intent.State == IntentState.Implementing)));
}

/// <summary>A question an agent asked during a Task, numbered in the order the Intent's questions were asked, from 1.</summary>
/// <param name="Answer">The human's answer; <c>null</c> while there is none.</param>
/// <param name="AwaitsAnswer">
/// Whether its session waits for the answer. One without an answer that doesn't, never gets one: its session has ended.
/// </param>
public sealed record TaskQuestion(int TaskNumber, int Number, string Question, string? Answer, bool AwaitsAnswer);

/// <summary>
/// Turns the light of an Intent whose Task's agent waits for the answer to its question orange, for the Intent Lifecycle, which
/// derives the light.
/// </summary>
public sealed class ImplementationAttention : IIntentAttention
{
    public IQueryable<Guid> NeedingAttentionIn(OverseerDbContext database) =>
        TaskQuestions.Open(database).Select(question => question.IntentId);
}

/// <summary>A question of a Task's agent, as PostgreSQL holds it.</summary>
public sealed class TaskQuestionRecord
{
    public required Guid IntentId { get; init; }
    public required int Number { get; init; }

    /// <summary>The number of the Intent's Task during which it was asked.</summary>
    public required int TaskNumber { get; init; }

    public required string Question { get; init; }
    public required DateTimeOffset AskedAt { get; init; }

    /// <summary>The human's answer, with when they gave it; <c>null</c> while there is none.</summary>
    public string? Answer { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }
}
