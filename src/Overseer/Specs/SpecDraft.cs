namespace Overseer.Specs;

/// <summary>
/// A Spec as the user edits it, before it is saved as a new version: what they entered, which needn't be a valid Spec yet. Its
/// decisions aren't edited: they are those of the version edited. Saving checks it as the Analyst's Spec is checked.
/// </summary>
/// <param name="ApiEndpointChanges">Markdown; <c>null</c> or blank when the Intent changes no API endpoint, and likewise below.</param>
/// <param name="Tasks">In the order they are carried out.</param>
public sealed record SpecDraft(
    string? Summary,
    string? Approach,
    string? AcceptanceCriteria,
    string? Dependencies,
    string? ApiEndpointChanges,
    string? DataModelChanges,
    string? UiChanges,
    IReadOnlyList<SpecTaskDraft> Tasks)
{
    /// <summary>The draft that edits <paramref name="spec"/>, before any change.</summary>
    public static SpecDraft Of(Spec spec) => new(
        spec.Summary,
        spec.Approach,
        spec.AcceptanceCriteria,
        spec.Dependencies,
        spec.ApiEndpointChanges,
        spec.DataModelChanges,
        spec.UiChanges,
        [.. spec.Tasks.Select(task => new SpecTaskDraft(
            task.Title, task.Description, task.Role.ToString(), [.. task.Repositories.Select(repository => repository.ToString())]))]);

    /// <summary>The draft with an empty Task added at the end.</summary>
    public SpecDraft WithTaskAdded() => this with { Tasks = [.. Tasks, SpecTaskDraft.Empty] };

    /// <summary>The draft without Task <paramref name="number"/>, counted from 1; as it is when there is no such Task.</summary>
    public SpecDraft WithoutTask(int number) =>
        IsTask(number) ? this with { Tasks = [.. Tasks.Where((_, index) => index != number - 1)] } : this;

    /// <summary>
    /// The draft with Task <paramref name="number"/>, counted from 1, swapped with the one before it (<paramref name="by"/> -1) or after
    /// it (1); as it is when there is no Task to swap with.
    /// </summary>
    public SpecDraft WithTaskMoved(int number, int by)
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
}

/// <summary>A Task as the user edits it, which needn't be valid yet.</summary>
/// <param name="Role">As entered: valid only when it names Designer or Developer.</param>
/// <param name="Repositories">As entered, each as owner/name: valid only when they are Repositories in scope.</param>
public sealed record SpecTaskDraft(string? Title, string? Description, string? Role, IReadOnlyList<string> Repositories)
{
    /// <summary>A Task just added, with nothing entered, to be carried out by a Developer.</summary>
    public static readonly SpecTaskDraft Empty = new("", "", "Developer", []);
}
