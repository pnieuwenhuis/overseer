using Overseer.Intents;
using Overseer.Persistence;

namespace Overseer.Review;

/// <summary>
/// Turns the light of an Intent In Review orange, for the Intent Lifecycle, which derives the light: only the user can move it on
/// while no agent is working on it, which so far is always, as no agent works on an Intent In Review yet.
/// </summary>
public sealed class ReviewAttention : IIntentAttention
{
    public IQueryable<Guid> NeedingAttentionIn(OverseerDbContext database) =>
        database.Intents.Where(intent => intent.State == IntentState.InReview).Select(intent => intent.Id);
}
