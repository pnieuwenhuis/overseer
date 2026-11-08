using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Overseer.GitHub;
using Overseer.Persistence;
using Overseer.Repositories;
using Overseer.Settings;

namespace Overseer.Intents;

/// <summary>Owns the Intents' state: everything else asks it to capture or change an Intent and never sets state directly.</summary>
public sealed class IntentLifecycle(
    IDbContextFactory<OverseerDbContext> databases, SettingsStore settings, TimeProvider time, IIntentFailures failures, IIntentAttention attention)
{
    /// <summary>The states of an Active Intent, each occupying a slot; what counts towards the maximum is defined here and nowhere else.</summary>
    private static readonly IntentState[] ActiveStates = [IntentState.Refining, IntentState.Implementing, IntentState.InReview];

    /// <summary>The states in which an Intent waits for the human to approve something, which Needs Attention whatever the modules say.</summary>
    private static readonly IntentState[] AwaitingApprovalStates = [IntentState.AwaitingSpecApproval];

    /// <summary>The states of an Intent that is closed: what isn't one of these is open.</summary>
    private static readonly IntentState[] ClosedStates = [IntentState.Done, IntentState.Cancelled];

    /// <summary>
    /// The states a Paused Intent can be resumed to, as the module that works on Intents in that state takes it up again: so far only
    /// Refinement does. Implementing and In Review join as their tickets make them resumable.
    /// </summary>
    private static readonly IntentState[] ResumableStates = [IntentState.Refining];

    /// <summary>What a change refused for want of the Intent says.</summary>
    private const string NoSuchIntent = "There is no such Intent: it may have been deleted.";

    /// <summary>The PostgreSQL advisory lock for the slots, in this database; the number means nothing, and only has to stay the only one used.</summary>
    private const long ActiveIntentSlotsLock = 7_234_001;

    /// <summary>An Intent was captured, edited, deleted or changed state. Raised once the change is committed, so that a read then sees it.</summary>
    public event Action? Changed;

    /// <summary>
    /// An Intent moved to a state: the hook for the side effects of transitions, such as Refinement launching the Analyst when one
    /// moves to Refining. Raised with the Intent's id and its new state once the transition is committed, after <see cref="Changed"/>.
    /// Handlers must not throw, and only start the work.
    /// </summary>
    public event Action<Guid, IntentState>? Transitioned;

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
        return await ChangeStateAsync(id, async (database, state, stopping) =>
        {
            await HoldActiveIntentSlotsAsync(database, stopping);
            return await WhyNotStartableAsync(database, id, state, limits.MaxActiveIntents, stopping) is { } notStartable
                ? new Decision.Refuse(notStartable)
                : new Decision.MoveTo(IntentState.Refining);
        }, cancellationToken);
    }

    /// <summary>
    /// Moves a Refining Intent to AwaitingSpecApproval, as the Analyst has written its Spec, which <paramref name="storeSpec"/> stores
    /// in the same transaction: the Spec is there exactly when the Intent awaits its approval. It leaves its Active Intent slot, and its
    /// Working Time stops. Refused unless the Intent is Refining at this moment, and then nothing is stored.
    /// </summary>
    /// <param name="storeSpec">Adds the Spec to the database it is given, under the lock on the Intent's row, without saving.</param>
    public Task<IntentChange> AwaitSpecApprovalAsync(
        Guid id, Func<OverseerDbContext, CancellationToken, Task> storeSpec, CancellationToken cancellationToken) =>
        ChangeStateAsync(id, async (database, state, stopping) =>
        {
            if (state is not IntentState.Refining)
            {
                return new Decision.Refuse(state is null
                    ? NoSuchIntent
                    : $"This Intent is {state.Value.InWords()}, and only a Refining Intent gets a Spec from the Analyst.");
            }
            await storeSpec(database, stopping);
            return new Decision.MoveTo(IntentState.AwaitingSpecApproval);
        }, cancellationToken);

    /// <summary>
    /// Adds a version to the Spec of an Intent awaiting its approval, which <paramref name="addVersion"/> does in the same transaction,
    /// under the lock on the Intent's row: no other version is added meanwhile. The Intent stays awaiting approval, now of that version.
    /// Refused unless the Intent awaits Spec approval at this moment, or when <paramref name="addVersion"/> refuses, and then nothing is
    /// stored.
    /// </summary>
    /// <param name="addVersion">
    /// Adds the version to the database it is given, without saving, and gives <c>null</c>; or adds nothing, and gives why not.
    /// </param>
    public async Task<IntentChange> EditSpecAsync(
        Guid id, Func<OverseerDbContext, CancellationToken, Task<string?>> addVersion, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        if ((WhyNotSpecEditable(await LockStateAsync(database, id, cancellationToken)) ?? await addVersion(database, cancellationToken)) is { } refusal)
        {
            return new IntentChange.Refused(refusal);
        }
        await database.SaveChangesAsync(cancellationToken);
        return await CommitAndAnnounceAsync(transaction, cancellationToken);
    }

    /// <summary>Why the Spec of an Intent in <paramref name="state"/> can't be edited, or <c>null</c> when it can.</summary>
    /// <param name="state">The Intent's state; <c>null</c> when there is no such Intent.</param>
    // Names AwaitingSpecApproval rather than the states it refuses, so that a state added later is refused.
    public static string? WhyNotSpecEditable(IntentState? state) => state switch
    {
        null => NoSuchIntent,
        IntentState.AwaitingSpecApproval => null,
        { } other => $"This Intent is {other.InWords()}, and only a Spec awaiting approval can be edited.",
    };

    /// <summary>
    /// Pauses an Intent agents are working on: it leaves its Active Intent slot, its Working Time stops, and it keeps the state it was
    /// paused from, which Resume returns it to. Refused unless the Intent is Refining, Implementing or In Review at this moment, or when
    /// <paramref name="confirm"/> says the reason to pause no longer holds.
    /// </summary>
    /// <param name="confirm">
    /// Under the lock on the Intent's row, confirms that the reason to pause still holds, and records in the database it is given what
    /// goes with the pause, without saving; <c>false</c> leaves the Intent as it is.
    /// </param>
    public Task<IntentChange> PauseAsync(
        Guid id, Func<OverseerDbContext, CancellationToken, Task<bool>> confirm, CancellationToken cancellationToken) =>
        ChangeStateAsync(id, async (database, state, stopping) =>
        {
            if (state is not { } active || !ActiveStates.Contains(active))
            {
                return new Decision.Refuse(state is null
                    ? NoSuchIntent
                    : $"This Intent is {state.Value.InWords()}, and only an Intent that is Refining, Implementing or In Review can be paused.");
            }
            return await confirm(database, stopping)
                ? new Decision.MoveTo(IntentState.Paused)
                : new Decision.Refuse("The reason to pause this Intent no longer holds.");
        }, cancellationToken);

    /// <summary>
    /// Resumes a Paused Intent: it moves back to the state it was paused from, taking an Active Intent slot again, and the module that
    /// works on Intents in that state goes on with it, as Refinement does in a fresh Sandbox. So far that is only Refinement, so only an
    /// Intent paused while Refining can be resumed (<see cref="Intent.ResumesTo"/>). Whether it may is decided as the Intent is at this
    /// moment rather than as the user's page showed it.
    /// </summary>
    public async Task<IntentChange> ResumeAsync(Guid id, CancellationToken cancellationToken)
    {
        // Ahead of the transaction, as for Start.
        var limits = await settings.GetOperatingLimitsAsync(cancellationToken);
        return await ChangeStateAsync(id, async (database, _, stopping) =>
        {
            await HoldActiveIntentSlotsAsync(database, stopping);
            var intent = await FindAsync(database, id, stopping);
            return await WhyNotResumableAsync(database, intent, limits.MaxActiveIntents, stopping) is { } notResumable
                ? new Decision.Refuse(notResumable)
                : new Decision.MoveTo(intent!.ResumesTo!.Value);
        }, cancellationToken);
    }

    /// <summary>Why the Intent with this id can't be resumed at the moment, with what a refused Resume says, or <c>null</c> when it can.</summary>
    public async Task<string?> WhyNotResumableAsync(Guid id, CancellationToken cancellationToken)
    {
        var limits = await settings.GetOperatingLimitsAsync(cancellationToken);
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return await WhyNotResumableAsync(database, await FindAsync(database, id, cancellationToken), limits.MaxActiveIntents, cancellationToken);
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
    /// The light an Intent shows, or <c>null</c> when it has none. This is the one place a light is derived. The tickets that introduce a
    /// condition add it here, reading whatever <see cref="Intent"/> then carries, each with its member of <see cref="IntentLight"/>, its
    /// colour and its label in <see cref="IntentWords"/>, so that the light doesn't come to rely on the colour. Checked in this order:
    /// first red, labelled Failed, when <see cref="Intent.HasFailed"/> (so far a failed Refinement, as when its Sandbox couldn't be
    /// created; later a Failed Task or a failed merge, each through <see cref="IIntentFailures"/>), as a failure outweighs a question; then
    /// orange, labelled Needs Attention, when <see cref="Intent.NeedsAttention"/> (so far an unanswered Round, through
    /// <see cref="IIntentAttention"/>, and a Spec awaiting approval, which its state says; later an agent's question and Review Comments
    /// awaiting triage, through <see cref="IIntentAttention"/>). Implementing and
    /// InReview aren't settled by the green below: the tickets that make them reachable decide their light, as an Intent In Review can be up
    /// to the user rather than to agents.
    /// </summary>
    public static IntentLight? LightOf(Intent intent) =>
        intent.HasFailed ? IntentLight.Red
        : intent.NeedsAttention ? IntentLight.Orange
        // What is left is an Active Intent or a state in which nothing runs.
        : ActiveStates.Contains(intent.State) ? IntentLight.Green
        : null;

    /// <summary>The Intents the Backlog shows under this filter, newest first.</summary>
    public async Task<IReadOnlyList<Intent>> ListBacklogAsync(BacklogFilter filter, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return await ReadAsync(
            database, filter == BacklogFilter.Closed ? database.Intents.Where(intent => ClosedStates.Contains(intent.State)) : Open(database.Intents),
            cancellationToken);
    }

    /// <summary>The Intent with this id, in whatever state; <c>null</c> when there is none, as after it was deleted.</summary>
    public async Task<Intent?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return await FindAsync(database, id, cancellationToken);
    }

    /// <summary>The Intent with this id, as <paramref name="database"/> sees it; <c>null</c> when there is none.</summary>
    private async Task<Intent?> FindAsync(OverseerDbContext database, Guid id, CancellationToken cancellationToken) =>
        (await ReadAsync(database, database.Intents.Where(intent => intent.Id == id), cancellationToken)).SingleOrDefault();

    /// <summary>
    /// Gives each Intent that left Draft before transitions were recorded the start it must have had, now: when it was started isn't
    /// known, so its Working Time counts from the upgrade rather than from its capture, which would count its time as a Draft. Run at
    /// startup, once the schema is up to date and before anything else changes an Intent; an Intent that has its start already is left
    /// alone, so a later startup changes nothing.
    /// </summary>
    public async Task RecordStartsBeforeTransitionsAsync(CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var unstarted = await database.Intents
            .Where(intent => intent.State != IntentState.Draft && !intent.Transitions.Any(transition => transition.From != null))
            .Select(intent => new { intent.Id, intent.State })
            .ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        database.Set<IntentTransitionRecord>().AddRange(unstarted.Select(intent =>
            new IntentTransitionRecord { IntentId = intent.Id, From = IntentState.Draft, To = intent.State, At = now }));
        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Says which open Intents have <paramref name="repository"/> in scope, in any case, by their first lines, newest first; <c>null</c>
    /// when none has.
    /// </summary>
    internal static async Task<string?> DescribeOpenWithInScopeAsync(OverseerDbContext database, RepositoryName repository, CancellationToken cancellationToken)
    {
        var fullName = repository.ToString();
        var open = await QuotedFirstLinesAsync(Open(database.Intents.Where(intent => intent.RepositoriesInScope.Any(inScope => inScope.FullName == fullName))), cancellationToken);
        return open.Count == 0
            ? null
            : Prose.Agreeing(open,
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
    /// How every change of an Intent's state is made: under the lock on its row, <paramref name="decide"/> is given its state and decides
    /// where it goes, recording what goes with that in the database it is given, without saving. The Intent then moves there, and the
    /// change is committed and announced, raising <see cref="Transitioned"/>. A refusal changes nothing.
    /// </summary>
    /// <param name="decide">Given the Intent's state, <c>null</c> when there is no such Intent.</param>
    private async Task<IntentChange> ChangeStateAsync(
        Guid id, Func<OverseerDbContext, IntentState?, CancellationToken, Task<Decision>> decide, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        switch (await decide(database, await LockStateAsync(database, id, cancellationToken), cancellationToken))
        {
            case Decision.MoveTo(var to):
                await TransitionAsync(database, id, to, cancellationToken);
                var made = await CommitAndAnnounceAsync(transaction, cancellationToken);
                Transitioned?.Invoke(id, to);
                return made;
            case Decision.Refuse(var error):
                return new IntentChange.Refused(error);
            default:
                throw new UnreachableException();
        }
    }

    /// <summary>Where a change of state goes, as decided under the lock on the Intent's row.</summary>
    private abstract record Decision
    {
        private Decision()
        {
        }

        /// <summary>The Intent moves to <paramref name="State"/>.</summary>
        public sealed record MoveTo(IntentState State) : Decision;

        /// <summary>Nothing changes; <paramref name="Error"/> says why.</summary>
        public sealed record Refuse(string Error) : Decision;
    }

    /// <summary>
    /// Moves the Intent with this id to <paramref name="to"/>, and records the transition with its time and the state it left, as read
    /// under the lock on its row that <paramref name="database"/>'s transaction takes here if it hasn't already. Every change of state
    /// goes through here: the transitions are what Working Time is derived from, and what later tickets rely on, such as which state a
    /// Paused Intent resumes to.
    /// </summary>
    private async Task TransitionAsync(OverseerDbContext database, Guid id, IntentState to, CancellationToken cancellationToken)
    {
        var from = await LockStateAsync(database, id, cancellationToken)
            ?? throw new InvalidOperationException($"There is no Intent {id} to move to {to}: decide on a transition under the lock on its row.");
        await database.Intents.Where(intent => intent.Id == id)
            .ExecuteUpdateAsync(intent => intent.SetProperty(record => record.State, to), cancellationToken);
        database.Set<IntentTransitionRecord>().Add(new IntentTransitionRecord { IntentId = id, From = from, To = to, At = time.GetUtcNow() });
        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The Working Time of an Intent in <paramref name="state"/> with these transitions, in the order they were made: the sum of the
    /// periods from a transition into an Active state to the next one, and the time since the last one while the Intent is still Active.
    /// A Draft has none, as no work on it has begun.
    /// </summary>
    private static WorkingTime? WorkingTimeOf(IntentState state, IEnumerable<Transition> transitions)
    {
        if (state == IntentState.Draft)
        {
            return null;
        }

        var accrued = TimeSpan.Zero;
        DateTimeOffset? activeSince = null;
        foreach (var (_, to, at) in transitions)
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

        // Both at once, so that dealing with one doesn't lead straight to the other.
        string?[] problems =
        [
            hasRepositoryInScope ? null : "Start needs at least one Repository in scope: edit this Draft to select one.",
            await WhyNoFreeSlotAsync(database, maxActiveIntents, cancellationToken),
        ];
        return AllOf(problems);
    }

    private static async Task<string?> WhyNotResumableAsync(
        OverseerDbContext database, Intent? intent, int maxActiveIntents, CancellationToken cancellationToken) =>
        intent switch
        {
            null => NoSuchIntent,
            { State: not IntentState.Paused } => $"This Intent is {intent.State.InWords()}, and Resume continues a Paused Intent.",
            { ResumesTo: null } => "So far Resume only continues a Refinement, and this Intent wasn't Refining when it was paused.",
            _ => await WhyNoFreeSlotAsync(database, maxActiveIntents, cancellationToken),
        };

    /// <summary>
    /// Why no Intent can become Active at the moment, naming the Intents that hold the slots and the way out; <c>null</c> when a slot is
    /// free. The same for Start and Resume.
    /// </summary>
    private static async Task<string?> WhyNoFreeSlotAsync(OverseerDbContext database, int maxActiveIntents, CancellationToken cancellationToken)
    {
        // The maximum may have been lowered below the number of Active Intents: they stay, and the Intent waits until there is a free slot.
        var active = await QuotedFirstLinesAsync(database.Intents.Where(intent => ActiveStates.Contains(intent.State)), cancellationToken);
        return active.Count < maxActiveIntents ? null : $"All Active Intent slots are taken, by {Prose.List(active)}. You can raise the maximum in Settings.";
    }

    /// <summary>The problems there are, as one message; <c>null</c> when there are none.</summary>
    private static string? AllOf(string?[] problems) =>
        problems.OfType<string>().ToList() is { Count: > 0 } found ? string.Join(" ", found) : null;

    // Names Draft rather than the states it refuses, so that a state added later is refused, open or not.
    private static string? WhyNotADraft(IntentState? state, Func<string, string> refusal) => state switch
    {
        null => NoSuchIntent,
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

    private static IQueryable<IntentRecord> Open(IQueryable<IntentRecord> intents) =>
        // Names the closed states rather than the open ones, so that a state added later counts as open.
        intents.Where(intent => !ClosedStates.Contains(intent.State));

    /// <summary>These Intents' first lines, in quotes, newest first.</summary>
    private static async Task<IReadOnlyList<string>> QuotedFirstLinesAsync(IQueryable<IntentRecord> intents, CancellationToken cancellationToken)
    {
        var texts = await intents.OrderByDescending(intent => intent.CreatedAt).Select(intent => intent.Text).ToListAsync(cancellationToken);
        return [.. texts.Select(text => $"\"{Intent.FirstLineOf(text)}\"")];
    }

    /// <summary>These Intents of <paramref name="database"/>, newest first.</summary>
    private async Task<IReadOnlyList<Intent>> ReadAsync(OverseerDbContext database, IQueryable<IntentRecord> intents, CancellationToken cancellationToken)
    {
        var failed = failures.FailedIn(database);
        var needingAttention = attention.NeedingAttentionIn(database);
        var stored = await intents
            // One query for the Repositories in scope and one for the transitions, rather than a row for each combination of them.
            .AsSplitQuery()
            .OrderByDescending(intent => intent.CreatedAt)
            .Select(intent => new
            {
                intent.Id,
                intent.Text,
                intent.State,
                Repositories = intent.RepositoriesInScope.Select(repository => repository.FullName).ToList(),
                // In the order they were made, as two can be made at the same moment.
                Transitions = intent.Transitions.OrderBy(transition => transition.Id).Select(transition => new Transition(transition.From, transition.To, transition.At)).ToList(),
                HasFailed = failed.Contains(intent.Id),
                NeedsAttention = AwaitingApprovalStates.Contains(intent.State) || needingAttention.Contains(intent.Id),
            })
            .ToListAsync(cancellationToken);
        return [.. stored.Select(intent => new Intent(
            intent.Id,
            intent.Text,
            intent.State,
            ConfiguredRepositories.ParseSortedByName(intent.Repositories),
            WorkingTimeOf(intent.State, intent.Transitions),
            intent.HasFailed,
            intent.NeedsAttention)
        {
            ResumesTo = ResumesToOf(intent.State, intent.Transitions),
        })];
    }

    /// <summary>
    /// The state Resume moves an Intent in <paramref name="state"/> with these transitions back to: the one the last transition into
    /// Paused left, when the Intent is Paused and that state is one it can be resumed to; otherwise <c>null</c>. The one place that is decided.
    /// </summary>
    private static IntentState? ResumesToOf(IntentState state, IEnumerable<Transition> transitions) =>
        state == IntentState.Paused
        && transitions.LastOrDefault(transition => transition.To == IntentState.Paused) is { From: { } from }
        && ResumableStates.Contains(from)
            ? from
            : null;

    /// <summary>A transition as Working Time and Resume need it: the state the Intent left and the one it moved to, and when.</summary>
    private readonly record struct Transition(IntentState? From, IntentState To, DateTimeOffset At);
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

    /// <summary>The Intent Needs Attention: it can't go on until the user acts, such as by answering the Analyst.</summary>
    Orange,

    /// <summary>The Intent has failed, and can't go on until the user acts, such as with Retry.</summary>
    Red,
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

/// <summary>
/// An Intent, as the human described it, how far it has come, the Configured Repositories it may change, its Working Time, and whether
/// it has failed.
/// </summary>
/// <param name="WorkingTime"><c>null</c> for a Draft, which has none.</param>
/// <param name="HasFailed">
/// Whether work on it has failed, as a module that works on it says (<see cref="IIntentFailures"/>), which turns its light red.
/// </param>
/// <param name="NeedsAttention">
/// Whether it waits for the user, as its state says, such as awaiting Spec approval, or a module that works on it says
/// (<see cref="IIntentAttention"/>); it turns its light orange.
/// </param>
public sealed partial record Intent(
    Guid Id, string Text, IntentState State, IReadOnlyList<RepositoryName> RepositoriesInScope, WorkingTime? WorkingTime, bool HasFailed,
    bool NeedsAttention)
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
    public string FirstLine => FirstLineOf(Text);

    /// <inheritdoc cref="FirstLine"/>
    public static string FirstLineOf(string text) =>
        text.Split('\n').Select(line => AtxHeading().Replace(line, "${heading}").Trim()).FirstOrDefault(line => line != "")
        ?? text.Trim().Split('\n', 2)[0].Trim();

    /// <summary>
    /// The state Resume moves a Paused Intent back to, the one it was paused from. <c>null</c> unless it is Paused from a state it can
    /// be resumed to, and then it can't be resumed: so far only Refining, as nothing yet takes up an Intent in another Active state.
    /// </summary>
    public IntentState? ResumesTo { get; init; }

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

    /// <summary>The Working Time at <paramref name="now"/> as it is shown: in whole minutes, a part of a minute left out.</summary>
    public TimeSpan ShownAt(DateTimeOffset now) => TimeSpan.FromMinutes(Math.Floor(At(now).TotalMinutes));
}

/// <summary>Where an Intent is in its lifecycle.</summary>
public enum IntentState
{
    Draft,
    Refining,

    /// <summary>The Analyst has written the Spec, which awaits the human's approval.</summary>
    AwaitingSpecApproval,

    // Nothing reaches these yet but Paused, except by an update of the database in tests. The transitions to and from them come with their triggers.
    Approved,
    Implementing,
    InReview,

    /// <summary>
    /// Work on the Intent stopped, to be resumed later from the state it was paused from. So far only a Refinement that waited on the
    /// human for longer than the idle timeout gets here.
    /// </summary>
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
