using Overseer.GitHub;
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
public sealed record SpecTask(int Number, string Title, string Description, AgentRole Role, IReadOnlyList<RepositoryName> Repositories);

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
