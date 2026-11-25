using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Repositories;
using Overseer.Sandboxes;

namespace Overseer.Implementation;

/// <summary>
/// Gets an Intent's Sandbox ready for the Squad: a new Sandbox with push access, holding the Repositories in scope, each put on
/// the Intent branch; then, for each repository with a Repository Manifest (ADR 0003), its <c>setup</c> command is run and the
/// services of its Compose file are started. A repository without a manifest is no failure, as a Task may still create one.
/// </summary>
public sealed class SandboxPreparation(
    SandboxRegistry sandboxes, IntentBranches branches, RepositoryCommands commands, ComposeDependencies dependencies)
{
    /// <summary>
    /// Creates the Intent's Sandbox and prepares it. The Sandbox stays once it is ready; when preparing fails or is interrupted,
    /// it is destroyed.
    /// </summary>
    /// <param name="entering">Called as each phase begins, with the repository it is about, if it is about one.</param>
    public async Task<SandboxPreparationResult> PrepareAsync(
        Intent intent, Func<ImplementationPhase, RepositoryName?, Task> entering, CancellationToken cancellationToken)
    {
        await entering(ImplementationPhase.ProvisioningSandbox, null);
        Sandbox sandbox;
        try
        {
            sandbox = await sandboxes.CreateAsync(
                new SandboxOwner(SandboxOwnerKind.Intent, intent.Id), [.. intent.RepositoriesInScope.Select(repository => repository.ToString())],
                mayPush: true, cancellationToken);
        }
        catch (SandboxCreationFailedException exception)
        {
            return new SandboxPreparationResult.Failed($"The Sandbox could not be created: {exception.Message}");
        }
        var ready = false;
        try
        {
            var error = await PrepareRepositoriesAsync(sandbox, intent, entering, cancellationToken);
            ready = error is null;
            return error is null ? new SandboxPreparationResult.Ready(sandbox) : new SandboxPreparationResult.Failed(error);
        }
        finally
        {
            if (!ready)
            {
                await sandboxes.DestroyOrLeaveAsync(sandbox);
            }
        }
    }

    /// <returns>Why the repositories couldn't be prepared, naming the one at fault if one is; <c>null</c> once they all are.</returns>
    private async Task<string?> PrepareRepositoriesAsync(
        Sandbox sandbox, Intent intent, Func<ImplementationPhase, RepositoryName?, Task> entering, CancellationToken cancellationToken)
    {
        // Every manifest is read before any setup runs, so that an invalid one is found at once rather than after a long setup.
        var manifests = new List<(RepositoryName Repository, RepositoryManifest Manifest)>();
        foreach (var repository in intent.RepositoriesInScope)
        {
            await entering(ImplementationPhase.CheckingOutIntentBranch, repository);
            if (await branches.CheckOutAsync(sandbox, repository, intent.Branch, cancellationToken) is { } notCheckedOut)
            {
                return notCheckedOut;
            }
            RepositoryManifestReading reading;
            try
            {
                reading = await commands.ReadManifestAsync(sandbox, repository, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                return exception.Message;
            }
            switch (reading)
            {
                case RepositoryManifestReading.Valid(var manifest):
                    manifests.Add((repository, manifest));
                    break;
                case RepositoryManifestReading.Invalid(var errors):
                    return $"The Repository Manifest of {repository} ({RepositoryManifest.Path}) is invalid:\n"
                        + string.Join("\n", errors.Select(error => $"- {error}"));
            }
        }
        foreach (var (repository, manifest) in manifests)
        {
            await entering(ImplementationPhase.RunningSetup, repository);
            var setUp = await commands.RunManifestCommandAsync(
                sandbox, repository, manifest.Setup, RepositoryCommands.SetupLimit, cancellationToken);
            if (setUp.ErrorOf($"The setup command of {repository}") is { } notSetUp)
            {
                return notSetUp;
            }
        }
        var composed = manifests.Where(read => read.Manifest.Compose is not null)
            .Select(read => (read.Repository, File: read.Manifest.Compose!))
            .ToList();
        var started = await dependencies.StartAsync(
            sandbox, composed, [.. composed.Select(project => project.Repository)], replacing: false,
            repository => entering(ImplementationPhase.StartingDependencies, repository), cancellationToken);
        if (started.LastOrDefault()?.Error is { } notStarted)
        {
            return notStarted;
        }
        // What setup and Compose wrote in any clone is no part of the Intent's work.
        foreach (var repository in intent.RepositoriesInScope)
        {
            if (await branches.SetAsideCommandOutputAsync(sandbox, repository, cancellationToken) is { } notSetAside)
            {
                return notSetAside;
            }
        }
        return null;
    }
}

/// <summary>What preparing an Intent's Sandbox came to.</summary>
public abstract record SandboxPreparationResult
{
    private SandboxPreparationResult()
    {
    }

    /// <summary>The Sandbox is ready for the Squad.</summary>
    public sealed record Ready(Sandbox Sandbox) : SandboxPreparationResult;

    /// <summary><paramref name="Error"/> says why it couldn't be prepared, naming the repository at fault if one is.</summary>
    public sealed record Failed(string Error) : SandboxPreparationResult;
}
