using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Overseer.GitHub;
using Overseer.Persistence;
using Overseer.Repositories;

namespace Overseer.Intents;

/// <summary>Owns the Intents' state: everything else asks it to capture or change an Intent and never sets state directly.</summary>
public sealed class IntentLifecycle(IDbContextFactory<OverseerDbContext> databases, TimeProvider time)
{
    /// <summary>Captures what the user wrote as a new Draft, with the Configured Repositories they selected in scope.</summary>
    /// <param name="selection">Selecting none is fine: only starting an Intent needs a Repository in scope.</param>
    public async Task<IntentCapturing> CaptureAsync(string? editorText, RepositorySelection selection, CancellationToken cancellationToken)
    {
        var text = Intent.TextFromEditor(editorText);
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var configured = await ConfiguredRepositories.HoldThoseConfiguredAsync(database, selection.Repositories, cancellationToken);
        // Both at once: the page drops a removed repository, so a refusal that left it out would be the last the user hears of it.
        string?[] problems = [text == "" ? "An Intent needs text: write what you want changed." : null, selection.RemovedNotice(configured)];
        if (problems.OfType<string>().ToList() is { Count: > 0 } refusal)
        {
            return new IntentCapturing.Refused(string.Join(" ", refusal));
        }

        database.Intents.Add(new IntentRecord
        {
            Id = Guid.NewGuid(),
            Text = text,
            State = IntentState.Draft,
            CreatedAt = time.GetUtcNow(),
            RepositoriesInScope = [.. configured.Select(repository => new RepositoryInScopeRecord { FullName = repository.ToString() })],
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new IntentCapturing.Captured();
    }

    /// <summary>The Backlog: every open Intent, newest first.</summary>
    public async Task<IReadOnlyList<Intent>> ListBacklogAsync(CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return await ListOpenAsync(database.Intents, cancellationToken);
    }

    /// <summary>
    /// Says which open Intents have <paramref name="repository"/> in scope, in any case, by their first lines, newest first; <c>null</c>
    /// when none has.
    /// </summary>
    internal static async Task<string?> DescribeOpenWithInScopeAsync(OverseerDbContext database, RepositoryName repository, CancellationToken cancellationToken)
    {
        var fullName = repository.ToString();
        var open = await ListOpenAsync(database.Intents.Where(intent => intent.RepositoriesInScope.Any(inScope => inScope.FullName == fullName)), cancellationToken);
        return open.Count == 0
            ? null
            : Prose.Agreeing([.. open.Select(intent => $"\"{intent.FirstLine}\"")],
                one: firstLine => $"the open Intent {firstLine} has it in scope",
                many: firstLines => $"the open Intents {firstLines} have it in scope");
    }

    private static async Task<IReadOnlyList<Intent>> ListOpenAsync(IQueryable<IntentRecord> intents, CancellationToken cancellationToken)
    {
        var stored = await intents
            // Names Done and Cancelled rather than the open states, so that a state added later counts as open.
            .Where(intent => intent.State != IntentState.Done && intent.State != IntentState.Cancelled)
            .OrderByDescending(intent => intent.CreatedAt)
            .Select(intent => new { intent.Text, intent.State, Repositories = intent.RepositoriesInScope.Select(repository => repository.FullName).ToList() })
            .ToListAsync(cancellationToken);
        return [.. stored.Select(intent => new Intent(intent.Text, intent.State, ConfiguredRepositories.ParseSortedByName(intent.Repositories)))];
    }
}

/// <summary>
/// The Repositories in scope a user ticked on a form, as they came in. The form may have been opened before some of them were removed,
/// and a value that isn't a repository at all can't be configured either.
/// </summary>
public sealed class RepositorySelection
{
    private readonly IReadOnlyList<string> _notRepositories;

    public RepositorySelection(IEnumerable<string> submitted)
    {
        var read = submitted.Select(value => (Value: value, Repository: RepositoryName.TryParse(value, out var repository) ? repository : null)).ToList();
        Repositories = [.. read.Select(item => item.Repository).OfType<RepositoryName>()];
        _notRepositories = [.. read.Where(item => item.Repository is null).Select(item => item.Value).Distinct()];
    }

    internal IReadOnlyList<RepositoryName> Repositories { get; }

    /// <summary>Whether <paramref name="repository"/> is ticked, in any case.</summary>
    public bool Includes(RepositoryName repository) => Repositories.Contains(repository, RepositoryName.SameRepository);

    /// <summary>
    /// What to tell the user when something ticked isn't among <paramref name="configured"/>, or <c>null</c> when all of it is. Going on
    /// without it would change what they selected behind their back, so they get to look at it again.
    /// </summary>
    public string? RemovedNotice(IEnumerable<RepositoryName> configured)
    {
        List<string> removed = [.. Repositories.Except(configured, RepositoryName.SameRepository).Select(repository => repository.ToString()), .. _notRepositories];
        return removed.Count == 0
            ? null
            : $"{ConfiguredRepositories.NoLongerConfigured(removed)}. Check the Repositories in scope and save again.";
    }
}

/// <summary>What capturing an Intent did.</summary>
public abstract record IntentCapturing
{
    private IntentCapturing()
    {
    }

    /// <summary>The Intent is now a Draft in the Backlog.</summary>
    public sealed record Captured : IntentCapturing;

    /// <summary>Nothing was saved; <paramref name="Error"/> says why.</summary>
    public sealed record Refused(string Error) : IntentCapturing;
}

/// <summary>An Intent, as the human described it, how far it has come, and the Configured Repositories it may change.</summary>
public sealed partial record Intent(string Text, IntentState State, IReadOnlyList<RepositoryName> RepositoriesInScope)
{
    /// <summary>
    /// An Intent's text as Overseer keeps it, from what the user wrote: LF line breaks whatever the browser sent, and empty when
    /// there is nothing but whitespace.
    /// </summary>
    public static string TextFromEditor(string? text) => string.IsNullOrWhiteSpace(text) ? "" : text.ReplaceLineEndings("\n");

    /// <summary>
    /// What the Backlog lists the Intent by, since an Intent has no title: the first line that says anything, without Markdown's
    /// heading markers. Text with nothing but heading markers shows its first line as it is.
    /// </summary>
    public string FirstLine =>
        Text.Split('\n').Select(line => AtxHeading().Replace(line, "${heading}").Trim()).FirstOrDefault(line => line != "")
        ?? Text.Trim().Split('\n', 2)[0].Trim();

    // As CommonMark defines an ATX heading: "# C#" is a heading about C#, "## ##" is an empty heading, and "#hashtag" isn't a heading.
    [GeneratedRegex(@"^ {0,3}#{1,6}(?:[ \t]+(?<heading>.*?))??(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex AtxHeading();
}

/// <summary>Where an Intent is in its lifecycle.</summary>
public enum IntentState
{
    Draft,

    // Nothing reaches these two yet. They exist because an Intent is open in every other state.
    Done,
    Cancelled,
}

/// <summary>An Intent, as PostgreSQL holds it.</summary>
public sealed class IntentRecord
{
    public required Guid Id { get; init; }

    /// <summary>The Markdown the human wrote, with LF line breaks.</summary>
    public required string Text { get; init; }

    public required IntentState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public List<RepositoryInScopeRecord> RepositoriesInScope { get; init; } = [];
}

/// <summary>
/// A Repository in scope of an Intent, as PostgreSQL holds it. It names the repository rather than referring to the Configured
/// Repository: an Intent that is Done or Cancelled keeps its history when the repository is removed later.
/// </summary>
public sealed class RepositoryInScopeRecord
{
    public Guid IntentId { get; init; }

    /// <summary><c>owner/name</c>, in the case it was configured in.</summary>
    public required string FullName { get; init; }
}
