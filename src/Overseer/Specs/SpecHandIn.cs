using System.Text.Json;
using Overseer.GitHub;
using Overseer.Sandboxes;

namespace Overseer.Specs;

/// <summary>
/// A Spec as the Analyst hands it in: a JSON file with its prose sections in Markdown and its ordered Tasks, each touching
/// Repositories in scope of the Intent. The Analyst's instructions describe the format.
/// </summary>
/// <param name="repositoriesInScope">The Intent's, which its Tasks may touch and no others.</param>
internal sealed class SpecHandIn(IReadOnlyList<RepositoryName> repositoriesInScope) : HandIn<Spec>
{
    /// <summary>In the home of the Sandbox's user, outside the repositories, next to the Round's.</summary>
    public const string FilePath = "/home/overseer/.overseer/spec.json";

    public override string Path => FilePath;

    /// <summary>The roles a Task can have.</summary>
    private static readonly AgentRole[] TaskRoles = [AgentRole.Designer, AgentRole.Developer];

    /// <summary>The prose sections it must have, by property.</summary>
    private static readonly string[] RequiredSections = ["summary", "approach", "acceptanceCriteria", "dependencies"];

    /// <summary>The prose sections it has only when they apply.</summary>
    private static readonly string[] OptionalSections = ["apiEndpointChanges", "dataModelChanges", "uiChanges"];

    /// <summary>An example of the format, for the Analyst's instructions.</summary>
    public const string Example = """
        {
          "summary": "The orders page gets an **Export** button that downloads every order as CSV.",
          "approach": "A new endpoint streams the orders as CSV; the orders page links to it.",
          "decisions": [
            { "decision": "The export is CSV, not Excel.", "analystsChoice": false },
            { "decision": "It holds every order, with no date filter.", "analystsChoice": true }
          ],
          "acceptanceCriteria": "- The orders page shows an Export button\n- The file it downloads opens in a spreadsheet",
          "dependencies": "None.",
          "apiEndpointChanges": "`GET /orders/export` returns `text/csv`.",
          "uiChanges": "An Export button above the orders table.",
          "tasks": [
            {
              "title": "Add the CSV export endpoint",
              "description": "Add `GET /orders/export`, streaming every order as CSV, with tests.",
              "role": "Developer",
              "repositories": ["acme/web"]
            }
          ]
        }
        """;

    protected override HandedIn<Spec> Validate(string content) => FromJson(content, root =>
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new HandedIn<Spec>.Invalid(["The file must hold a JSON object with the Spec's sections and its \"tasks\"."]);
        }

        var errors = new List<string>();
        var sections = new Dictionary<string, string?>();
        foreach (var section in RequiredSections)
        {
            sections[section] = TextOf(root, section);
            if (sections[section] is null)
            {
                errors.Add($"The Spec has no \"{section}\": it must be Markdown text that isn't empty.");
            }
        }
        foreach (var section in OptionalSections)
        {
            sections[section] = TextOf(root, section);
            if (sections[section] is null && root.TryGetProperty(section, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                errors.Add($"The Spec's \"{section}\" must be Markdown text, or left out when there are none.");
            }
        }
        var decisions = ReadDecisions(root, errors);
        var tasks = ReadTasks(root, errors);

        return errors.Count > 0
            ? new HandedIn<Spec>.Invalid(errors)
            : new HandedIn<Spec>.Valid(new Spec(
                sections["summary"]!,
                sections["approach"]!,
                decisions,
                sections["acceptanceCriteria"]!,
                sections["dependencies"]!,
                sections["apiEndpointChanges"],
                sections["dataModelChanges"],
                sections["uiChanges"],
                tasks));
    });

    private static List<SpecDecision> ReadDecisions(JsonElement root, List<string> errors)
    {
        var read = new List<SpecDecision>();
        if (!root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array || decisions.GetArrayLength() == 0)
        {
            errors.Add("The Spec has no \"decisions\": it must be a list of at least one decision.");
            return read;
        }
        var number = 0;
        foreach (var decision in decisions.EnumerateArray())
        {
            number++;
            if (decision.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"Decision {number} must be an object with \"decision\" and \"analystsChoice\".");
                continue;
            }
            var text = TextOf(decision, "decision");
            if (text is null)
            {
                errors.Add($"Decision {number} has no \"decision\": it must be Markdown text that isn't empty.");
            }
            bool? analystsChoice = decision.TryGetProperty("analystsChoice", out var choice) && choice.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? choice.GetBoolean()
                : null;
            if (analystsChoice is null)
            {
                errors.Add($"Decision {number} has no \"analystsChoice\": it must be true when the human left the decision to you, and false otherwise.");
            }
            if (text is not null && analystsChoice is { } isAnalystsChoice)
            {
                read.Add(new SpecDecision(text, isAnalystsChoice));
            }
        }
        return read;
    }

    private List<SpecTask> ReadTasks(JsonElement root, List<string> errors)
    {
        var read = new List<SpecTask>();
        if (!root.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array || tasks.GetArrayLength() == 0)
        {
            errors.Add("The Spec has no Tasks: \"tasks\" must be a list of at least one Task.");
            return read;
        }
        var number = 0;
        foreach (var task in tasks.EnumerateArray())
        {
            number++;
            if (task.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"Task {number} must be an object with \"title\", \"description\", \"role\" and \"repositories\".");
                continue;
            }
            var before = errors.Count;
            var title = TextOf(task, "title");
            if (title is null)
            {
                errors.Add($"Task {number} has no \"title\": it must be text that isn't empty.");
            }
            var description = TextOf(task, "description");
            if (description is null)
            {
                errors.Add($"Task {number} has no \"description\": it must be Markdown text that isn't empty.");
            }
            var role = ReadRole(task, number, errors);
            var repositories = ReadRepositories(task, number, errors);
            if (errors.Count == before)
            {
                read.Add(new SpecTask(number, title!, description!, role!.Value, repositories));
            }
        }
        return read;
    }

    private static AgentRole? ReadRole(JsonElement task, int number, List<string> errors)
    {
        var allowed = string.Join(" or ", TaskRoles.Select(role => $"\"{role}\""));
        if (TextOf(task, "role") is not { } role)
        {
            errors.Add($"Task {number} has no \"role\": it must be {allowed}.");
            return null;
        }
        if (TaskRoles.Cast<AgentRole?>().FirstOrDefault(taskRole => taskRole.ToString() == role) is { } found)
        {
            return found;
        }
        errors.Add($"Task {number} has the role \"{role}\": it must be {allowed}.");
        return null;
    }

    private List<RepositoryName> ReadRepositories(JsonElement task, int number, List<string> errors)
    {
        var inScope = string.Join(", ", repositoriesInScope);
        if (!task.TryGetProperty("repositories", out var repositories) || repositories.ValueKind != JsonValueKind.Array || repositories.GetArrayLength() == 0)
        {
            errors.Add($"Task {number} touches no repository: \"repositories\" must list at least one of the Repositories in scope, {inScope}.");
            return [];
        }
        var read = new List<RepositoryName>();
        foreach (var repository in repositories.EnumerateArray())
        {
            var named = repository.ValueKind == JsonValueKind.String ? repository.GetString()!.Trim() : repository.GetRawText();
            if (repository.ValueKind == JsonValueKind.String && RepositoryName.TryParse(named, out var name)
                && repositoriesInScope.FirstOrDefault(inScopeRepository => RepositoryName.SameRepository.Equals(inScopeRepository, name)) is { } match)
            {
                if (!read.Contains(match))
                {
                    read.Add(match);
                }
            }
            else
            {
                errors.Add($"Task {number} touches {named}, which isn't a Repository in scope: those are {inScope}.");
            }
        }
        return read;
    }
}
