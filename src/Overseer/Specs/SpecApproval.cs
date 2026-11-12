using Microsoft.EntityFrameworkCore;
using Overseer.Intents;

namespace Overseer.Specs;

/// <summary>
/// The user approving the Spec version they are looking at. It becomes the Approved Spec, the fixed contract for implementation and
/// review: only the latest version can be approved, as approving an earlier one would quietly undo later edits. The Intent then moves
/// through the Intent Lifecycle, to Implementing when approval also starts it, otherwise to Approved.
/// </summary>
public sealed class SpecApproval(IntentLifecycle lifecycle)
{
    /// <summary>
    /// Approves version <paramref name="version"/>, and starts the implementation too when <paramref name="start"/>. Refused, approving
    /// nothing, unless that version is still the latest, the Intent awaits Spec approval and, to start, a slot is free.
    /// </summary>
    public async Task<SpecApproved> ApproveAsync(Guid intentId, int version, bool start, CancellationToken cancellationToken)
    {
        SpecApproved? notApproved = null;
        var change = await lifecycle.ApproveSpecAsync(intentId, start, async (database, stopping) =>
        {
            // Under the lock on the Intent's row, which whoever edits the Spec holds: no version is added meanwhile.
            var latest = await SpecStore.LatestNumberAsync(database, intentId, stopping);
            if (latest != version)
            {
                notApproved = new SpecApproved.NotApproved(latest is { } newer
                    ? $"The Spec has a newer version, {newer}, since you read version {version}. Read it, and approve it if it is right."
                    : $"The Spec has no version {version}.");
                return "The version is not the latest.";
            }
            await database.Intents.Where(intent => intent.Id == intentId)
                .ExecuteUpdateAsync(intent => intent.SetProperty(record => record.ApprovedSpecVersion, version), stopping);
            return null;
        }, cancellationToken);
        return change switch
        {
            IntentChange.Made => new SpecApproved.Approved(),
            _ when notApproved is not null => notApproved,
            IntentChange.Refused refused => new SpecApproved.Refused(refused.Error),
            _ => throw new InvalidOperationException("A change is made or refused."),
        };
    }
}

/// <summary>What became of approving a Spec.</summary>
public abstract record SpecApproved
{
    private SpecApproved()
    {
    }

    /// <summary>The Spec version is the Approved Spec, and the Intent is Approved or Implementing.</summary>
    public sealed record Approved : SpecApproved;

    /// <summary>Nothing is approved, as the version isn't the latest.</summary>
    public sealed record NotApproved(string Reason) : SpecApproved;

    /// <summary>Nothing is approved, as the Intent can't be at the moment: it doesn't await Spec approval, or, to start, every slot is taken.</summary>
    public sealed record Refused(string Reason) : SpecApproved;
}
