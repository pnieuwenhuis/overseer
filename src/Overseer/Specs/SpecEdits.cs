using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Refinement;
using Overseer.Sandboxes;

namespace Overseer.Specs;

/// <summary>
/// The user's edits of a Spec awaiting their approval. Each save checks the edit as the Analyst's Spec is checked and adds it as a
/// new version, keeping the earlier ones. Saves are optimistic: one saves only while the version it edits is still the latest.
/// </summary>
public sealed class SpecEdits(IntentLifecycle lifecycle, SpecStore specs, IGitHubGateway gitHub, TimeProvider time)
{
    /// <summary>
    /// Saves <paramref name="edit"/> as the next version of the Intent's Spec, which edits version <paramref name="basedOn"/>.
    /// Refused, saving nothing, unless the Intent awaits Spec approval, the edit is a valid Spec, and version
    /// <paramref name="basedOn"/> is still the latest.
    /// </summary>
    public async Task<SpecEdit> SaveAsync(Guid intentId, int basedOn, EditedSpec edit, CancellationToken cancellationToken)
    {
        var intent = await lifecycle.FindAsync(intentId, cancellationToken);
        if (IntentLifecycle.WhyNotSpecEditable(intent?.State) is { } notEditable)
        {
            return new SpecEdit.Refused([notEditable]);
        }
        if (await specs.VersionOfAsync(intentId, basedOn, cancellationToken) is not { } version)
        {
            return new SpecEdit.Refused([$"The Spec has no version {basedOn} to edit."]);
        }

        // The Repository Manifests as they are now, as a Refinement starting now would find them.
        var manifests = await ManifestCheck.RunAsync(gitHub, intent!.RepositoriesInScope, cancellationToken);
        switch (new SpecHandIn(intent.RepositoriesInScope, manifests.Lacking).Check(edit, version.Spec.Decisions))
        {
            case HandedIn<Spec>.Invalid(var errors):
                return new SpecEdit.Refused(errors);
            case HandedIn<Spec>.Valid(var spec):
                return await AddVersionAsync(intentId, basedOn, spec, cancellationToken);
            default:
                throw new InvalidOperationException("An edit is always handed in.");
        }
    }

    /// <summary>Adds <paramref name="spec"/> as the next version of the Intent's Spec, while version <paramref name="basedOn"/> is the latest.</summary>
    private async Task<SpecEdit> AddVersionAsync(Guid intentId, int basedOn, Spec spec, CancellationToken cancellationToken)
    {
        var refusal = await lifecycle.EditSpecAsync<SpecEdit>(intentId, async (database, stopping) =>
        {
            if (await SpecStore.LatestOfAsync(database, intentId, stopping) is { } latest && latest.Number != basedOn)
            {
                return new SpecEdit.Outdated(latest);
            }
            await SpecStore.AddVersionAsync(database, intentId, spec, SpecAuthor.User, time.GetUtcNow(), stopping);
            return null;
        }, notEditable => new SpecEdit.Refused([notEditable]), cancellationToken);
        // Added under the lock while version basedOn was the latest, so as the one after it.
        return refusal ?? new SpecEdit.Saved(basedOn + 1);
    }
}

/// <summary>What became of saving an edit of a Spec.</summary>
public abstract record SpecEdit
{
    private SpecEdit()
    {
    }

    /// <summary>It is saved as this version.</summary>
    public sealed record Saved(int Version) : SpecEdit;

    /// <summary>Nothing is saved, for these reasons.</summary>
    public sealed record Refused(IReadOnlyList<string> Reasons) : SpecEdit;

    /// <summary>Nothing is saved, as the version edited is no longer the latest: <paramref name="Latest"/> is.</summary>
    public sealed record Outdated(SpecVersion Latest) : SpecEdit;
}
