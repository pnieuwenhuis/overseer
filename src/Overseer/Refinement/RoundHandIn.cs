using System.Text.Json;
using Overseer.Sandboxes;

namespace Overseer.Refinement;

/// <summary>
/// A Round as the Analyst hands it in: a JSON file with its questions in order, each with the Analyst's suggested answer. The
/// Analyst's instructions describe the format (<see cref="AnalystInstructions"/>).
/// </summary>
internal sealed class RoundHandIn : HandIn<IReadOnlyList<HandedInQuestion>>
{
    public static RoundHandIn Instance { get; } = new();

    /// <summary>In the home of the Sandbox's user, outside the repositories.</summary>
    public override string Path => "/home/overseer/.overseer/round.json";

    /// <summary>An example of the format, for the Analyst's instructions.</summary>
    public const string Example = """
        {
          "questions": [
            {
              "question": "Which format should the export have?",
              "suggestedAnswer": "CSV, since every spreadsheet opens it and the orders page already lists plain values."
            }
          ]
        }
        """;

    protected override HandedIn<IReadOnlyList<HandedInQuestion>> Validate(string content) => FromJson(content, root =>
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
        {
            return new HandedIn<IReadOnlyList<HandedInQuestion>>.Invalid(["The file must hold a JSON object whose \"questions\" is a list of questions."]);
        }
        if (questions.GetArrayLength() == 0)
        {
            return new HandedIn<IReadOnlyList<HandedInQuestion>>.Invalid(["A Round needs at least one question."]);
        }

        var errors = new List<string>();
        var read = new List<HandedInQuestion>();
        var number = 0;
        foreach (var question in questions.EnumerateArray())
        {
            number++;
            if (question.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"Question {number} must be an object with \"question\" and \"suggestedAnswer\".");
                continue;
            }
            var text = TextOf(question, "question");
            var suggestion = TextOf(question, "suggestedAnswer");
            if (text is null)
            {
                errors.Add($"Question {number} has no \"question\": it must be text that isn't empty.");
            }
            if (suggestion is null)
            {
                errors.Add($"Question {number} has no \"suggestedAnswer\": it must be text that isn't empty.");
            }
            if (text is not null && suggestion is not null)
            {
                read.Add(new HandedInQuestion(text, suggestion));
            }
        }
        return errors.Count == 0
            ? new HandedIn<IReadOnlyList<HandedInQuestion>>.Valid(read)
            : new HandedIn<IReadOnlyList<HandedInQuestion>>.Invalid(errors);
    });
}

/// <summary>A question of a Round, with the Analyst's suggested answer to it.</summary>
internal sealed record HandedInQuestion(string Text, string SuggestedAnswer);
