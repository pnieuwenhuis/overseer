using System.Text.Json;
using System.Text.RegularExpressions;
using Overseer.GitHub;

namespace Overseer.Implementation;

/// <summary>
/// The Compose project names under which each repository's services start. Compose names a project after the <c>name</c> in its
/// file, or else after the directory the file is in, among other things, and starting a project replaces the services it has: two
/// repositories whose files give the same name would replace each other's. Those get a name of their own; the others keep the
/// name Compose gives them, so that <c>docker compose</c> run in such a repository finds its services.
/// </summary>
internal static partial class ComposeProjects
{
    /// <summary>The project name in what <c>docker compose config --format json</c> printed; <c>null</c> when there is none.</summary>
    public static string? NameIn(string config)
    {
        try
        {
            using var document = JsonDocument.Parse(config);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && name.GetString() is { Length: > 0 } found
                    ? found
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The name to start each repository's services under instead of Compose's, for those whose name another has too: Compose's
    /// name followed by the repository's, and by a number should even that be taken.
    /// </summary>
    /// <param name="composeNames">Each repository with the name Compose gives its project.</param>
    public static IReadOnlyDictionary<RepositoryName, string> Renamed(IReadOnlyList<(RepositoryName Repository, string Name)> composeNames)
    {
        var shared = composeNames.GroupBy(project => project.Name).Where(same => same.Count() > 1).Select(same => same.Key).ToHashSet();
        var taken = composeNames.Select(project => project.Name).ToHashSet();
        var renamed = new Dictionary<RepositoryName, string>();
        foreach (var (repository, name) in composeNames.Where(project => shared.Contains(project.Name)))
        {
            // A project name holds lowercase letters, digits, hyphens and underscores only.
            var wanted = NotInAProjectName().Replace($"{name}-{repository.Owner}-{repository.Name}".ToLowerInvariant(), "-");
            var own = wanted;
            for (var number = 2; !taken.Add(own); number++)
            {
                own = $"{wanted}-{number}";
            }
            renamed.Add(repository, own);
        }
        return renamed;
    }

    [GeneratedRegex("[^a-z0-9_-]")]
    private static partial Regex NotInAProjectName();
}
