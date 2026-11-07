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
    public const string FilePath = "/home/overseer/.overseer/round.json";

    public override string Path => FilePath;

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
        if (ListOf(root, "questions") is not { } questions)
        {
            return new HandedIn<IReadOnlyList<HandedInQuestion>>.Invalid(["The file must hold a JSON object whose \"questions\" is a list of questions."]);
        }
        if (questions.Count == 0)
        {
            return new HandedIn<IReadOnlyList<HandedInQuestion>>.Invalid(["A Round needs at least one question."]);
        }

        var errors = new List<string>();
        var read = ReadEach(questions, "Question", ["question", "suggestedAnswer"], errors, (question, number) =>
        {
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
            return text is not null && suggestion is not null ? new HandedInQuestion(text, suggestion) : null;
        });
        return errors.Count == 0
            ? new HandedIn<IReadOnlyList<HandedInQuestion>>.Valid(read)
            : new HandedIn<IReadOnlyList<HandedInQuestion>>.Invalid(errors);
    });
}

/// <summary>A question of a Round, with the Analyst's suggested answer to it.</summary>
internal sealed record HandedInQuestion(string Text, string SuggestedAnswer);
