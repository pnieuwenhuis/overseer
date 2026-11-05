using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Overseer.GitHub;
using Overseer.Persistence;
using Overseer.Repositories;
using Overseer.Settings;

namespace Overseer.Intents;

/// <summary>Owns the Intents' state: everything else asks it to capture or change an Intent and never sets state directly.</summary>
public sealed class IntentLifecycle(IDbContextFactory<OverseerDbContext> databases, SettingsStore settings, TimeProvider time)
{
    /// <summary>The states of an Active Intent, each occupying a slot; what counts towards the maximum is defined here and nowhere else.</summary>
    private static readonly IntentState[] ActiveStates = [IntentState.Refining, IntentState.Implementing, IntentState.InReview];

    /// <summary>The PostgreSQL advisory lock for the slots, in this database; the number means nothing, and only has to stay the only one used.</summary>
    private const long ActiveIntentSlotsLock = 7_234_001;

    /// <summary>Captures what the user wrote as a new Draft, with the Configured Repositories they selected in scope.</summary>
    /// <param name="selection">Selecting none is fine: only starting an Intent needs a Repository in scope.</param>
    public async Task<IntentChange> CaptureAsync(string? editorText, RepositorySelection selection, CancellationToken cancellationToken)
    {
        var text = Intent.TextFromEditor(editorText);
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var configured = await ConfiguredRepositories.HoldThoseConfiguredAsync(database, selection.Repositories, cancellationToken);
        if (WhyNotSavable(text, selection, configured) is { } refusal)
        {
            return new IntentChange.Refused(refusal);
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
        return new IntentChange.Made();
    }

    /// <summary>
    /// Replaces a Draft's text and Repositories in scope with what the user entered. Anything but a Draft is refused, as the Intent is at
    /// this moment rather than as the user's page showed it.
    /// </summary>
    /// <param name="selection">Selecting none is fine, as when capturing.</param>
    public async Task<IntentChange> EditAsync(Guid id, string? editorText, RepositorySelection selection, CancellationToken cancellationToken)
    {
        var text = Intent.TextFromEditor(editorText);
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        if (WhyNotEditable(await LockStateAsync(database, id, cancellationToken)) is { } notEditable)
        {
            return new IntentChange.Refused(notEditable);
        }

        var configured = await ConfiguredRepositories.HoldThoseConfiguredAsync(database, selection.Repositories, cancellationToken);
        if (WhyNotSavable(text, selection, configured) is { } refusal)
        {
            return new IntentChange.Refused(refusal);
        }

        await database.Intents.Where(intent => intent.Id == id)
            .ExecuteUpdateAsync(intent => intent.SetProperty(record => record.Text, text), cancellationToken);
        var inScope = database.Set<RepositoryInScopeRecord>();
        await inScope.Where(repository => repository.IntentId == id).ExecuteDeleteAsync(cancellationToken);
        inScope.AddRange(configured.Select(repository => new RepositoryInScopeRecord { IntentId = id, FullName = repository.ToString() }));
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new IntentChange.Made();
    }

    /// <summary>Why an Intent in <paramref name="state"/> can't be edited, or <c>null</c> when it can.</summary>
    /// <param name="state">The Intent's state; <c>null</c> when there is no such Intent.</param>
    public static string? WhyNotEditable(IntentState? state) => WhyNotADraft(state, other => $"This Intent is {other}, and only a Draft can be edited.");

    /// <summary>
    /// Deletes a Draft outright, with its Repositories in scope: it is gone, not Cancelled. Anything but a Draft is refused, as the Intent
    /// is at this moment rather than as the user's page showed it.
    /// </summary>
    public async Task<IntentChange> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        if (WhyNotDeletable(await LockStateAsync(database, id, cancellationToken)) is { } notDeletable)
        {
            return new IntentChange.Refused(notDeletable);
        }

        // The Configured Repositories aren't held, as capturing and editing do: deleting only ever lets a removal through. A removal
        // that still sees this Intent is refused, as it would have been a moment earlier.
        await database.Intents.Where(intent => intent.Id == id).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new IntentChange.Made();
    }

    /// <summary>Why an Intent in <paramref name="state"/> can't be deleted, or <c>null</c> when it can.</summary>
    /// <param name="state">The Intent's state; <c>null</c> when there is no such Intent.</param>
    public static string? WhyNotDeletable(IntentState? state) => WhyNotADraft(state, other => $"This Intent is {other}, and only a Draft can be deleted.");

    /// <summary>
    /// Starts work on a Draft: it moves to Refining, and its text and Repositories in scope are locked from then on. Whether it may is
    /// decided as the Intent is at this moment rather than as the user's page showed it.
    /// </summary>
    public async Task<IntentChange> StartAsync(Guid id, CancellationToken cancellationToken)
    {
        // Ahead of the transaction, so that no request holds a connection and its locks while it waits for another connection.
        var limits = await settings.GetOperatingLimitsAsync(cancellationToken);
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var state = await LockStateAsync(database, id, cancellationToken);
        await HoldActiveIntentSlotsAsync(database, cancellationToken);
        if (await WhyNotStartableAsync(database, id, state, limits.MaxActiveIntents, cancellationToken) is { } notStartable)
        {
            return new IntentChange.Refused(notStartable);
        }

        await database.Intents.Where(intent => intent.Id == id)
            .ExecuteUpdateAsync(intent => intent.SetProperty(record => record.State, IntentState.Refining), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new IntentChange.Made();
    }

    /// <summary>Why the Intent with this id can't be started at the moment, with what a refused Start says, or <c>null</c> when it can.</summary>
    public async Task<string?> WhyNotStartableAsync(Guid id, CancellationToken cancellationToken)
    {
        var limits = await settings.GetOperatingLimitsAsync(cancellationToken);
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var state = await database.Intents.Where(intent => intent.Id == id).Select(intent => (IntentState?)intent.State).SingleOrDefaultAsync(cancellationToken);
        return await WhyNotStartableAsync(database, id, state, limits.MaxActiveIntents, cancellationToken);
    }

    /// <summary>The Backlog: every open Intent, newest first.</summary>
    public async Task<IReadOnlyList<Intent>> ListBacklogAsync(CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return await ListOpenAsync(database.Intents, cancellationToken);
    }

    /// <summary>The Intent with this id, in whatever state; <c>null</c> when there is none, as after it was deleted.</summary>
    public async Task<Intent?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return (await ReadAsync(database.Intents.Where(intent => intent.Id == id), cancellationToken)).SingleOrDefault();
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

    /// <summary>Why this text and selection can't be saved on an Intent, or <c>null</c> when they can.</summary>
    private static string? WhyNotSavable(string text, RepositorySelection selection, IReadOnlyList<RepositoryName> configured)
    {
        // Both at once: the page drops a removed repository, so a refusal that left it out would be the last the user hears of it.
        string?[] problems = [text == "" ? "An Intent needs text: write what you want changed." : null, selection.RemovedNotice(configured)];
        return problems.OfType<string>().ToList() is { Count: > 0 } found ? string.Join(" ", found) : null;
    }

    private static async Task<string?> WhyNotStartableAsync(
        OverseerDbContext database, Guid id, IntentState? state, int maxActiveIntents, CancellationToken cancellationToken)
    {
        if (WhyNotADraft(state, other => $"This Intent is {other}, and only a Draft can be started.") is { } notADraft)
        {
            return notADraft;
        }

        var hasRepositoryInScope = await database.Set<RepositoryInScopeRecord>().AnyAsync(repository => repository.IntentId == id, cancellationToken);
        // The maximum may have been lowered below the number of Active Intents: they stay, and Start waits until there is a free slot.
        var active = await ReadAsync(database.Intents.Where(intent => ActiveStates.Contains(intent.State)).OrderByDescending(intent => intent.CreatedAt), cancellationToken);

        // Both at once, so that dealing with one doesn't lead straight to the other.
        string?[] problems =
        [
            hasRepositoryInScope ? null : "Start needs at least one Repository in scope: edit this Draft to select one.",
            active.Count < maxActiveIntents ? null : $"All Active Intent slots are taken, by {Prose.List([.. active.Select(intent => $"\"{intent.FirstLine}\"")])}. You can raise the maximum in Settings.",
        ];
        return problems.OfType<string>().ToList() is { Count: > 0 } found ? string.Join(" ", found) : null;
    }

    // Names Draft rather than the states it refuses, so that a state added later is refused, open or not.
    private static string? WhyNotADraft(IntentState? state, Func<IntentState, string> refusal) => state switch
    {
        null => "There is no such Intent: it may have been deleted.",
        IntentState.Draft => null,
        { } other => refusal(other),
    };

    /// <summary>
    /// The state of the Intent with this id, <c>null</c> when there is none. It keeps that state until <paramref name="database"/>'s
    /// transaction ends: a change under way is waited for and then seen, and one that comes later waits for this transaction.
    /// </summary>
    private static async Task<IntentState?> LockStateAsync(OverseerDbContext database, Guid id, CancellationToken cancellationToken)
    {
        var locked = await database.Intents.FromSql($"""SELECT * FROM "Intents" WHERE "Id" = {id} FOR UPDATE""").AsNoTracking().ToListAsync(cancellationToken);
        return locked.SingleOrDefault()?.State;
    }

    /// <summary>
    /// Keeps every other transition into an Active state waiting until <paramref name="database"/>'s transaction ends, so that counting
    /// the Active Intents and taking a slot are one step: under READ COMMITTED a count doesn't see the slot another transaction has taken
    /// but not committed, and there is no row to lock for a slot that is free. What is counted after this sees whatever the previous
    /// holder committed. Take it after the Intent's row, always in that order: a transaction holding this never waits for an Intent's
    /// row, so two Starts can't deadlock.
    /// </summary>
    private static Task HoldActiveIntentSlotsAsync(OverseerDbContext database, CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ActiveIntentSlotsLock})", cancellationToken);

    private static Task<IReadOnlyList<Intent>> ListOpenAsync(IQueryable<IntentRecord> intents, CancellationToken cancellationToken) =>
        ReadAsync(intents
            // Names Done and Cancelled rather than the open states, so that a state added later counts as open.
            .Where(intent => intent.State != IntentState.Done && intent.State != IntentState.Cancelled)
            .OrderByDescending(intent => intent.CreatedAt), cancellationToken);

    private static async Task<IReadOnlyList<Intent>> ReadAsync(IQueryable<IntentRecord> intents, CancellationToken cancellationToken)
    {
        var stored = await intents
            .Select(intent => new { intent.Id, intent.Text, intent.State, Repositories = intent.RepositoriesInScope.Select(repository => repository.FullName).ToList() })
            .ToListAsync(cancellationToken);
        return [.. stored.Select(intent => new Intent(intent.Id, intent.Text, intent.State, ConfiguredRepositories.ParseSortedByName(intent.Repositories)))];
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

    /// <summary>A selection of exactly these repositories, as an Intent has them in scope.</summary>
    public RepositorySelection(IEnumerable<RepositoryName> repositories)
    {
        Repositories = [.. repositories];
        _notRepositories = [];
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

/// <summary>What capturing, editing, deleting or starting an Intent did.</summary>
public abstract record IntentChange
{
    private IntentChange()
    {
    }

    /// <summary>The change is made.</summary>
    public sealed record Made : IntentChange;

    /// <summary>Nothing changed; <paramref name="Error"/> says why.</summary>
    public sealed record Refused(string Error) : IntentChange;
}

/// <summary>An Intent, as the human described it, how far it has come, and the Configured Repositories it may change.</summary>
public sealed partial record Intent(Guid Id, string Text, IntentState State, IReadOnlyList<RepositoryName> RepositoriesInScope)
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

    /// <summary>The Repositories in scope, in a line of their own.</summary>
    public string RepositoriesInScopeLine =>
        RepositoriesInScope.Count == 0 ? "No Repositories in scope" : $"Repositories in scope: {string.Join(", ", RepositoriesInScope)}";

    // As CommonMark defines an ATX heading: "# C#" is a heading about C#, "## ##" is an empty heading, and "#hashtag" isn't a heading.
    [GeneratedRegex(@"^ {0,3}#{1,6}(?:[ \t]+(?<heading>.*?))??(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex AtxHeading();
}

/// <summary>Where an Intent is in its lifecycle.</summary>
public enum IntentState
{
    Draft,
    Refining,

    // Nothing reaches these yet, except by an update of the database in tests. The transitions to and from them come with their triggers.
    AwaitingSpecApproval,
    Approved,
    Implementing,
    InReview,
    Paused,
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
