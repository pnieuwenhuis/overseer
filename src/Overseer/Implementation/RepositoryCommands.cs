using Overseer.GitHub;
using Overseer.Repositories;
using Overseer.Sandboxes;
using Overseer.Secrets;

namespace Overseer.Implementation;

/// <summary>
/// Runs Overseer's commands on a repository's clone in a Sandbox, each under a time limit, with the GitHub token taken out of
/// whatever comes back, and says apart whether a command failed, hung, or couldn't be run at all.
/// </summary>
public sealed class RepositoryCommands(
    SandboxRegistry sandboxes, ISecretReader secrets, TimeProvider time, ILogger<RepositoryCommands> logger)
{
    /// <summary>How much of a command's output is kept: its end, where the command says what went wrong.</summary>
    private const int OutputKept = 4000;

    /// <summary>How many bytes of the end of what a manifest command printed come back from the Sandbox.</summary>
    private const int ScriptOutputRead = 65536;

    // How long each command may take before Overseer gives up on it, so that one that hangs turns the Intent red rather than
    // leaving it working for good. Generous: only what hangs should ever reach them.
    public static readonly TimeSpan GitLimit = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SetupLimit = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan BuildLimit = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan TestLimit = TimeSpan.FromMinutes(30);

    /// <summary>Runs a script from the repository's root, for no longer than <paramref name="limit"/>.</summary>
    public async Task<CommandRun> RunAsync(
        Sandbox sandbox, RepositoryName repository, string script, TimeSpan limit, CancellationToken cancellationToken)
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
            return new CommandRun.TimedOut(limit);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "A command in {Repository} could not be run in the Sandbox {Sandbox}.", repository, sandbox.ResourceId);
            return new CommandRun.Unreachable(await RedactedAsync(exception.Message, cancellationToken));
        }
        var output = await RedactedAsync(result.Output, cancellationToken);
        return result.ExitCode == 0 ? new CommandRun.Succeeded(output) : new CommandRun.Failed(result.ExitCode, output);
    }

    /// <summary>
    /// Runs one of a Repository Manifest's commands as a script that stops at its first line that fails, as a CI job does, and has
    /// nothing to read, so that nothing waits for an answer. What it prints goes to a file, whose end is printed once the script
    /// ends: a process it leaves running, such as a server, then holds the file rather than the connection to the Sandbox, which
    /// would otherwise stay open for as long as that process runs.
    /// </summary>
    public Task<CommandRun> RunManifestCommandAsync(
        Sandbox sandbox, RepositoryName repository, string command, TimeSpan limit, CancellationToken cancellationToken) =>
        RunAsync(
            sandbox, repository,
            $"""
            log=$(mktemp) || exit
            (
            set -e
            {command}
            ) < /dev/null > "$log" 2>&1
            status=$?
            tail -c {ScriptOutputRead} "$log"
            rm -f "$log"
            exit "$status"
            """,
            limit, cancellationToken);

    /// <summary>The repository's Repository Manifest as its clone holds it now.</summary>
    /// <exception cref="InvalidOperationException">It couldn't be read; the message says why.</exception>
    public async Task<RepositoryManifestReading> ReadManifestAsync(
        Sandbox sandbox, RepositoryName repository, CancellationToken cancellationToken)
    {
        try
        {
            var content = await sandboxes.ReadFileAsync(
                sandbox, $"{Sandbox.CloneDirectory(repository)}/{RepositoryManifest.Path}", cancellationToken);
            return RepositoryManifest.Read(content);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "The Repository Manifest of {Repository} in the Sandbox {Sandbox} could not be read.", repository, sandbox.ResourceId);
            throw new InvalidOperationException(
                $"The Repository Manifest of {repository} ({RepositoryManifest.Path}) could not be read: "
                + await RedactedAsync(exception.Message, cancellationToken), exception);
        }
    }

    /// <summary>
    /// <paramref name="text"/> without the GitHub token. The Sandbox holds it to push with, so that anything from there can hold it,
    /// and a secret never goes into PostgreSQL.
    /// </summary>
    public async Task<string> RedactedAsync(string text, CancellationToken cancellationToken) =>
        text.Length == 0 ? text : Redaction.Without(text, await secrets.ReadAsync(GitHubToken.SecretName, cancellationToken) ?? "");

    /// <summary>The end of a command's output, as much of it as is kept.</summary>
    public static string EndOf(string output)
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

/// <summary>What running a command in a Sandbox came to.</summary>
public abstract record CommandRun
{
    private CommandRun()
    {
    }

    /// <summary>The command exited with 0.</summary>
    public sealed record Succeeded(string Output) : CommandRun;

    /// <summary>The command ran and failed.</summary>
    public sealed record Failed(int ExitCode, string Output) : CommandRun;

    /// <summary>The command didn't end within its time limit.</summary>
    public sealed record TimedOut(TimeSpan Limit) : CommandRun;

    /// <summary>The command couldn't be run, as when the Sandbox no longer answers; <paramref name="Error"/> says why.</summary>
    public sealed record Unreachable(string Error) : CommandRun;

    /// <summary>
    /// A sentence saying how <paramref name="what"/> the command did went wrong, without what it printed; <c>null</c> when it
    /// succeeded.
    /// </summary>
    public string? FailureOf(string what) => this switch
    {
        Failed failed => $"{what} failed with exit code {failed.ExitCode}.",
        TimedOut timedOut => $"{what} didn't finish within {timedOut.Limit.TotalMinutes:0} minutes.",
        Unreachable unreachable => $"{what} failed: {unreachable.Error}",
        _ => null,
    };

    /// <summary>
    /// As <see cref="FailureOf"/>, followed by the end of what a command that failed printed, to show the human; <c>null</c> when it
    /// succeeded.
    /// </summary>
    public string? ErrorOf(string what) => this is Failed failed
        ? $"{what} failed with exit code {failed.ExitCode}:\n{RepositoryCommands.EndOf(failed.Output)}"
        : FailureOf(what);
}
