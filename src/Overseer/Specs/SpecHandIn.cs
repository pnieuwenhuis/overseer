using System.Text.Json;
using Overseer.GitHub;
using Overseer.Sandboxes;

namespace Overseer.Specs;

/// <summary>
/// A Spec as the Analyst hands it in: a JSON file with its prose sections in Markdown and its ordered Tasks, each touching
/// Repositories in scope of the Intent. The Analyst's instructions describe the format.
/// </summary>
/// <param name="repositoriesInScope">The Intent's, which its Tasks may touch and no others.</param>
/// <param name="lackingManifest">
/// The Repositories in scope known to have no Repository Manifest, for each of which the Spec must have
/// <see cref="SpecTask.CreatingManifest"/>.
/// </param>
internal sealed class SpecHandIn(IReadOnlyList<RepositoryName> repositoriesInScope, IReadOnlyList<RepositoryName> lackingManifest) : HandIn<Spec>
{
    /// <summary>In the home of the Sandbox's user, outside the repositories, next to the Round's.</summary>
    public const string FilePath = "/home/overseer/.overseer/spec.json";

    public override string Path => FilePath;

    /// <summary>The roles a Task can have.</summary>
    private static readonly AgentRole[] TaskRoles = [AgentRole.Designer, AgentRole.Developer];

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
        var summary = RequiredSection(root, "summary", errors);
        var approach = RequiredSection(root, "approach", errors);
        var acceptanceCriteria = RequiredSection(root, "acceptanceCriteria", errors);
        var dependencies = RequiredSection(root, "dependencies", errors);
        var apiEndpointChanges = OptionalSection(root, "apiEndpointChanges", errors);
        var dataModelChanges = OptionalSection(root, "dataModelChanges", errors);
        var uiChanges = OptionalSection(root, "uiChanges", errors);
        var decisions = ReadDecisions(root, errors);
        var tasks = ReadTasks(root, errors, out var manifestsCreated);
        CheckManifestTasks(manifestsCreated, errors);

        return errors.Count > 0
            ? new HandedIn<Spec>.Invalid(errors)
            : new HandedIn<Spec>.Valid(new Spec(
                summary!, approach!, decisions, acceptanceCriteria!, dependencies!, apiEndpointChanges, dataModelChanges, uiChanges, tasks));
    });

    /// <summary>Adds an error for each Repository in scope that lacks a Repository Manifest and has no Task that creates it.</summary>
    /// <param name="manifestsCreated">The repositories whose manifest a Task creates, also one with errors of its own.</param>
    private void CheckManifestTasks(IReadOnlySet<RepositoryName> manifestsCreated, List<string> errors)
    {
        foreach (var repository in lackingManifest.Where(repository => !manifestsCreated.Contains(repository)))
        {
            errors.Add($"No Task creates the Repository Manifest of {repository}, which has none on its default branch: add {SpecTask.CreatingManifest}.");
        }
    }

    /// <summary>The Markdown of a prose section every Spec has; <c>null</c>, with the error, when it has none.</summary>
    private static string? RequiredSection(JsonElement root, string section, List<string> errors)
    {
        var text = TextOf(root, section);
        if (text is null)
        {
            errors.Add($"The Spec has no \"{section}\": it must be Markdown text that isn't empty.");
        }
        return text;
    }

    /// <summary>
    /// The Markdown of a prose section a Spec has only when it applies; <c>null</c> when it is left out, blank or <c>null</c>, and,
    /// with the error, when it isn't text.
    /// </summary>
    private static string? OptionalSection(JsonElement root, string section, List<string> errors)
    {
        var text = TextOf(root, section);
        if (text is null && root.TryGetProperty(section, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            errors.Add($"The Spec's \"{section}\" must be Markdown text, or left out when there are none.");
        }
        return text;
    }

    /// <summary>The Spec's decisions in order, which may be none, without those that are wrong, each adding its errors.</summary>
    private static List<SpecDecision> ReadDecisions(JsonElement root, List<string> errors)
    {
        if (ListOf(root, "decisions") is not { } decisions)
        {
            errors.Add("The Spec has no \"decisions\": it must be a list of decisions, empty when there are none.");
            return [];
        }
        return ReadEach(decisions, "Decision", ["decision", "analystsChoice"], errors, (decision, number) =>
        {
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
            return text is not null && analystsChoice is { } isAnalystsChoice ? new SpecDecision(text, isAnalystsChoice) : null;
        });
    }

    /// <summary>The Spec's Tasks in order, at least one, without those that are wrong, each adding its errors.</summary>
    /// <param name="manifestsCreated">
    /// The repositories whose Repository Manifest a Task creates, from what could be read of each, so that a Task left out for
    /// another error doesn't seem missing as well.
    /// </param>
    private List<SpecTask> ReadTasks(JsonElement root, List<string> errors, out HashSet<RepositoryName> manifestsCreated)
    {
        var created = manifestsCreated = [];
        if (ListOf(root, "tasks") is not { Count: > 0 } tasks)
        {
            errors.Add("The Spec has no Tasks: \"tasks\" must be a list of at least one Task.");
            return [];
        }
        return ReadEach(tasks, "Task", ["title", "description", "role", "repositories"], errors, (task, number) =>
        {
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
            created.UnionWith(repositories.Where(repository => SpecTask.CreatesManifestOf(repository, repositories, title, description)));
            return errors.Count == before ? new SpecTask(number, title!, description!, role!.Value, repositories) : null;
        });
    }

    /// <summary>The role of Task <paramref name="number"/>, Designer or Developer; <c>null</c>, with the error, when it has neither.</summary>
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

    /// <summary>
    /// The Repositories in scope Task <paramref name="number"/> touches, at least one, each once and as the Intent has it in scope;
    /// any other it names adds an error.
    /// </summary>
    private List<RepositoryName> ReadRepositories(JsonElement task, int number, List<string> errors)
    {
        var inScope = string.Join(", ", repositoriesInScope);
        if (ListOf(task, "repositories") is not { Count: > 0 } repositories)
        {
            errors.Add($"Task {number} touches no repository: \"repositories\" must list at least one of the Repositories in scope, {inScope}.");
            return [];
        }
        var read = new List<RepositoryName>();
        foreach (var repository in repositories)
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
