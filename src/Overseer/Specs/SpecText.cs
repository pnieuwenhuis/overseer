namespace Overseer.Specs;

/// <summary>A Spec as Markdown, for an agent to read.</summary>
internal static class SpecText
{
    /// <summary>The whole of <paramref name="spec"/>: its prose sections, those that apply, followed by its Tasks.</summary>
    public static string Of(Spec spec)
    {
        List<string> sections =
        [
            $"## {SpecLabels.Summary}\n\n{spec.Summary}",
            $"## {SpecLabels.Approach}\n\n{spec.Approach}",
            $"## {SpecLabels.Decisions}\n\n" + (spec.Decisions.Count == 0
                ? "None."
                : string.Join("\n\n", spec.Decisions.Select((decision, index) =>
                    $"{index + 1}. {(decision.IsAnalystsChoice ? "(Analyst's Choice) " : "")}{decision.Text}"))),
            $"## {SpecLabels.AcceptanceCriteria}\n\n{spec.AcceptanceCriteria}",
            $"## {SpecLabels.Dependencies}\n\n{spec.Dependencies}",
        ];
        foreach (var (label, text) in new (string, string?)[]
        {
            (SpecLabels.ApiEndpointChanges, spec.ApiEndpointChanges), (SpecLabels.DataModelChanges, spec.DataModelChanges), (SpecLabels.UiChanges, spec.UiChanges),
        })
        {
            if (text is not null)
            {
                sections.Add($"## {label}\n\n{text}");
            }
        }
        sections.Add($"## {SpecLabels.Tasks}\n\n" + string.Join("\n\n", spec.Tasks.Select(task =>
            $"### Task {task.Number}: {task.Title}\n\nRole: {task.Role}. Repositories: {string.Join(", ", task.Repositories)}.\n\n{task.Description}")));
        return string.Join("\n\n", sections);
    }
}
