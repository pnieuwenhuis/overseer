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
    /// <param name="repositoriesInScope">
    /// As <c>owner/name</c> in any case. Selecting none is fine: only starting an Intent needs a Repository in scope.
    /// </param>
    public async Task<IntentCapturing> CaptureAsync(string? editorText, IReadOnlyCollection<string> repositoriesInScope, CancellationToken cancellationToken)
    {
        var text = Intent.TextFromEditor(editorText);
        if (text == "")
        {
            return new IntentCapturing.Refused("An Intent needs text: write what you want changed.");
        }

        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var configured = await ConfiguredRepositories.HoldAsync(database, repositoriesInScope, cancellationToken);
        var removed = repositoriesInScope.Except(configured, StringComparer.OrdinalIgnoreCase).ToList();
        if (removed.Count > 0)
        {
            // Saving without them would change what the user selected behind their back, so they get to look at it again.
            return new IntentCapturing.Refused(
                $"{Prose.List(removed)} {(removed.Count == 1 ? "is no longer a Configured Repository" : "are no longer Configured Repositories")}. "
                + "Check the Repositories in scope and save again.");
        }

        database.Intents.Add(new IntentRecord
        {
            Id = Guid.NewGuid(),
            Text = text,
            State = IntentState.Draft,
            CreatedAt = time.GetUtcNow(),
            RepositoriesInScope = [.. configured.Select(repository => new RepositoryInScopeRecord { FullName = repository })],
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

    /// <summary>The open Intents, newest first, that have <paramref name="repository"/> in scope, in any case.</summary>
    internal static Task<IReadOnlyList<Intent>> ListOpenWithInScopeAsync(OverseerDbContext database, string repository, CancellationToken cancellationToken) =>
        ListOpenAsync(database.Intents.Where(intent => intent.RepositoriesInScope.Any(inScope => inScope.FullName == repository)), cancellationToken);

    private static async Task<IReadOnlyList<Intent>> ListOpenAsync(IQueryable<IntentRecord> intents, CancellationToken cancellationToken)
    {
        var stored = await intents
            // Named by what closes an Intent, so that a state added later counts as open.
            .Where(intent => intent.State != IntentState.Done && intent.State != IntentState.Cancelled)
            .OrderByDescending(intent => intent.CreatedAt)
            .Select(intent => new { intent.Text, intent.State, Repositories = intent.RepositoriesInScope.Select(repository => repository.FullName).ToList() })
            .ToListAsync(cancellationToken);
        return [.. stored.Select(intent => new Intent(intent.Text, intent.State, ConfiguredRepositories.ByName(intent.Repositories)))];
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

    // Nothing reaches these two yet. They exist because they are what closes an Intent: every other state is open.
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
