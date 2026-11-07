using Overseer.Persistence;

namespace Overseer.Intents;

/// <summary>
/// Which Intents have failed, as the module whose work failed them keeps it, such as Refinement: what turns an Intent's light
/// red in <see cref="IntentLifecycle.LightOf"/>. The Intent Lifecycle reads it with the Intents themselves, so that a module
/// can fail an Intent without the lifecycle knowing about it.
/// </summary>
public interface IIntentFailures
{
    /// <summary>The ids of the Intents that have failed, as a query in <paramref name="database"/>.</summary>
    IQueryable<Guid> FailedIn(OverseerDbContext database);
}
