using Overseer.Persistence;

namespace Overseer.Intents;

/// <summary>
/// Which Intents Need Attention, as the module that waits on the human keeps it, such as Refinement while the Analyst waits for
/// answers: what turns an Intent's light orange in <see cref="IntentLifecycle.LightOf"/>. The Intent Lifecycle reads it with the
/// Intents themselves, so that a module can ask for the human's attention without the lifecycle knowing about it.
/// </summary>
public interface IIntentAttention
{
    /// <summary>The ids of the Intents that Need Attention, as a query in <paramref name="database"/>.</summary>
    IQueryable<Guid> NeedingAttentionIn(OverseerDbContext database);
}
