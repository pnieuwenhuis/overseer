using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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

    /// <summary>The states of an Intent that is closed: what isn't one of these is open.</summary>
    private static readonly IntentState[] ClosedStates = [IntentState.Done, IntentState.Cancelled];

    /// <summary>The PostgreSQL advisory lock for the slots, in this database; the number means nothing, and only has to stay the only one used.</summary>
    private const long ActiveIntentSlotsLock = 7_234_001;

    /// <summary>An Intent was captured, edited, deleted or changed state. Raised once the change is committed, so that a read then sees it.</summary>
    public event Action? Changed;

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

        var now = time.GetUtcNow();
        database.Intents.Add(new IntentRecord
        {
            Id = Guid.NewGuid(),
            Text = text,
            State = IntentState.Draft,
            CreatedAt = now,
            RepositoriesInScope = [.. configured.Select(repository => new RepositoryInScopeRecord { FullName = repository.ToString() })],
            Transitions = [new IntentTransitionRecord { From = null, To = IntentState.Draft, At = now }],
        });
        await database.SaveChangesAsync(cancellationToken);
        return await CommitAndAnnounceAsync(transaction, cancellationToken);
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
        return await CommitAndAnnounceAsync(transaction, cancellationToken);
    }

    /// <summary>Why an Intent in <paramref name="state"/> can't be edited, or <c>null</c> when it can.</summary>
    /// <param name="state">The Intent's state; <c>null</c> when there is no such Intent.</param>
    public static string? WhyNotEditable(IntentState? state) => WhyNotADraft(state, shownAs => $"This Intent is {shownAs}, and only a Draft can be edited.");

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
        return await CommitAndAnnounceAsync(transaction, cancellationToken);
    }

    /// <summary>Why an Intent in <paramref name="state"/> can't be deleted, or <c>null</c> when it can.</summary>
    /// <param name="state">The Intent's state; <c>null</c> when there is no such Intent.</param>
    public static string? WhyNotDeletable(IntentState? state) => WhyNotADraft(state, shownAs => $"This Intent is {shownAs}, and only a Draft can be deleted.");

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

        await TransitionAsync(database, id, IntentState.Draft, IntentState.Refining, cancellationToken);
        return await CommitAndAnnounceAsync(transaction, cancellationToken);
    }

    /// <summary>Why the Intent with this id can't be started at the moment, with what a refused Start says, or <c>null</c> when it can.</summary>
    public async Task<string?> WhyNotStartableAsync(Guid id, CancellationToken cancellationToken)
    {
        var limits = await settings.GetOperatingLimitsAsync(cancellationToken);
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var state = await database.Intents.Where(intent => intent.Id == id).Select(intent => (IntentState?)intent.State).SingleOrDefaultAsync(cancellationToken);
        return await WhyNotStartableAsync(database, id, state, limits.MaxActiveIntents, cancellationToken);
    }

    /// <summary>
    /// The light an Intent shows, or <c>null</c> when it has none. This is the one place a light is derived, and so far only green can be
    /// reached, by a Refining Intent. The tickets that introduce a condition add it here, ahead of the green below and reading whatever
    /// <see cref="Intent"/> then carries, each with its member of <see cref="IntentLight"/>, its colour and its label in
    /// <see cref="IntentWords"/>, so that the light doesn't come to rely on the colour. Checked in this order: first red, labelled Failed
    /// (a Failed Task, Sandbox creation failed, a merge failed), as a failure outweighs a question; then orange, labelled Needs Attention
    /// (an unanswered Round, a Spec awaiting approval, an agent's question, Review Comments awaiting triage). Implementing and InReview
    /// aren't settled by the green below: the tickets that make them reachable decide their light, as an Intent In Review can be up to the
    /// user rather than to agents.
    /// </summary>
    public static IntentLight? LightOf(Intent intent) =>
        // What is left is an Active Intent or a state in which nothing runs. AwaitingSpecApproval isn't Active, so it has no light until
        // its orange condition is added above.
        ActiveStates.Contains(intent.State) ? IntentLight.Green : null;

    /// <summary>The Intents the Backlog shows under this filter, newest first.</summary>
    public async Task<IReadOnlyList<Intent>> ListBacklogAsync(BacklogFilter filter, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return filter == BacklogFilter.Closed
            ? await ReadAsync(database.Intents.Where(intent => ClosedStates.Contains(intent.State)), cancellationToken)
            : await ListOpenAsync(database.Intents, cancellationToken);
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
            : Prose.Agreeing([.. open.Select(QuotedFirstLine)],
                one: firstLine => $"the open Intent {firstLine} has it in scope",
                many: firstLines => $"the open Intents {firstLines} have it in scope");
    }

    /// <summary>How every change to an Intent ends, so that none goes unannounced and a refused one, which never gets here, isn't announced.</summary>
    private async Task<IntentChange> CommitAndAnnounceAsync(IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken);
        Changed?.Invoke();
        return new IntentChange.Made();
    }

    /// <summary>
    /// Moves the Intent with this id, whose row <paramref name="database"/>'s transaction has locked, from one state to another, and
    /// records the transition with its time. Every change of state goes through here: the transitions are what Working Time is derived
    /// from, and what later tickets rely on, such as which state a Paused Intent resumes to.
    /// </summary>
    private async Task TransitionAsync(OverseerDbContext database, Guid id, IntentState from, IntentState to, CancellationToken cancellationToken)
    {
        await database.Intents.Where(intent => intent.Id == id)
            .ExecuteUpdateAsync(intent => intent.SetProperty(record => record.State, to), cancellationToken);
        database.Set<IntentTransitionRecord>().Add(new IntentTransitionRecord { IntentId = id, From = from, To = to, At = time.GetUtcNow() });
        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The Working Time of an Intent with these transitions, in the order they were made: the sum of the periods from a transition into
    /// an Active state to the next one, and the time since the last one while the Intent is still Active.
    /// </summary>
    private static WorkingTime WorkingTimeOf(IEnumerable<(IntentState To, DateTimeOffset At)> transitions)
    {
        var accrued = TimeSpan.Zero;
        DateTimeOffset? activeSince = null;
        foreach (var (to, at) in transitions)
        {
            if (activeSince is { } since)
            {
                accrued += at - since;
            }

            activeSince = ActiveStates.Contains(to) ? at : null;
        }

        return new WorkingTime(accrued, activeSince);
    }

    /// <summary>Why this text and selection can't be saved on an Intent, or <c>null</c> when they can.</summary>
    private static string? WhyNotSavable(string text, RepositorySelection selection, IReadOnlyList<RepositoryName> configured)
    {
        // Both at once: the page drops a removed repository, so a refusal that left it out would be the last the user hears of it.
        string?[] problems = [text == "" ? "An Intent needs text: write what you want changed." : null, selection.RemovedNotice(configured)];
        return AllOf(problems);
    }

    private static async Task<string?> WhyNotStartableAsync(
        OverseerDbContext database, Guid id, IntentState? state, int maxActiveIntents, CancellationToken cancellationToken)
    {
        if (WhyNotADraft(state, shownAs => $"This Intent is {shownAs}, and Start moves a Draft to Refining.") is { } notADraft)
        {
            return notADraft;
        }

        var hasRepositoryInScope = await database.Set<RepositoryInScopeRecord>().AnyAsync(repository => repository.IntentId == id, cancellationToken);
        // The maximum may have been lowered below the number of Active Intents: they stay, and Start waits until there is a free slot.
        var active = await ReadAsync(database.Intents.Where(intent => ActiveStates.Contains(intent.State)), cancellationToken);

        // Both at once, so that dealing with one doesn't lead straight to the other.
        string?[] problems =
        [
            hasRepositoryInScope ? null : "Start needs at least one Repository in scope: edit this Draft to select one.",
            active.Count < maxActiveIntents ? null : $"All Active Intent slots are taken, by {Prose.List([.. active.Select(QuotedFirstLine)])}. You can raise the maximum in Settings.",
        ];
        return AllOf(problems);
    }

    /// <summary>The problems there are, as one message; <c>null</c> when there are none.</summary>
    private static string? AllOf(string?[] problems) =>
        problems.OfType<string>().ToList() is { Count: > 0 } found ? string.Join(" ", found) : null;

    private static string QuotedFirstLine(Intent intent) => $"\"{intent.FirstLine}\"";

    // Names Draft rather than the states it refuses, so that a state added later is refused, open or not.
    private static string? WhyNotADraft(IntentState? state, Func<string, string> refusal) => state switch
    {
        null => "There is no such Intent: it may have been deleted.",
        IntentState.Draft => null,
        { } other => refusal(other.InWords()),
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
        // Names the closed states rather than the open ones, so that a state added later counts as open.
        ReadAsync(intents.Where(intent => !ClosedStates.Contains(intent.State)), cancellationToken);

    /// <summary>These Intents, newest first.</summary>
    private static async Task<IReadOnlyList<Intent>> ReadAsync(IQueryable<IntentRecord> intents, CancellationToken cancellationToken)
    {
        var stored = await intents
            .OrderByDescending(intent => intent.CreatedAt)
            .Select(intent => new
            {
                intent.Id,
                intent.Text,
                intent.State,
                Repositories = intent.RepositoriesInScope.Select(repository => repository.FullName).ToList(),
                // In the order they were made, as two can be made at the same moment.
                Transitions = intent.Transitions.OrderBy(transition => transition.Id).Select(transition => new { transition.To, transition.At }).ToList(),
            })
            .ToListAsync(cancellationToken);
        return [.. stored.Select(intent => new Intent(
            intent.Id,
            intent.Text,
            intent.State,
            ConfiguredRepositories.ParseSortedByName(intent.Repositories),
            WorkingTimeOf(intent.Transitions.Select(transition => (transition.To, transition.At)))))];
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

/// <summary>What an Intent's light says at a glance.</summary>
public enum IntentLight
{
    /// <summary>Agents are working.</summary>
    Green,
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

/// <summary>An Intent, as the human described it, how far it has come, the Configured Repositories it may change and its Working Time.</summary>
public sealed partial record Intent(Guid Id, string Text, IntentState State, IReadOnlyList<RepositoryName> RepositoriesInScope, WorkingTime WorkingTime)
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

/// <summary>
/// The total time an Intent has spent in Active states: what it had accrued by its last transition, and since when it has been adding
/// to that while it is still Active, so that it goes on advancing without the Intent changing.
/// </summary>
/// <param name="ActiveSince">When the Intent last became Active; <c>null</c> when it isn't Active.</param>
public readonly record struct WorkingTime(TimeSpan Accrued, DateTimeOffset? ActiveSince)
{
    /// <summary>The Working Time at <paramref name="now"/>.</summary>
    public TimeSpan At(DateTimeOffset now) => ActiveSince is { } since && now > since ? Accrued + (now - since) : Accrued;
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

    /// <summary>Every change of the Intent's state, from its capture as a Draft on.</summary>
    public List<IntentTransitionRecord> Transitions { get; init; } = [];
}

/// <summary>A change of an Intent's state, with its time, as PostgreSQL holds it.</summary>
public sealed class IntentTransitionRecord
{
    /// <summary>Increases with each transition recorded, so that it orders transitions made at the same moment.</summary>
    public long Id { get; init; }

    public Guid IntentId { get; init; }

    /// <summary>The state the Intent left; <c>null</c> for its capture as a Draft.</summary>
    public required IntentState? From { get; init; }

    public required IntentState To { get; init; }
    public required DateTimeOffset At { get; init; }
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
