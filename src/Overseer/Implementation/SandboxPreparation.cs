using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Repositories;
using Overseer.Sandboxes;
using Overseer.Secrets;

namespace Overseer.Implementation;

/// <summary>
/// Gets an Intent's Sandbox ready for the Squad: a new Sandbox with push access, holding the Repositories in scope, each put on
/// the Intent branch; then, for each repository with a Repository Manifest (ADR 0003), its <c>setup</c> command is run and the
/// services of its Compose file are started. A repository without a manifest is no failure, as a Task may still create one.
/// </summary>
public sealed class SandboxPreparation(
    SandboxRegistry sandboxes, ISecretReader secrets, TimeProvider time, ILogger<SandboxPreparation> logger)
{
    /// <summary>How much of a failed command's output is kept with the error: its end, where the command says what went wrong.</summary>
    private const int OutputKept = 4000;

    /// <summary>How many bytes of the end of what a <c>setup</c> command printed come back from the Sandbox.</summary>
    private const int SetupOutputRead = 65536;

    // How long each command may take before Overseer gives up on it, so that one that hangs turns the Intent red rather than
    // leaving it working for good. Generous: only what hangs should ever reach them.
    private static readonly TimeSpan GitLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SetupLimit = TimeSpan.FromMinutes(30);

    /// <summary>For starting a repository's Compose services, with pulling and building their images.</summary>
    private static readonly TimeSpan ComposeStartLimit = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long Compose itself waits for the services to run and be healthy: it then says which one isn't, which is worth more
    /// than giving up on the command.
    /// </summary>
    private static readonly TimeSpan ComposeWaitLimit = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Creates the Intent's Sandbox and prepares it. The Sandbox stays once it is ready; when preparing fails or is interrupted,
    /// it is destroyed.
    /// </summary>
    /// <param name="entering">Called as each phase begins, with the repository it is about, if it is about one.</param>
    /// <returns>Why the Sandbox couldn't be prepared, naming the repository at fault if one is; <c>null</c> once it is ready.</returns>
    public async Task<string?> PrepareAsync(
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
            return $"The Sandbox could not be created: {exception.Message}";
        }
        var ready = false;
        try
        {
            var error = await PrepareRepositoriesAsync(sandbox, intent, entering, cancellationToken);
            ready = error is null;
            return error;
        }
        finally
        {
            if (!ready)
            {
                await sandboxes.DestroyOrLeaveAsync(sandbox);
            }
        }
    }

    private async Task<string?> PrepareRepositoriesAsync(
        Sandbox sandbox, Intent intent, Func<ImplementationPhase, RepositoryName?, Task> entering, CancellationToken cancellationToken)
    {
        // Every manifest is read before any setup runs, so that an invalid one is found at once rather than after a long setup.
        var manifests = new List<(RepositoryName Repository, RepositoryManifest Manifest)>();
        foreach (var repository in intent.RepositoriesInScope)
        {
            await entering(ImplementationPhase.CheckingOutIntentBranch, repository);
            if (await RunAsync(
                    sandbox, repository, $"Putting {repository} on the Intent branch {intent.Branch}", CheckOut(intent.Branch), GitLimit,
                    cancellationToken)
                is CommandRun.Failed notCheckedOut)
            {
                return notCheckedOut.Error;
            }
            string? content;
            try
            {
                content = await sandboxes.ReadFileAsync(
                    sandbox, $"{Sandbox.CloneDirectory(repository)}/{RepositoryManifest.Path}", cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "The Repository Manifest of {Repository} for the Intent {IntentId} could not be read.", repository, intent.Id);
                return $"The Repository Manifest of {repository} ({RepositoryManifest.Path}) could not be read: {exception.Message}";
            }
            switch (RepositoryManifest.Read(content))
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
            if (await RunAsync(
                    sandbox, repository, $"The setup command of {repository}", Setup(manifest.Setup), SetupLimit, cancellationToken)
                is CommandRun.Failed notSetUp)
            {
                return notSetUp.Error;
            }
        }
        var composed = manifests.Where(read => read.Manifest.Compose is not null)
            .Select(read => (read.Repository, File: read.Manifest.Compose!))
            .ToList();
        return await StartDependenciesAsync(sandbox, composed, entering, cancellationToken);
    }

    /// <summary>
    /// Starts the services of each repository's Compose file, each repository's in a project no other repository's services are in
    /// (<see cref="ComposeProjects"/>).
    /// </summary>
    /// <returns>Why they couldn't be started, naming the repository at fault; <c>null</c> once they all run.</returns>
    private async Task<string?> StartDependenciesAsync(
        Sandbox sandbox, IReadOnlyList<(RepositoryName Repository, string File)> composed,
        Func<ImplementationPhase, RepositoryName?, Task> entering, CancellationToken cancellationToken)
    {
        // Compose is asked each project's name before any starts. One repository alone shares its name with none.
        var names = new List<(RepositoryName Repository, string Name)>();
        foreach (var (repository, file) in composed.Count > 1 ? composed : [])
        {
            await entering(ImplementationPhase.StartingDependencies, repository);
            var what = $"Reading the Compose file of {repository}";
            switch (await RunAsync(sandbox, repository, what, ProjectConfig(file), GitLimit, cancellationToken))
            {
                case CommandRun.Failed notRead:
                    return notRead.Error;
                case CommandRun.Succeeded read when ComposeProjects.NameIn(read.Output) is { } name:
                    names.Add((repository, name));
                    break;
                default:
                    return $"{what} failed: Compose gave no project name for it.";
            }
        }

        var renamed = ComposeProjects.Renamed(names);
        foreach (var (repository, file) in composed)
        {
            await entering(ImplementationPhase.StartingDependencies, repository);
            var project = renamed.TryGetValue(repository, out var own) ? $" --project-name {ShellText.Quoted(own)}" : "";
            // --wait returns only once every service runs, and is healthy where it has a health check.
            var up = $"docker compose --file {ShellText.Quoted(file)}{project} up --detach --wait --wait-timeout {ComposeWaitLimit.TotalSeconds:0}";
            if (await RunAsync(sandbox, repository, $"Starting the Compose services of {repository}", up, ComposeStartLimit, cancellationToken)
                is CommandRun.Failed notStarted)
            {
                return notStarted.Error;
            }
        }
        return null;
    }

    /// <summary>
    /// Prints a Compose file's project as Compose reads it, as JSON, and nothing else when it can: warnings, which Compose writes to
    /// standard error, are printed only when it fails.
    /// </summary>
    private static string ProjectConfig(string file) =>
        $"""
        errors=$(mktemp) || exit
        docker compose --file {ShellText.Quoted(file)} config --format json 2> "$errors"
        status=$?
        [ "$status" -eq 0 ] || cat "$errors"
        rm -f "$errors"
        exit "$status"
        """;

    /// <summary>
    /// Runs a repository's <c>setup</c> command as a script that stops at its first line that fails, as a CI job does, and has
    /// nothing to read, so that nothing waits for an answer. What it prints goes to a file, whose end is printed once the script
    /// ends: a process it leaves running, such as a server, then holds the file rather than the connection to the Sandbox, which
    /// would otherwise stay open for as long as that process runs.
    /// </summary>
    private static string Setup(string setup) =>
        $"""
        log=$(mktemp) || exit
        (
        set -e
        {setup}
        ) < /dev/null > "$log" 2>&1
        status=$?
        tail -c {SetupOutputRead} "$log"
        rm -f "$log"
        exit "$status"
        """;

    /// <summary>
    /// Puts a fresh clone, on its default branch, on the Intent branch: the one on GitHub when it exists there, and otherwise a new
    /// one from the default branch.
    /// </summary>
    private static string CheckOut(string branch) =>
        $"""
        if git rev-parse --verify --quiet {ShellText.Quoted($"refs/remotes/origin/{branch}")} > /dev/null; then
          git checkout --quiet -b {ShellText.Quoted(branch)} --track {ShellText.Quoted($"origin/{branch}")}
        else
          git checkout --quiet -b {ShellText.Quoted(branch)}
        fi
        """;

    /// <summary>Runs a script from the repository's root, for no longer than <paramref name="limit"/>.</summary>
    /// <param name="what">What the script does, to say that it failed.</param>
    private async Task<CommandRun> RunAsync(
        Sandbox sandbox, RepositoryName repository, string what, string script, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var timedOut = new CancellationTokenSource(limit, time);
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timedOut.Token);
        CommandResult result;
        try
        {
            // Not left to the provider alone: a Sandbox that no longer answers may not heed the cancellation.
            result = await sandboxes.RunCommandAsync(
                    sandbox, $"cd {ShellText.Quoted(Sandbox.CloneDirectory(repository))} || exit\n{script}", running.Token)
                .WaitAsync(running.Token);
        }
        catch (Exception) when (timedOut.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new CommandRun.Failed($"{what} didn't finish within {limit.TotalMinutes:0} minutes.");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "A command in {Repository} could not be run in the Sandbox {Sandbox}.", repository, sandbox.ResourceId);
            return new CommandRun.Failed($"{what} failed: {exception.Message}");
        }
        if (result.ExitCode == 0)
        {
            return new CommandRun.Succeeded(result.Output);
        }
        // The Sandbox holds the GitHub token to push with, so a command can print it; a secret never goes into PostgreSQL.
        var token = await secrets.ReadAsync(GitHubToken.SecretName, cancellationToken) ?? "";
        return new CommandRun.Failed($"{what} failed with exit code {result.ExitCode}:\n{EndOf(Redaction.Without(result.Output, token))}");
    }

    /// <summary>What running a command in the Sandbox came to.</summary>
    private abstract record CommandRun
    {
        private CommandRun()
        {
        }

        public sealed record Succeeded(string Output) : CommandRun;

        /// <summary><paramref name="Error"/> says what failed and why, for the human.</summary>
        public sealed record Failed(string Error) : CommandRun;
    }

    /// <summary>The end of a command's output.</summary>
    private static string EndOf(string output)
    {
        var text = output.Trim();
        if (text.Length <= OutputKept)
        {
            return text;
        }
        var from = text.Length - OutputKept;
        // Not from the middle of a character that takes two.
        if (char.IsLowSurrogate(text[from]))
        {
            from++;
        }
        return $"[…]\n{text[from..]}";
    }
}
