using Overseer.GitHub;
using Overseer.Repositories;
using Overseer.Sandboxes;

namespace Overseer.Specs;

/// <summary>
/// What a Spec says: prose Markdown sections, which the Reviewer checks the work against, and the ordered Tasks the Task Runner
/// carries out.
/// </summary>
/// <param name="ApiEndpointChanges">Markdown; <c>null</c> when the Intent changes no API endpoint.</param>
/// <param name="DataModelChanges">Markdown; <c>null</c> when the Intent changes no data model.</param>
/// <param name="UiChanges">Markdown; <c>null</c> when the Intent changes no UI.</param>
public sealed record Spec(
    string Summary,
    string Approach,
    IReadOnlyList<SpecDecision> Decisions,
    string AcceptanceCriteria,
    string Dependencies,
    string? ApiEndpointChanges,
    string? DataModelChanges,
    string? UiChanges,
    IReadOnlyList<SpecTask> Tasks);

/// <summary>A decision in a Spec, in Markdown, marked when it is an Analyst's Choice: one the human left to the Analyst.</summary>
public sealed record SpecDecision(string Text, bool IsAnalystsChoice);

/// <summary>A Task of a Spec, numbered in the order the Tasks are carried out, from 1.</summary>
/// <param name="Description">Markdown.</param>
/// <param name="Role">Designer or Developer.</param>
/// <param name="Repositories">The Repositories in scope it touches, at least one, as the Intent has them in scope.</param>
public sealed record SpecTask(int Number, string Title, string Description, AgentRole Role, IReadOnlyList<RepositoryName> Repositories)
{
    /// <summary>
    /// What a Task that creates a missing Repository Manifest looks like, so that Overseer recognizes it, for "the Spec must have ...".
    /// </summary>
    internal const string CreatingManifest =
        $"a Task that touches it and creates its `{RepositoryManifest.Path}`, naming that file in its title or description";

    /// <summary>
    /// Whether a Task creates the Repository Manifest of <paramref name="repository"/>, as far as Overseer can tell: it touches the
    /// repository and names the manifest's file in its title or description. Also for a Task handed in with errors, from what
    /// could be read of it.
    /// </summary>
    /// <param name="repositories">The Repositories in scope the Task touches.</param>
    /// <param name="title"><c>null</c> when it can't be read.</param>
    /// <param name="description"><c>null</c> when it can't be read.</param>
    internal static bool CreatesManifestOf(
        RepositoryName repository, IReadOnlyList<RepositoryName> repositories, string? title, string? description) =>
        repositories.Contains(repository) && (RepositoryManifest.IsNamedIn(title) || RepositoryManifest.IsNamedIn(description));
}

/// <summary>A version of an Intent's Spec, numbered from 1.</summary>
public sealed record SpecVersion(int Number, Spec Spec);

/// <summary>A version of an Intent's Spec, as PostgreSQL holds it.</summary>
public sealed class SpecVersionRecord
{
    public required Guid IntentId { get; init; }
    public required int Number { get; init; }
    public required DateTimeOffset WrittenAt { get; init; }
    public required string Summary { get; init; }
    public required string Approach { get; init; }
    public required string AcceptanceCriteria { get; init; }
    public required string Dependencies { get; init; }
    public string? ApiEndpointChanges { get; init; }
    public string? DataModelChanges { get; init; }
    public string? UiChanges { get; init; }
    public List<SpecDecisionRecord> Decisions { get; init; } = [];
    public List<SpecTaskRecord> Tasks { get; init; } = [];
}

/// <summary>A decision of a Spec version, as PostgreSQL holds it, numbered in its order from 1.</summary>
public sealed class SpecDecisionRecord
{
    public required Guid IntentId { get; init; }
    public required int SpecVersion { get; init; }
    public required int Number { get; init; }
    public required string Text { get; init; }
    public required bool IsAnalystsChoice { get; init; }
}

/// <summary>A Task of a Spec version, as PostgreSQL holds it, numbered in its order from 1.</summary>
public sealed class SpecTaskRecord
{
    public required Guid IntentId { get; init; }
    public required int SpecVersion { get; init; }
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required AgentRole Role { get; init; }

    /// <summary><c>owner/name</c> of each Repository in scope it touches, as the Intent has it in scope.</summary>
    public required List<string> Repositories { get; init; }
}
