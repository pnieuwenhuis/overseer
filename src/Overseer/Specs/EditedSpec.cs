using Overseer.Sandboxes;

namespace Overseer.Specs;

/// <summary>
/// A Spec as the user edits it, before it is saved as a new version: what they entered, which needn't be a valid Spec yet. Saving
/// checks it as the Analyst's Spec is checked.
/// </summary>
/// <param name="Decisions">
/// Each decision's Markdown, in order. Which are Analyst's Choices isn't edited: see <see cref="IsAnalystsChoice"/>.
/// </param>
/// <param name="ApiEndpointChanges">Markdown; <c>null</c> or blank when the Intent changes no API endpoint, and likewise below.</param>
/// <param name="Tasks">In the order they are carried out.</param>
public sealed record EditedSpec(
    string? Summary,
    string? Approach,
    IReadOnlyList<string?> Decisions,
    string? AcceptanceCriteria,
    string? Dependencies,
    string? ApiEndpointChanges,
    string? DataModelChanges,
    string? UiChanges,
    IReadOnlyList<EditedTask> Tasks)
{
    /// <summary>The edit of <paramref name="spec"/>, before any change.</summary>
    public static EditedSpec Of(Spec spec) => new(
        spec.Summary,
        spec.Approach,
        [.. spec.Decisions.Select(decision => decision.Text)],
        spec.AcceptanceCriteria,
        spec.Dependencies,
        spec.ApiEndpointChanges,
        spec.DataModelChanges,
        spec.UiChanges,
        [.. spec.Tasks.Select(task => new EditedTask(
            task.Title, task.Description, task.Role.ToString(), [.. task.Repositories.Select(repository => repository.ToString())]))]);

    /// <summary>
    /// Whether the decision <paramref name="text"/> is an Analyst's Choice, given the decisions of the version edited: only when it is
    /// one of their Analyst's Choices, unchanged. A decision the user changed or added is theirs.
    /// </summary>
    public static bool IsAnalystsChoice(string? text, IReadOnlyList<SpecDecision> edited) =>
        edited.Any(decision => decision.IsAnalystsChoice && SameText(decision.Text, text));

    /// <summary>The edit with an empty decision added at the end.</summary>
    public EditedSpec WithDecisionAdded() => this with { Decisions = [.. Decisions, ""] };

    /// <summary>The edit without decision <paramref name="number"/>, counted from 1; as it is when there is no such decision.</summary>
    public EditedSpec WithoutDecision(int number) =>
        number >= 1 && number <= Decisions.Count ? this with { Decisions = [.. Decisions.Where((_, index) => index != number - 1)] } : this;

    /// <summary>The edit with an empty Task added at the end.</summary>
    public EditedSpec WithTaskAdded() => this with { Tasks = [.. Tasks, EditedTask.Empty] };

    /// <summary>The edit without Task <paramref name="number"/>, counted from 1; as it is when there is no such Task.</summary>
    public EditedSpec WithoutTask(int number) =>
        IsTask(number) ? this with { Tasks = [.. Tasks.Where((_, index) => index != number - 1)] } : this;

    /// <summary>
    /// The edit with Task <paramref name="number"/>, counted from 1, swapped with the one before it (<paramref name="by"/> -1) or after
    /// it (1); as it is when there is no Task to swap with.
    /// </summary>
    public EditedSpec WithTaskMoved(int number, int by)
    {
        var to = number + by;
        if (!IsTask(number) || !IsTask(to))
        {
            return this;
        }
        var tasks = Tasks.ToList();
        (tasks[number - 1], tasks[to - 1]) = (tasks[to - 1], tasks[number - 1]);
        return this with { Tasks = tasks };
    }

    private bool IsTask(int number) => number >= 1 && number <= Tasks.Count;

    /// <summary>Whether two Markdown texts are the same as a Spec stores them: without surrounding whitespace, with LF line breaks.</summary>
    private static bool SameText(string stored, string? entered) =>
        entered is not null && stored.Trim().ReplaceLineEndings("\n") == entered.Trim().ReplaceLineEndings("\n");
}

/// <summary>A Task as the user edits it, which needn't be valid yet.</summary>
/// <param name="Role">As entered: valid only when it names one of <see cref="SpecHandIn.TaskRoles"/>.</param>
/// <param name="Repositories">As entered, each as owner/name: valid only when they are Repositories in scope.</param>
public sealed record EditedTask(string? Title, string? Description, string? Role, IReadOnlyList<string> Repositories)
{
    /// <summary>A Task just added, with nothing entered, to be carried out by a Developer.</summary>
    public static readonly EditedTask Empty = new("", "", nameof(AgentRole.Developer), []);
}
