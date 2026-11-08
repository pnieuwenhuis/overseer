using Overseer.Sandboxes;

namespace Overseer.Specs;

/// <summary>
/// How the reasons a Spec isn't valid name what is wrong, for whoever has to act on them: the Analyst, who wrote the Spec as JSON, or
/// the user, who edited it in the Portal's form. <see cref="SpecHandIn"/> decides what is wrong, the same for both.
/// </summary>
internal abstract class SpecWording
{
    /// <summary>In the Analyst's terms: the JSON properties of the format its instructions describe.</summary>
    public static readonly SpecWording Analyst = new AnalystWording();

    /// <summary>In the user's terms: the labels of the Spec's editor.</summary>
    public static readonly SpecWording Editor = new EditorWording();

    /// <summary>The required prose section that is the JSON property <paramref name="section"/> is missing or blank.</summary>
    public abstract string NoSection(string section);

    public abstract string NoDecision(int number);

    public abstract string NoTasks();

    public abstract string NoTitle(int task);

    public abstract string NoDescription(int task);

    public abstract string NoRole(int task);

    /// <summary>The Task has <paramref name="role"/>, which isn't one of <see cref="SpecHandIn.TaskRoles"/>.</summary>
    public abstract string OtherRole(int task, string role);

    /// <param name="inScope">The Repositories in scope, listed.</param>
    public abstract string NoRepository(int task, string inScope);

    private sealed class AnalystWording : SpecWording
    {
        private static readonly string Roles = string.Join(" or ", SpecHandIn.TaskRoles.Select(role => $"\"{role}\""));

        public override string NoSection(string section) => $"The Spec has no \"{section}\": it must be Markdown text that isn't empty.";

        public override string NoDecision(int number) => $"Decision {number} has no \"decision\": it must be Markdown text that isn't empty.";

        public override string NoTasks() => "The Spec has no Tasks: \"tasks\" must be a list of at least one Task.";

        public override string NoTitle(int task) => $"Task {task} has no \"title\": it must be text that isn't empty.";

        public override string NoDescription(int task) => $"Task {task} has no \"description\": it must be Markdown text that isn't empty.";

        public override string NoRole(int task) => $"Task {task} has no \"role\": it must be {Roles}.";

        public override string OtherRole(int task, string role) => $"Task {task} has the role \"{role}\": it must be {Roles}.";

        public override string NoRepository(int task, string inScope) =>
            $"Task {task} touches no repository: \"repositories\" must list at least one of the Repositories in scope, {inScope}.";
    }

    private sealed class EditorWording : SpecWording
    {
        private static readonly string Roles = string.Join(" or ", SpecHandIn.TaskRoles);

        /// <summary>The editor's label of each required prose section, by its JSON property.</summary>
        private static readonly Dictionary<string, string> Sections = new()
        {
            ["summary"] = SpecLabels.Summary,
            ["approach"] = SpecLabels.Approach,
            ["acceptanceCriteria"] = SpecLabels.AcceptanceCriteria,
            ["dependencies"] = SpecLabels.Dependencies,
        };

        public override string NoSection(string section) => $"{Sections[section]} is empty: write it in Markdown.";

        public override string NoDecision(int number) => $"Decision {number} is empty: write it in Markdown, or remove it.";

        public override string NoTasks() => "The Spec has no Tasks: add at least one.";

        public override string NoTitle(int task) => $"Task {task}'s title is empty.";

        public override string NoDescription(int task) => $"Task {task}'s description is empty: write it in Markdown.";

        public override string NoRole(int task) => $"Task {task} has no role: choose {Roles}.";

        public override string OtherRole(int task, string role) => $"Task {task}'s role is {role}: choose {Roles}.";

        public override string NoRepository(int task, string inScope) =>
            $"Task {task} touches no repository: tick at least one of the Repositories in scope, {inScope}.";
    }
}
