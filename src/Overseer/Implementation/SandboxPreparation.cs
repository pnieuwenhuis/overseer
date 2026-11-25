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
public sealed class SandboxPreparation(SandboxRegistry sandboxes, ISecretReader secrets, ILogger<SandboxPreparation> logger)
{
    /// <summary>How much of a failed command's output is kept with the error: its end, where the command says what went wrong.</summary>
    private const int OutputKept = 4000;

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
                    sandbox, repository, $"Putting {repository} on the Intent branch {intent.Branch}", CheckOut(intent.Branch), cancellationToken)
                is { } notCheckedOut)
            {
                return notCheckedOut;
            }
            string? content;
            try
            {
                content = await sandboxes.ReadFileAsync(
                    sandbox, $"{Sandbox.CloneDirectory(repository.ToString())}/{RepositoryManifest.Path}", cancellationToken);
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
            // Stops at the first line that fails, as a CI job does, and has nothing to read, so that nothing waits for an answer.
            if (await RunAsync(
                    sandbox, repository, $"The setup command of {repository}", $"exec < /dev/null\nset -e\n{manifest.Setup}", cancellationToken)
                is { } notSetUp)
            {
                return notSetUp;
            }
        }
        foreach (var (repository, manifest) in manifests)
        {
            if (manifest.Compose is not { } compose)
            {
                continue;
            }
            await entering(ImplementationPhase.StartingDependencies, repository);
            // --wait returns only once every service runs, and is healthy where it has a health check.
            if (await RunAsync(
                    sandbox, repository, $"Starting the Compose services of {repository}",
                    $"docker compose --file {ShellText.Quoted(compose)} up --detach --wait", cancellationToken)
                is { } notStarted)
            {
                return notStarted;
            }
        }
        return null;
    }

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

    /// <summary>Runs a script from the repository's root.</summary>
    /// <param name="what">What the script does, to say that it failed.</param>
    /// <returns>Why it failed; <c>null</c> when it succeeded.</returns>
    private async Task<string?> RunAsync(
        Sandbox sandbox, RepositoryName repository, string what, string script, CancellationToken cancellationToken)
    {
        CommandResult result;
        try
        {
            result = await sandboxes.RunCommandAsync(
                sandbox, $"cd {ShellText.Quoted(Sandbox.CloneDirectory(repository.ToString()))} || exit\n{script}", cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "A command in {Repository} could not be run in the Sandbox {Sandbox}.", repository, sandbox.ResourceId);
            return $"{what} failed: {exception.Message}";
        }
        if (result.ExitCode == 0)
        {
            return null;
        }
        // The Sandbox holds the GitHub token to push with, so a command can print it; a secret never goes into PostgreSQL.
        var output = await secrets.ReadAsync(GitHubToken.SecretName, cancellationToken) is { Length: > 0 } token
            ? result.Output.Replace(token, "***")
            : result.Output;
        return $"{what} failed with exit code {result.ExitCode}:\n{EndOf(output)}";
    }

    /// <summary>The end of a command's output, as PostgreSQL can store it: without NUL characters.</summary>
    private static string EndOf(string output)
    {
        var text = output.Replace("\0", "").Trim();
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
