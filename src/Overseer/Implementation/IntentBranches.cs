using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Sandboxes;

namespace Overseer.Implementation;

/// <summary>
/// The git work on the Intent branch in the clones of a Sandbox. Each clone keeps, in a ref of its own, the commit that holds
/// the last verified work: where the branch started, and from then on what was last verified and pushed. What a Task changed is
/// whatever differs from it, and only verified work is committed by Overseer and pushed.
/// </summary>
public sealed class IntentBranches(RepositoryCommands commands)
{
    /// <summary>The ref in each clone at the last verified work. It never leaves the Sandbox.</summary>
    private const string Verified = "refs/overseer/verified";

    // Whom commits made in a Sandbox are by, Overseer's and its agents': no one's account, as .invalid is nobody's domain.
    private const string CommitterName = "Overseer";
    private const string CommitterEmail = "overseer@overseer.invalid";

    /// <summary>
    /// Puts a fresh clone, on its default branch, on the Intent branch: the one on GitHub when it exists there, and otherwise a new
    /// one from the default branch. What it then holds counts as verified. Commits made in the clone are by Overseer.
    /// </summary>
    /// <returns>Why it couldn't be put there; <c>null</c> once it is.</returns>
    public async Task<string?> CheckOutAsync(
        Sandbox sandbox, RepositoryName repository, string branch, CancellationToken cancellationToken)
    {
        var script =
            $"""
            set -e
            if git rev-parse --verify --quiet {ShellText.Quoted($"refs/remotes/origin/{branch}")} > /dev/null; then
              git checkout --quiet -b {ShellText.Quoted(branch)} --track {ShellText.Quoted($"origin/{branch}")}
            else
              git checkout --quiet -b {ShellText.Quoted(branch)}
            fi
            git config user.name {ShellText.Quoted(CommitterName)}
            git config user.email {ShellText.Quoted(CommitterEmail)}
            # An empty repository has no commit to remember yet.
            if git rev-parse --verify --quiet HEAD > /dev/null; then
              git update-ref {Verified} HEAD
            fi
            """;
        var run = await commands.RunAsync(sandbox, repository, script, RepositoryCommands.GitLimit, cancellationToken);
        return run.ErrorOf($"Putting {repository} on the Intent branch {branch}");
    }

    /// <summary>
    /// The files of the repository that differ from its last verified work, by their paths from its root: committed since,
    /// uncommitted or untracked, but not ignored.
    /// </summary>
    /// <exception cref="InvalidOperationException">git couldn't say; the message says why.</exception>
    public async Task<IReadOnlyList<string>> ChangedFilesAsync(
        Sandbox sandbox, RepositoryName repository, CancellationToken cancellationToken)
    {
        // Against the empty tree where there is no verified commit. Paths are separated by NULs, so none needs quoting.
        var script = ShellText.OutputAlone(
            $"""
            base=$(git rev-parse --verify --quiet {Verified} || git hash-object -t tree /dev/null) &&
              git diff --name-only --no-renames -z "$base" -- &&
              git ls-files --others --exclude-standard -z
            """);
        var run = await commands.RunAsync(sandbox, repository, script, RepositoryCommands.GitLimit, cancellationToken);
        return run is CommandRun.Succeeded succeeded
            ? [.. succeeded.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct()]
            : throw new InvalidOperationException(run.ErrorOf($"Looking for changes in {repository}"));
    }

    /// <summary>
    /// Commits what is uncommitted in the repository, untracked files too, with <paramref name="message"/>, and makes the Intent
    /// branch end there, wherever the clone was. The repository's commit hooks don't run: its build and test have verified the work.
    /// </summary>
    /// <returns>Why it couldn't be committed; <c>null</c> once nothing is left uncommitted.</returns>
    public async Task<string?> CommitAsync(
        Sandbox sandbox, RepositoryName repository, string branch, string message, CancellationToken cancellationToken)
    {
        var script =
            $"""
            set -e
            git switch --quiet --force-create {ShellText.Quoted(branch)}
            git add --all
            if ! git diff --cached --quiet; then
              git commit --quiet --no-verify --message {ShellText.Quoted(message)}
            fi
            """;
        var run = await commands.RunAsync(sandbox, repository, script, RepositoryCommands.GitLimit, cancellationToken);
        return run.ErrorOf($"Committing the work in {repository}");
    }

    /// <summary>
    /// Pushes the Intent branch to GitHub, after which what it holds counts as verified. Pushing it again changes nothing.
    /// </summary>
    /// <returns>Why it couldn't be pushed, with what GitHub said; <c>null</c> once it is.</returns>
    public async Task<string?> PushAsync(
        Sandbox sandbox, RepositoryName repository, string branch, CancellationToken cancellationToken)
    {
        var reference = ShellText.Quoted($"refs/heads/{branch}");
        var script =
            $"""
            git push --quiet --set-upstream origin {ShellText.Quoted($"refs/heads/{branch}:refs/heads/{branch}")} || exit
            git update-ref {Verified} {reference}
            """;
        var run = await commands.RunAsync(sandbox, repository, script, RepositoryCommands.GitLimit, cancellationToken);
        return run.ErrorOf($"Pushing the Intent branch {branch} of {repository}");
    }
}
