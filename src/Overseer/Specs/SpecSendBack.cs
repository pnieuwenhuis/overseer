using Microsoft.EntityFrameworkCore;
using Overseer.Intents;

namespace Overseer.Specs;

/// <summary>
/// The user sending a Spec that isn't right back to Refinement with comments, instead of approving it. The comments become part of the
/// Refinement transcript, and the Intent is Refining again through the Intent Lifecycle, where the Analyst goes on from the transcript,
/// the latest Spec version and the comments. Sending back is optimistic like editing: the comments are about the version the user
/// read, which must still be the latest.
/// </summary>
public sealed class SpecSendBack(IntentLifecycle lifecycle, TimeProvider time)
{
    /// <summary>
    /// Sends the Spec back with <paramref name="comments"/> about version <paramref name="aboutVersion"/>. Refused, storing nothing, unless
    /// the comments say something, that version is still the latest, the Intent awaits Spec approval and a slot is free.
    /// </summary>
    public async Task<SpecSentBack> SendAsync(Guid intentId, int aboutVersion, string? comments, CancellationToken cancellationToken)
    {
        var text = comments?.Trim().ReplaceLineEndings("\n");
        if (string.IsNullOrEmpty(text))
        {
            return new SpecSentBack.NotSent("Write your comments: say what is not right about the Spec.");
        }

        SpecSentBack? notSent = null;
        var change = await lifecycle.SendSpecBackAsync(intentId, async (database, stopping) =>
        {
            var latest = await SpecStore.LatestNumberAsync(database, intentId, stopping);
            if (latest != aboutVersion)
            {
                notSent = latest is { } newer
                    ? new SpecSentBack.NotSent($"The Spec has a newer version, {newer}, since you read version {aboutVersion}. Read it, and send it back with your comments if they still apply.")
                    : new SpecSentBack.NotSent($"The Spec has no version {aboutVersion}.");
                return "The comments are about a version that is not the latest.";
            }
            // Under the lock on the Intent's row, and the Analyst isn't working: no Round is added meanwhile.
            var afterRound = await database.Rounds.Where(round => round.IntentId == intentId).MaxAsync(round => (int?)round.Number, stopping) ?? 0;
            await SpecStore.AddCommentAsync(database, intentId, aboutVersion, afterRound, text, time.GetUtcNow(), stopping);
            return null;
        }, cancellationToken);
        return change switch
        {
            IntentChange.Made => new SpecSentBack.Sent(),
            _ when notSent is not null => notSent,
            IntentChange.Refused refused => new SpecSentBack.Refused(refused.Error),
            _ => throw new InvalidOperationException("A change is made or refused."),
        };
    }
}

/// <summary>What became of sending a Spec back.</summary>
public abstract record SpecSentBack
{
    private SpecSentBack()
    {
    }

    /// <summary>The Intent is Refining again.</summary>
    public sealed record Sent : SpecSentBack;

    /// <summary>Nothing is stored, for something wrong with the comments or the version they are about.</summary>
    public sealed record NotSent(string Reason) : SpecSentBack;

    /// <summary>Nothing is stored, as the Intent can't be sent back at the moment: it doesn't await Spec approval, or every slot is taken.</summary>
    public sealed record Refused(string Reason) : SpecSentBack;
}
