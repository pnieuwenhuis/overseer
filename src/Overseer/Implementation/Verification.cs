using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Repositories;
using Overseer.Sandboxes;

namespace Overseer.Implementation;

/// <summary>
/// Verification: Overseer itself builds and tests repositories in a Sandbox, by their Repository Manifests (ADR 0003) as the
/// clones hold them now, so that work counts as done only when its tests actually pass.
/// </summary>
public sealed class Verification(RepositoryCommands commands, ComposeDependencies dependencies, IntentBranches branches)
{
    /// <summary>
    /// Verifies the work on a Task: every repository the Task lists, plus any other Repository in scope that holds changes since
    /// its last verified work. First, where a repository's changes touch its manifest or its Compose file, its <c>setup</c> command
    /// is run and its Compose services are started again; then each one's <c>build</c> and <c>test</c> are run, in their order. It
    /// stops at the first thing that fails. A repository without a manifest is skipped; an invalid manifest fails. So does a clone
    /// in scope that isn't on the Intent branch, before anything is run.
    /// </summary>
    /// <param name="intent">
    /// Whose Repositories in scope are all cloned in the Sandbox, where their Compose services share the machine.
    /// </param>
    /// <param name="listed">The repositories the Task lists.</param>
    /// <returns>
    /// What was run to verify them, in order, which passed when every step did, and which repositories hold changes. A step that
    /// failed is the last, but for clones off the Intent branch, which are all named. No step when there was nothing to verify,
    /// which passes too.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// They couldn't be verified, as when the Sandbox no longer answers, which says nothing about the work; the message says why.
    /// </exception>
    public Task<WorkVerification> VerifyAsync(
        Sandbox sandbox, Intent intent, IReadOnlyList<RepositoryName> listed, CancellationToken cancellationToken) =>
        VerifyAsync(sandbox, intent, listed, full: false, cancellationToken);

    /// <summary>
    /// The full verification, which the work on the Intent branch passes as a whole: it verifies every Repository in scope as
    /// <c>VerifyAsync</c> does, but a repository without a manifest fails when it differs from its default branch, and a repository
    /// that fails doesn't keep the others from being built and tested, so that all that fail are found. A <c>setup</c> command or a
    /// Compose start that fails still stops it, as what is built and tested after it relies on them.
    /// </summary>
    /// <returns>As for <c>VerifyAsync</c>.</returns>
    /// <exception cref="InvalidOperationException">They couldn't be verified, as for <c>VerifyAsync</c>.</exception>
    public Task<WorkVerification> VerifyAllAsync(Sandbox sandbox, Intent intent, CancellationToken cancellationToken) =>
        VerifyAsync(sandbox, intent, intent.RepositoriesInScope, full: true, cancellationToken);

    private async Task<WorkVerification> VerifyAsync(
        Sandbox sandbox, Intent intent, IReadOnlyList<RepositoryName> listed, bool full, CancellationToken cancellationToken)
    {
        var offBranch = await OffIntentBranchAsync(sandbox, intent, cancellationToken);
        if (offBranch.Count > 0)
        {
            return new WorkVerification(offBranch, []);
        }
        // All are asked, as an agent may have changed any.
        var changes = new Dictionary<RepositoryName, IReadOnlyList<string>>();
        foreach (var repository in intent.RepositoriesInScope)
        {
            var files = await branches.ChangedFilesAsync(sandbox, repository, cancellationToken);
            if (files.Count > 0)
            {
                changes.Add(repository, files);
            }
        }
        IReadOnlyList<RepositoryName> repositories = full ? intent.RepositoriesInScope : [.. listed.Union(changes.Keys)];
        var steps = await RunAsync(sandbox, intent.RepositoriesInScope, repositories, changes, full, cancellationToken);
        return new WorkVerification(steps, [.. intent.RepositoriesInScope.Where(changes.ContainsKey)]);
    }

    /// <summary>A failed step for each clone in scope that isn't on the Intent branch: what it holds isn't what gets pushed.</summary>
    private async Task<IReadOnlyList<VerificationStep>> OffIntentBranchAsync(
        Sandbox sandbox, Intent intent, CancellationToken cancellationToken)
    {
        var steps = new List<VerificationStep>();
        foreach (var repository in intent.RepositoriesInScope)
        {
            if (await branches.OffIntentBranchAsync(sandbox, repository, intent.Branch, cancellationToken) is { } head)
            {
                var failure = $"{repository} isn't on the Intent branch {intent.Branch}, but on {head}.";
                steps.Add(new VerificationStep(
                    repository, $"The Intent branch of {repository}", Command: null,
                    await commands.RedactedAsync(failure, cancellationToken),
                    "Check out the Intent branch again, bringing along what belongs on it, without forcing it to another commit."));
            }
        }
        return steps;
    }

    private async Task<IReadOnlyList<VerificationStep>> RunAsync(
        Sandbox sandbox, IReadOnlyList<RepositoryName> scope, IReadOnlyCollection<RepositoryName> repositories,
        IReadOnlyDictionary<RepositoryName, IReadOnlyList<string>> changes, bool full, CancellationToken cancellationToken)
    {
        var steps = new List<VerificationStep>();
        // Every manifest is read before anything runs, so that an invalid one is found at once.
        var manifests = new List<(RepositoryName Repository, RepositoryManifest Manifest)>();
        foreach (var repository in scope.Where(repositories.Contains))
        {
            switch (await commands.ReadManifestAsync(sandbox, repository, cancellationToken))
            {
                case RepositoryManifestReading.Valid(var manifest):
                    manifests.Add((repository, manifest));
                    break;
                case RepositoryManifestReading.Invalid(var errors):
                    var invalid = ManifestOf(repository);
                    steps.Add(new VerificationStep(
                        repository, invalid, Command: null, $"{invalid} is invalid.",
                        await commands.RedactedAsync(string.Join("\n", errors.Select(error => $"- {error}")), cancellationToken)));
                    if (!full)
                    {
                        return steps;
                    }
                    break;
                case RepositoryManifestReading.Missing
                    when full && await branches.DiffersFromDefaultBranchAsync(sandbox, repository, cancellationToken):
                    steps.Add(new VerificationStep(
                        repository, ManifestOf(repository), Command: null,
                        $"{repository} is changed and has no Repository Manifest ({RepositoryManifest.Path}), so its work can't be verified.",
                        Output: ""));
                    break;
            }
        }

        var outdated = manifests
            .Where(read => changes.TryGetValue(read.Repository, out var files)
                && (files.Contains(RepositoryManifest.Path)
                    || read.Manifest.Compose is { } compose && files.Contains(PathFromRoot(compose))))
            .ToList();
        foreach (var (repository, manifest) in outdated)
        {
            if (!await RunAsync(repository, "setup", manifest.Setup, RepositoryCommands.SetupLimit))
            {
                return steps;
            }
        }
        if (outdated.Any(read => read.Manifest.Compose is not null) && !await StartDependenciesAsync())
        {
            return steps;
        }

        foreach (var (repository, manifest) in manifests)
        {
            var passed = await RunAsync(repository, "build", manifest.Build, RepositoryCommands.BuildLimit)
                && await RunAsync(repository, "test", manifest.Test, RepositoryCommands.TestLimit);
            if (!passed && !full)
            {
                break;
            }
        }
        return steps;

        // Adds the step of one of a manifest's commands, and says whether it passed.
        async Task<bool> RunAsync(RepositoryName repository, string name, string command, TimeSpan limit)
        {
            var run = await commands.RunManifestCommandAsync(sandbox, repository, command, limit, cancellationToken);
            return await AddAsync(repository, $"The {name} command of {repository}", command, run, problem: null);
        }

        async Task<bool> StartDependenciesAsync()
        {
            // The others' too: a project's name depends on the names of all.
            var composed = new List<(RepositoryName Repository, string File)>();
            foreach (var repository in scope)
            {
                var known = manifests.Where(read => read.Repository == repository).Select(read => read.Manifest).FirstOrDefault()
                    ?? (await commands.ReadManifestAsync(sandbox, repository, cancellationToken) as RepositoryManifestReading.Valid)?.Manifest;
                if (known?.Compose is { } file)
                {
                    composed.Add((repository, file));
                }
            }
            var started = await dependencies.StartAsync(
                sandbox, composed, [.. outdated.Select(read => read.Repository)], replacing: true, _ => Task.CompletedTask,
                cancellationToken);
            foreach (var command in started)
            {
                if (!await AddAsync(command.Repository, command.What, command.Command, command.Run, command.Problem))
                {
                    return false;
                }
            }
            return true;
        }

        async Task<bool> AddAsync(RepositoryName repository, string what, string command, CommandRun run, string? problem)
        {
            if (run is CommandRun.Unreachable)
            {
                throw new InvalidOperationException(run.FailureOf(what));
            }
            var output = run switch
            {
                CommandRun.Succeeded succeeded => succeeded.Output,
                CommandRun.Failed failed => failed.Output,
                _ => "",
            };
            var failure = problem ?? run.FailureOf(what);
            steps.Add(new VerificationStep(
                repository, what, await commands.RedactedAsync(command, cancellationToken), failure, RepositoryCommands.EndOf(output)));
            return failure is null;
        }
    }

    private static string ManifestOf(RepositoryName repository) => $"The Repository Manifest of {repository} ({RepositoryManifest.Path})";

    /// <summary>A manifest's path of a file as git names it: without <c>.</c> segments or doubled slashes.</summary>
    private static string PathFromRoot(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(segment => segment != "."));
}

/// <summary>What verifying the work in a Sandbox came to.</summary>
/// <param name="Steps">What was run, or checked, in order; the work passed when every step did.</param>
/// <param name="Changed">
/// The repositories that hold changes since their last verified work, in the Intent's order; none known when a clone was off the
/// Intent branch.
/// </param>
public sealed record WorkVerification(IReadOnlyList<VerificationStep> Steps, IReadOnlyList<RepositoryName> Changed);

/// <summary>One thing run, or checked, to verify a repository.</summary>
/// <param name="What">What it is, such as "The test command of acme/web".</param>
/// <param name="Command">The command; <c>null</c> when none was run, as for an invalid Repository Manifest.</param>
/// <param name="Failure">A sentence saying how it failed; <c>null</c> when it passed.</param>
/// <param name="Output">The end of what it printed.</param>
public sealed record VerificationStep(RepositoryName Repository, string What, string? Command, string? Failure, string Output)
{
    public bool Passed => Failure is null;
}
