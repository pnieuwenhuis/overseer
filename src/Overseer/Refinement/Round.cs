namespace Overseer.Refinement;

/// <summary>A Round of the Refinement transcript: the Analyst's numbered questions, with the human's answers once given.</summary>
/// <param name="Number">Its number within the Intent's Refinement, from 1.</param>
public sealed record Round(int Number, IReadOnlyList<RoundQuestion> Questions, bool IsAnswered);

/// <summary>A question of a Round, numbered within it from 1, with the Analyst's suggested answer.</summary>
/// <param name="Answer"><c>null</c> until the Round is answered.</param>
public sealed record RoundQuestion(int Number, string Text, string SuggestedAnswer, RoundAnswer? Answer);

/// <summary>
/// The answer recorded for a question: the human's own, or, when they left it to the Analyst, its suggested answer as the
/// Analyst's Choice.
/// </summary>
public sealed record RoundAnswer(string Text, bool IsAnalystsChoice);

/// <summary>A Round of an Intent's Refinement transcript, as PostgreSQL holds it.</summary>
public sealed class RoundRecord
{
    public required Guid IntentId { get; init; }
    public required int Number { get; init; }
    public required DateTimeOffset AskedAt { get; init; }

    /// <summary>When the human's answers were recorded; <c>null</c> while it has none.</summary>
    public DateTimeOffset? AnsweredAt { get; set; }

    public List<RoundQuestionRecord> Questions { get; init; } = [];
}

/// <summary>A question of a Round, as PostgreSQL holds it, with its answer once the Round is answered.</summary>
public sealed class RoundQuestionRecord
{
    public required Guid IntentId { get; init; }
    public required int RoundNumber { get; init; }
    public required int Number { get; init; }
    public required string Text { get; init; }
    public required string SuggestedAnswer { get; init; }

    /// <summary>The human's answer, or the suggested answer when it is the Analyst's Choice; <c>null</c> until answered.</summary>
    public string? Answer { get; set; }

    public bool IsAnalystsChoice { get; set; }
}
