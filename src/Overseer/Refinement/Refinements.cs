using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Persistence;
using Overseer.Sandboxes;
using Overseer.Settings;
using Overseer.Specs;
using HandedRound = Overseer.Sandboxes.HandedIn<System.Collections.Generic.IReadOnlyList<Overseer.Refinement.HandedInQuestion>>;

namespace Overseer.Refinement;

/// <summary>
/// Refinement: when the Intent Lifecycle moves an Intent to Refining, the Analyst works on it in a fresh Sandbox the Intent
/// owns, holding its Repositories in scope. Each of its turns ends with a Round, which the human answers in the same Analyst
/// session, until the Analyst decides it has enough and hands in the Spec instead, which ends the Refinement: the Intent Lifecycle
/// stores it and moves the Intent to AwaitingSpecApproval, and the session and Sandbox go. Where each Refinement stands, and its
/// transcript of Rounds, is kept in PostgreSQL. It sets an Intent's state only through the Intent Lifecycle: a Refinement that
/// fails leaves the Intent Refining, in its slot, and red until a Retry, and a Round left unanswered for longer than the
/// Refinement idle timeout pauses the Intent, which stops the session and destroys the Sandbox.
/// <para>
/// Every Analyst session starts afresh from the transcript, so that a Refinement goes on where it was whatever stopped its last
/// session: its first message holds the Intent's text and every Round so far, with its answers, and a Round still open stays
/// open, for the human to answer once the Analyst has caught up. So the Refinement continues when its Intent resumes, after a
/// Retry, and after a restart of serve.
/// </para>
/// </summary>
public sealed class Refinements(
    IntentLifecycle lifecycle, SandboxRegistry sandboxes, IGitHubGateway gitHub, IDbContextFactory<OverseerDbContext> databases,
    SettingsStore settings, TimeProvider time, ILogger<Refinements> logger)
    : IHostedService
{
    /// <summary>
    /// How many turns in a row the Analyst may end without a valid Round or Spec, each time told what was wrong, before the Refinement
    /// fails: an Analyst that can't hand one in would otherwise go on at the human's expense.
    /// </summary>
    public const int HandInAttempts = 3;

    private readonly SandboxedWork _work = new(sandboxes, SandboxOwnerKind.Intent, logger);

    /// <summary>The Refinements waiting for the human's answers to their open Round, each completed once they are recorded.</summary>
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _awaitingAnswers = new();

    /// <summary>
    /// Raised whenever a Refinement changes phase or its transcript changes, on the thread that changed it, once the change is
    /// committed. Handlers must not throw; read the Refinement again with <see cref="ProgressOfAsync"/>.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Destroys the Sandboxes that the Refinements a stop of serve interrupted left behind, and continues each of them in a fresh
    /// Sandbox from its transcript; from then on launches the Analyst for each Intent that moves to Refining. Runs as serve starts,
    /// before the Portal takes requests, and sets them to provision their Sandbox before it does, so that no answers come for a
    /// Round whose Analyst session is gone.
    /// </summary>
    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        List<Guid> interrupted;
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            // Also a Refining Intent without a Refinement: one started before Refinement existed, or as serve stopped. A failed one
            // waits for its Retry.
            interrupted = await database.Intents
                .Where(intent => intent.State == IntentState.Refining
                    && !database.Refinements.Any(refinement => refinement.IntentId == intent.Id && refinement.Phase == RefinementPhase.Failed))
                .Select(intent => intent.Id)
                .ToListAsync(cancellationToken);
            foreach (var intentId in interrupted)
            {
                await EnterPhaseAsync(database, intentId, RefinementPhase.ProvisioningSandbox, error: null, cancellationToken);
            }
        }

        // No Refinement runs yet, so every Sandbox an Intent owns is left over.
        await _work.DestroyLeftOverAsync(cancellationToken);
        lifecycle.Transitioned += OnTransitioned;
        foreach (var intentId in interrupted)
        {
            Run(intentId);
        }
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        lifecycle.Transitioned -= OnTransitioned;
        return _work.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Where the Refinement of <paramref name="intent"/> stands; <c>null</c> unless it is Refining. A Refining Intent whose
    /// Refinement hasn't recorded anything yet, as just after Start, is about to provision its Sandbox.
    /// </summary>
    public async Task<RefinementProgress?> ProgressOfAsync(Intent intent, CancellationToken cancellationToken)
    {
        if (intent.State != IntentState.Refining)
        {
            return null;
        }
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return await database.Refinements
            .Where(refinement => refinement.IntentId == intent.Id)
            .Select(refinement => new RefinementProgress(
                refinement.Phase,
                refinement.Error,
                refinement.Phase == RefinementPhase.AwaitingAnswers
                    ? database.Rounds.Where(round => round.IntentId == intent.Id).Max(round => (int?)round.Number)
                    : null))
            .SingleOrDefaultAsync(cancellationToken)
            ?? new RefinementProgress(RefinementPhase.ProvisioningSandbox, Error: null, OpenRound: null);
    }

    /// <summary>
    /// The transcript of the Intent's Refinement: every Round the Analyst put to the human, in order, with the answers given.
    /// Empty when there is none.
    /// </summary>
    public async Task<IReadOnlyList<Round>> TranscriptOfAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return await TranscriptAsync(database, intentId, roundNumber: null, cancellationToken);
    }

    /// <summary>
    /// Records the human's answers to the Round the Analyst waits on, and sends them to the Analyst in the same session. Refused
    /// unless <paramref name="roundNumber"/> is the Round that is open now, so answers are taken only once.
    /// </summary>
    /// <param name="ownAnswers">
    /// The human's own answers, by question number. A question without one, or with a blank one, is left to the Analyst: its
    /// suggested answer is recorded as the Analyst's Choice.
    /// </param>
    /// <returns>Why the answers were refused; <c>null</c> when they are recorded and on their way to the Analyst.</returns>
    public async Task<string?> AnswerAsync(
        Guid intentId, int roundNumber, IReadOnlyDictionary<int, string> ownAnswers, CancellationToken cancellationToken)
    {
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            // The Refinement's row is where answers queue: of two submitted at once, the second finds it no longer awaiting them.
            var taken = await database.Refinements
                .Where(refinement => refinement.IntentId == intentId && refinement.Phase == RefinementPhase.AwaitingAnswers
                    && database.Intents.Any(intent => intent.Id == intentId && intent.State == IntentState.Refining)
                    && database.Rounds.Any(round => round.IntentId == intentId && round.Number == roundNumber && round.AnsweredAt == null)
                    && !database.Rounds.Any(round => round.IntentId == intentId && round.Number > roundNumber))
                .ExecuteUpdateAsync(refinement => refinement.SetProperty(record => record.Phase, RefinementPhase.AnalystWorking), cancellationToken);
            if (taken == 0)
            {
                return await WhyNotAnswerableAsync(database, intentId, roundNumber, cancellationToken);
            }
            var round = await database.Rounds
                .Include(round => round.Questions)
                .SingleAsync(round => round.IntentId == intentId && round.Number == roundNumber, cancellationToken);
            if (ownAnswers.Keys.Where(number => round.Questions.All(question => question.Number != number)).Order().Cast<int?>().FirstOrDefault() is { } unknown)
            {
                return $"Round {roundNumber} has no question {unknown}.";
            }
            round.AnsweredAt = time.GetUtcNow();
            foreach (var question in round.Questions)
            {
                var own = ownAnswers.GetValueOrDefault(question.Number) is { } answer && !string.IsNullOrWhiteSpace(answer)
                    ? answer.Trim().ReplaceLineEndings("\n")
                    : null;
                question.Answer = own ?? question.SuggestedAnswer;
                question.IsAnalystsChoice = own is null;
            }
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        Changed?.Invoke();
        if (_awaitingAnswers.TryGetValue(intentId, out var answered))
        {
            answered.TrySetResult();
        }
        return null;
    }

    /// <summary>Why answers to the Round can't be taken now, said for the human.</summary>
    private static async Task<string> WhyNotAnswerableAsync(OverseerDbContext database, Guid intentId, int roundNumber, CancellationToken cancellationToken)
    {
        var round = await database.Rounds
            .Where(round => round.IntentId == intentId && round.Number == roundNumber)
            .Select(round => new { round.AnsweredAt })
            .SingleOrDefaultAsync(cancellationToken);
        var paused = await database.Intents.AnyAsync(intent => intent.Id == intentId && intent.State == IntentState.Paused, cancellationToken);
        return round switch
        {
            null => $"The Analyst asked no Round {roundNumber}.",
            { AnsweredAt: not null } => $"Round {roundNumber} is already answered.",
            _ when paused => $"This Intent is Paused. Resume it to answer Round {roundNumber}.",
            _ => $"Round {roundNumber} no longer awaits answers.",
        };
    }

    /// <summary>
    /// Launches the Analyst again for a Refining Intent whose Refinement failed, in a new Sandbox. The Intent stays Refining
    /// throughout.
    /// </summary>
    /// <returns>Whether it launched again: not when there is no such Intent, or its Refinement hasn't failed.</returns>
    public async Task<bool> RetryAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            // Only one of two Retries pressed at once finds it failed.
            var retried = await database.Refinements
                .Where(refinement => refinement.IntentId == intentId && refinement.Phase == RefinementPhase.Failed
                    && database.Intents.Any(intent => intent.Id == intentId && intent.State == IntentState.Refining))
                .ExecuteUpdateAsync(refinement => refinement
                    .SetProperty(record => record.Phase, RefinementPhase.ProvisioningSandbox)
                    .SetProperty(record => record.Error, (string?)null), cancellationToken);
            if (retried == 0)
            {
                return false;
            }
        }
        Changed?.Invoke();
        Run(intentId);
        return true;
    }

    private void OnTransitioned(Guid intentId, IntentState to)
    {
        if (to == IntentState.Refining)
        {
            Run(intentId);
        }
    }

    /// <summary>Runs the Intent's Refinement in the background, from provisioning its Sandbox on.</summary>
    private void Run(Guid intentId) => _work.Run(
        $"the Refinement of the Intent {intentId}",
        stopping => RefineAsync(intentId, stopping),
        // Nothing to record when it is no longer the Intent's to run.
        error => error is null ? Task.CompletedTask : EnterPhaseAsync(intentId, RefinementPhase.Failed, error, CancellationToken.None));

    /// <summary>
    /// Provisions the Intent's Sandbox and has the Analyst work in it, Round after Round until it hands in the Spec, destroying the
    /// Sandbox once it is no longer used.
    /// </summary>
    /// <returns>Why the Refinement failed; <c>null</c> when it is no longer the Intent's to run.</returns>
    private async Task<string?> RefineAsync(Guid intentId, CancellationToken cancellationToken)
    {
        if (await lifecycle.FindAsync(intentId, cancellationToken) is not { State: IntentState.Refining } intent)
        {
            return null;
        }
        await EnterPhaseAsync(intentId, RefinementPhase.ProvisioningSandbox, error: null, cancellationToken);

        Sandbox sandbox;
        try
        {
            sandbox = await sandboxes.CreateAsync(
                new SandboxOwner(SandboxOwnerKind.Intent, intent.Id), [.. intent.RepositoriesInScope.Select(repository => repository.ToString())],
                cancellationToken);
        }
        catch (SandboxCreationFailedException exception)
        {
            return $"The Sandbox could not be created: {exception.Message}";
        }
        try
        {
            await EnterPhaseAsync(intentId, RefinementPhase.AnalystWorking, error: null, cancellationToken);
            IAgentSession started;
            try
            {
                started = await sandboxes.StartAgentSessionAsync(sandbox, AgentRole.Analyst, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "The Analyst's session for the Intent {IntentId} could not be started.", intentId);
                return $"The Analyst's session failed: {exception.Message}";
            }
            await using var session = started;
            // Before the Analyst's first turn, also when a Refinement starts again, as the manifests may have changed meanwhile.
            var manifests = await ManifestCheck.RunAsync(gitHub, intent.RepositoriesInScope, cancellationToken);
            // The session starts afresh from the transcript, which is empty when the Refinement has just started.
            var transcript = await TranscriptOfAsync(intentId, cancellationToken);
            var message = AnalystInstructions.FirstMessage(intent, manifests, transcript);
            var specHandIn = new SpecHandIn(intent.RepositoriesInScope, manifests.Lacking);
            if (transcript is [.., { IsAnswered: false } open])
            {
                // The Analyst catches up on the transcript first, and then the human answers the Round still open, rather than a new one.
                try
                {
                    await TakeTurnAsync(session, message, cancellationToken);
                }
                catch (AgentSessionFailedException exception)
                {
                    return $"The Analyst's session failed: {exception.Message}";
                }
                // Taken away, so that the next turn doesn't hand it in: it was told to hand in nothing this turn.
                if ((await RoundHandIn.Instance.TakeAsync(sandboxes, sandbox, cancellationToken), await specHandIn.TakeAsync(sandboxes, sandbox, cancellationToken))
                    is not (HandedRound.Nothing, HandedIn<Spec>.Nothing))
                {
                    logger.LogWarning("The Analyst of the Intent {IntentId} handed something in while Round {Round} was open; it is ignored.", intentId, open.Number);
                }
                if (await AskAsync(intentId, newRound: null, cancellationToken) is not { } answered)
                {
                    return null;
                }
                message = AnalystInstructions.Answers(answered);
            }
            var failedHandIns = 0;
            while (true)
            {
                AgentOutcome outcome;
                try
                {
                    outcome = await TakeTurnAsync(session, message, cancellationToken);
                }
                catch (AgentSessionFailedException exception)
                {
                    return $"The Analyst's session failed: {exception.Message}";
                }
                // Both, whichever it handed in, so that neither is left for a later turn.
                var round = await RoundHandIn.Instance.TakeAsync(sandboxes, sandbox, cancellationToken);
                var spec = await specHandIn.TakeAsync(sandboxes, sandbox, cancellationToken);
                switch (round, spec)
                {
                    case (HandedRound.Valid(var questions), HandedIn<Spec>.Nothing):
                        failedHandIns = 0;
                        if (await AskAsync(intentId, questions, cancellationToken) is not { } answered)
                        {
                            return null;
                        }
                        message = AnalystInstructions.Answers(answered);
                        continue;
                    case (HandedRound.Nothing, HandedIn<Spec>.Valid(var written)):
                        // The Refinement ends here: the Intent is no longer Refining, and the session and Sandbox go. Should it have
                        // left Refining meanwhile, the Spec is not stored, and the Refinement is no longer the Intent's to run.
                        if (await lifecycle.AwaitSpecApprovalAsync(
                                intentId, (database, stopping) => SpecStore.AddVersionAsync(database, intentId, written, SpecAuthor.Analyst, time.GetUtcNow(), stopping), cancellationToken)
                            is IntentChange.Refused refused)
                        {
                            logger.LogWarning("The Spec the Analyst handed in for the Intent {IntentId} was not stored: {Error}", intentId, refused.Error);
                        }
                        return null;
                }
                // What went wrong, for the human should the Refinement fail of it, and what the Analyst is told to set it right.
                var (wrong, retell) = (round, spec) switch
                {
                    (not HandedRound.Nothing, not HandedIn<Spec>.Nothing) =>
                        ("it handed in both a Round and a Spec.", AnalystInstructions.BothHandedIn),
                    (HandedRound.Invalid(var errors), _) =>
                        ($"its Round was not valid: {string.Join(" ", errors)}", AnalystInstructions.InvalidRound(errors)),
                    (_, HandedIn<Spec>.Invalid(var errors)) =>
                        ($"its Spec was not valid: {string.Join(" ", errors)}", AnalystInstructions.InvalidSpec(errors)),
                    _ when outcome is AgentOutcome.AskedQuestion asked =>
                        ($"it asked a question outside a Round: {asked.Question}", AnalystInstructions.AskedOutsideRound(asked.Question)),
                    _ => ("it ended its turn without handing in a Round or a Spec.", AnalystInstructions.NothingHandedIn),
                };
                if (++failedHandIns == HandInAttempts)
                {
                    return $"The Analyst's session failed: it handed in no valid Round or Spec in {HandInAttempts} turns in a row. The last time, {wrong}";
                }
                message = retell;
            }
        }
        finally
        {
            await sandboxes.DestroyOrLeaveAsync(sandbox);
        }
    }

    /// <summary>Sends the Analyst a message and waits for the turn it starts to end.</summary>
    /// <exception cref="AgentSessionFailedException">The turn broke off.</exception>
    private static async Task<AgentOutcome> TakeTurnAsync(IAgentSession session, string message, CancellationToken cancellationToken)
    {
        var turn = await session.SendAsync(message, cancellationToken);
        await foreach (var _ in turn.Updates.WithCancellation(cancellationToken))
        {
        }
        return await turn.Outcome.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Puts a Round to the human and waits for their answers, for as long as the Refinement idle timeout lets it: once the Round has
    /// waited longer, the Intent is paused instead. Only the time it waits counts, not the time the Analyst worked before it.
    /// </summary>
    /// <param name="newRound">
    /// The questions of a new Round, the Intent's next; <c>null</c> to put the Round still open to the human again, the Intent's latest.
    /// </param>
    /// <returns>
    /// The Round, answered; <c>null</c> when the Intent was paused, or has left Refining otherwise, and the Refinement is no longer the
    /// Intent's to run.
    /// </returns>
    private async Task<Round?> AskAsync(Guid intentId, IReadOnlyList<HandedInQuestion>? newRound, CancellationToken cancellationToken)
    {
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Ahead of the Round, so that no answer comes before there is something to tell.
        _awaitingAnswers[intentId] = answered;
        try
        {
            int number;
            await using (var database = await databases.CreateDbContextAsync(cancellationToken))
            {
                await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
                // One Refinement runs per Intent at a time, so no other Round is added meanwhile.
                var latest = await database.Rounds.Where(round => round.IntentId == intentId).MaxAsync(round => (int?)round.Number, cancellationToken) ?? 0;
                number = newRound is null ? latest : AddRound(database, intentId, latest + 1, newRound);
                await database.SaveChangesAsync(cancellationToken);
                await EnterPhaseAsync(database, intentId, RefinementPhase.AwaitingAnswers, error: null, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            var idleSince = time.GetUtcNow();
            Changed?.Invoke();

            if (!await AwaitAnswersAsync(intentId, answered.Task, idleSince, cancellationToken))
            {
                return null;
            }
            await using (var database = await databases.CreateDbContextAsync(cancellationToken))
            {
                return (await TranscriptAsync(database, intentId, number, cancellationToken)).Single();
            }
        }
        finally
        {
            _awaitingAnswers.TryRemove(KeyValuePair.Create(intentId, answered));
        }
    }

    /// <summary>Adds a Round of these questions with this number, without saving.</summary>
    /// <returns>Its number.</returns>
    private int AddRound(OverseerDbContext database, Guid intentId, int number, IReadOnlyList<HandedInQuestion> questions)
    {
        database.Rounds.Add(new RoundRecord
        {
            IntentId = intentId,
            Number = number,
            AskedAt = time.GetUtcNow(),
            Questions = [.. questions.Select((question, index) => new RoundQuestionRecord
            {
                IntentId = intentId,
                RoundNumber = number,
                Number = index + 1,
                Text = question.Text,
                SuggestedAnswer = question.SuggestedAnswer,
            })],
        });
        return number;
    }

    /// <summary>
    /// Waits for the human's answers until the Refinement has been idle since <paramref name="idleSince"/> for longer than the
    /// Refinement idle timeout, and then pauses the Intent, unless the answers came first. The timeout is read again whenever it is
    /// saved, so that a change applies to a Round already waiting.
    /// </summary>
    /// <param name="answered">Completes once the answers are recorded.</param>
    /// <returns>
    /// Whether the answers came; <c>false</c> when the Intent was paused instead, or has left Refining otherwise, and the Refinement is
    /// no longer the Intent's to run.
    /// </returns>
    private async Task<bool> AwaitAnswersAsync(Guid intentId, Task answered, DateTimeOffset idleSince, CancellationToken cancellationToken)
    {
        while (!answered.IsCompleted)
        {
            var limitsSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLimitsSaved() => limitsSaved.TrySetResult();
            // Ahead of reading the timeout, so that no change falls between them.
            settings.OperatingLimitsSaved += OnLimitsSaved;
            try
            {
                var idleUntil = idleSince + (await settings.GetOperatingLimitsAsync(cancellationToken)).RefinementIdleTimeout;
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var timedOut = Task.Delay(TimeSpan.FromTicks(Math.Max(0, (idleUntil - time.GetUtcNow()).Ticks)), time, waiting.Token);
                // Should the clock pass the moment while the delay is set, the delay would end later than it.
                if (time.GetUtcNow() < idleUntil)
                {
                    await Task.WhenAny(answered, timedOut, limitsSaved.Task);
                    await waiting.CancelAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (answered.IsCompleted)
                {
                    return true;
                }
                if (time.GetUtcNow() >= idleUntil)
                {
                    // Not paused when answers took the Refinement first, or the Intent has left Refining meanwhile some other way.
                    if (await PauseAsync(intentId, cancellationToken)
                        || await lifecycle.FindAsync(intentId, cancellationToken) is not { State: IntentState.Refining })
                    {
                        return false;
                    }
                    // Answers took it, which are told once recorded.
                    await answered.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                settings.OperatingLimitsSaved -= OnLimitsSaved;
            }
        }
        return true;
    }

    /// <summary>
    /// Pauses the Intent through the Intent Lifecycle, as its Refinement has waited for answers for longer than the idle timeout, and
    /// sets the Refinement to provision a Sandbox, which it does first when it continues after Resume, so that answers no longer go
    /// through and a resumed Intent shows where its Refinement stands from the moment it is Refining again.
    /// </summary>
    /// <returns>Whether it paused; not when answers came first, or the Intent is no longer Refining.</returns>
    private async Task<bool> PauseAsync(Guid intentId, CancellationToken cancellationToken)
    {
        var paused = await lifecycle.PauseAsync(
            intentId,
            // Of the pause and answers given at the same moment, the one that takes the Refinement's row first goes through.
            async (database, stopping) => await database.Refinements
                .Where(refinement => refinement.IntentId == intentId && refinement.Phase == RefinementPhase.AwaitingAnswers)
                .ExecuteUpdateAsync(refinement => refinement.SetProperty(record => record.Phase, RefinementPhase.ProvisioningSandbox), stopping) > 0,
            cancellationToken);
        if (paused is IntentChange.Refused refused)
        {
            logger.LogInformation("The idle Refinement of the Intent {IntentId} was not paused: {Reason}", intentId, refused.Error);
            return false;
        }
        logger.LogInformation("The Intent {IntentId} is paused: its Refinement waited for answers for longer than the idle timeout.", intentId);
        return true;
    }

    /// <summary>The Intent's Rounds in order, or only the one numbered <paramref name="roundNumber"/>.</summary>
    private static async Task<IReadOnlyList<Round>> TranscriptAsync(
        OverseerDbContext database, Guid intentId, int? roundNumber, CancellationToken cancellationToken)
    {
        var rounds = await database.Rounds
            .Where(round => round.IntentId == intentId && (roundNumber == null || round.Number == roundNumber))
            .OrderBy(round => round.Number)
            .Select(round => new
            {
                round.Number,
                IsAnswered = round.AnsweredAt != null,
                Questions = round.Questions.OrderBy(question => question.Number).ToList(),
            })
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        return [.. rounds.Select(round => new Round(
            round.Number,
            [.. round.Questions.Select(question => new RoundQuestion(
                question.Number,
                question.Text,
                question.SuggestedAnswer,
                question.Answer is { } answer ? new RoundAnswer(answer, question.IsAnalystsChoice) : null))],
            round.IsAnswered))];
    }

    private async Task EnterPhaseAsync(Guid intentId, RefinementPhase phase, string? error, CancellationToken cancellationToken)
    {
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            await EnterPhaseAsync(database, intentId, phase, error, cancellationToken);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Records where the Intent's Refinement stands, whether or not it had one before: updated when it has, and inserted when
    /// it hasn't. Should another insert come first meanwhile, this one is applied after it, as an update.
    /// </summary>
    private static async Task EnterPhaseAsync(OverseerDbContext database, Guid intentId, RefinementPhase phase, string? error, CancellationToken cancellationToken)
    {
        if (await UpdateAsync() > 0)
        {
            return;
        }
        database.Refinements.Add(new RefinementRecord { IntentId = intentId, Phase = phase, Error = error });
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            database.ChangeTracker.Clear();
            await UpdateAsync();
        }

        Task<int> UpdateAsync() => database.Refinements
            .Where(refinement => refinement.IntentId == intentId)
            .ExecuteUpdateAsync(refinement => refinement.SetProperty(record => record.Phase, phase).SetProperty(record => record.Error, error), cancellationToken);
    }
}

/// <summary>
/// Turns the light of an Intent whose Refinement failed red, for the Intent Lifecycle, which derives the light: a failed
/// Refinement leaves the Intent Refining, until a Retry.
/// </summary>
public sealed class RefinementFailures : IIntentFailures
{
    public IQueryable<Guid> FailedIn(OverseerDbContext database) =>
        database.Refinements
            .Where(refinement => refinement.Phase == RefinementPhase.Failed
                && database.Intents.Any(intent => intent.Id == refinement.IntentId && intent.State == IntentState.Refining))
            .Select(refinement => refinement.IntentId);
}

/// <summary>
/// Turns the light of an Intent whose Analyst waits for the human's answers to a Round orange, for the Intent Lifecycle, which
/// derives the light.
/// </summary>
public sealed class RefinementAttention : IIntentAttention
{
    public IQueryable<Guid> NeedingAttentionIn(OverseerDbContext database) =>
        database.Refinements
            .Where(refinement => refinement.Phase == RefinementPhase.AwaitingAnswers
                && database.Intents.Any(intent => intent.Id == refinement.IntentId && intent.State == IntentState.Refining))
            .Select(refinement => refinement.IntentId);
}

/// <summary>Where an Intent's Refinement stands.</summary>
public enum RefinementPhase
{
    /// <summary>
    /// Creating the Sandbox the Analyst works in, which takes minutes in Azure, or about to: a Refinement that continues once its Intent
    /// is resumed, or serve has restarted, is set to this ahead of it.
    /// </summary>
    ProvisioningSandbox,

    /// <summary>The Analyst is working on the Intent.</summary>
    AnalystWorking,

    /// <summary>The Analyst waits for the human's answers to its latest Round.</summary>
    AwaitingAnswers,

    /// <summary>The Refinement failed; it can be retried.</summary>
    Failed,
}

/// <summary>Where an Intent's Refinement stands, and why it failed when it did.</summary>
/// <param name="Error"><c>null</c> unless <paramref name="Phase"/> is <see cref="RefinementPhase.Failed"/>.</param>
/// <param name="OpenRound">
/// The number of the Round that awaits the human's answers; <c>null</c> unless <paramref name="Phase"/> is
/// <see cref="RefinementPhase.AwaitingAnswers"/>.
/// </param>
public sealed record RefinementProgress(RefinementPhase Phase, string? Error, int? OpenRound);

/// <summary>An Intent's Refinement, as PostgreSQL holds it: one per Intent, for the Refinement it has now.</summary>
public sealed class RefinementRecord
{
    public required Guid IntentId { get; init; }
    public required RefinementPhase Phase { get; set; }

    /// <summary>Why the Refinement failed, when it did.</summary>
    public string? Error { get; set; }
}
