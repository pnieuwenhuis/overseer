using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Sandboxes;

namespace Overseer.Implementation;

/// <summary>
/// The git work on the Intent branch in the clones of a Sandbox. Each clone keeps, in a ref of its own, the commit that holds
/// the last verified work: where the branch started, and from then on what was last verified and pushed. What a Task changed is
/// whatever differs from it, and only verified work is committed by Overseer and pushed. What Overseer's own commands write in a
/// clone, such as a <c>setup</c> or a build where <c>.gitignore</c> doesn't cover it, is no part of that work: it stays there,
/// and is taken as a change only once something else changes it.
/// </summary>
public sealed class IntentBranches(RepositoryCommands commands)
{
    // The refs each clone keeps. None of them leaves the Sandbox.

    /// <summary>The commit of the last verified work.</summary>
    private const string Verified = "refs/overseer/verified";

    /// <summary>The tree of the work as it was last taken (<see cref="TakeWorkAsync"/>): what a commit then holds.</summary>
    private const string Work = "refs/overseer/work";

    /// <summary>
    /// The tree of the clone as Overseer's commands last left it, without the work: it differs from <see cref="LeftOn"/> in what
    /// they wrote.
    /// </summary>
    private const string Left = "refs/overseer/left";

    /// <summary>The tree of the commit checked out as <see cref="Left"/> was recorded.</summary>
    private const string LeftOn = "refs/overseer/left-on";

    /// <summary>The ref in each clone at the repository's default branch, as it was when the Sandbox cloned it.</summary>
    private const string DefaultBranch = "refs/remotes/origin/HEAD";

    // Whom commits made in a Sandbox are by, Overseer's and its agents': no one's account, as .invalid is nobody's domain.
    private const string CommitterName = "Overseer";
    private const string CommitterEmail = "overseer@overseer.invalid";

    /// <summary>
    /// Why no new Intent branch is made in a repository without a commit to start it from: GitHub makes the first branch pushed to
    /// an empty repository its default branch.
    /// </summary>
    private const string NothingToBranchFrom =
        "The repository has no commit on its default branch, as when it is empty. The Intent branch would become its default "
        + "branch, and no pull request could be opened. Add a first commit to it on GitHub, such as a README, then Retry.";

    /// <summary>
    /// The start of a script that builds, in an index of its own, what the clone holds now: every file that isn't ignored. The
    /// clone's own index stays as it is.
    /// </summary>
    private const string SnapshotClone =
        """
        scratch=$(mktemp -d)
        trap 'rm -rf "$scratch"' EXIT
        # A copy, with -p keeping its time, which git goes by to tell which files may have changed since it was written.
        cp -p "$(git rev-parse --git-path index)" "$scratch/index"
        export GIT_INDEX_FILE="$scratch/index"
        git add --all
        """;

    /// <summary>
    /// Records what the clone holds besides the work as written by Overseer's commands, to be run in a script of its own, after
    /// them: whatever isn't committed and isn't in the work as it was taken before they ran.
    /// </summary>
    private const string SetAside =
        $$"""
        (
        set -e
        # What Overseer's commands wrote.
        {{SnapshotClone}}
        git -c core.quotePath=true diff-tree -r --name-only --no-renames HEAD {{Work}} > "$scratch/work"
        if [ -s "$scratch/work" ]; then
          git --literal-pathspecs reset --quiet HEAD --pathspec-from-file="$scratch/work"
        fi
        git update-ref {{Left}} "$(git write-tree)"
        git update-ref {{LeftOn}} 'HEAD^{tree}'
        )
        """;

    /// <summary>
    /// Puts a fresh clone, on its default branch, on the Intent branch: the one on GitHub when it exists there, and otherwise a new
    /// one from the default branch, which has to have a commit. What it then holds counts as verified. Commits made in the clone
    /// are by Overseer.
    /// </summary>
    /// <returns>Why it couldn't be put there; <c>null</c> once it is.</returns>
    public async Task<string?> CheckOutAsync(
        Sandbox sandbox, RepositoryName repository, string branch, CancellationToken cancellationToken)
    {
        var script =
            $$"""
            set -e
            if git rev-parse --verify --quiet {{ShellText.Quoted($"refs/remotes/origin/{branch}")}} > /dev/null; then
              git checkout --quiet -b {{ShellText.Quoted(branch)}} --track {{ShellText.Quoted($"origin/{branch}")}}
            elif git rev-parse --verify --quiet HEAD > /dev/null; then
              git checkout --quiet -b {{ShellText.Quoted(branch)}}
            else
              echo {{ShellText.Quoted(NothingToBranchFrom)}}
              exit 1
            fi
            git config user.name {{ShellText.Quoted(CommitterName)}}
            git config user.email {{ShellText.Quoted(CommitterEmail)}}
            git update-ref {{Verified}} HEAD
            # No work yet, and nothing written by a command.
            git update-ref {{Work}} 'HEAD^{tree}'
            git update-ref {{Left}} 'HEAD^{tree}'
            git update-ref {{LeftOn}} 'HEAD^{tree}'
            """;
        var run = await commands.RunAsync(sandbox, repository, script, RepositoryCommands.GitLimit, cancellationToken);
        return run.ErrorOf($"Putting {repository} on the Intent branch {branch}");
    }

    /// <summary>Where the repository's clone is when it isn't on the Intent branch, such as <c>main</c>; <c>null</c> when it is.</summary>
    /// <exception cref="InvalidOperationException">git couldn't say; the message says why.</exception>
    public async Task<string?> OffIntentBranchAsync(
        Sandbox sandbox, RepositoryName repository, string branch, CancellationToken cancellationToken)
    {
        // git symbolic-ref exits with 1 for a detached HEAD, and otherwise fails only when it can't tell.
        var script =
            $$"""
            head=$(git symbolic-ref --quiet HEAD) || [ $? -eq 1 ] && {
              [ "$head" = {{ShellText.Quoted($"refs/heads/{branch}")}} ] || { name=${head#refs/heads/}; echo "${name:-a detached HEAD}"; }
            }
            """;
        var head = (await OutputOfAsync(
            sandbox, repository, script, $"Looking for the branch checked out in {repository}", cancellationToken)).Trim();
        return head.Length == 0 ? null : head;
    }

    /// <summary>
    /// Takes the work in the repository as it is now, which is what <see cref="CommitAsync"/> then commits: what is committed, and
    /// what isn't but is neither ignored nor left as Overseer's commands wrote it. Gives the files in which it differs from the last
    /// verified work, by their paths from the repository's root.
    /// </summary>
    /// <exception cref="InvalidOperationException">git couldn't say; the message says why.</exception>
    public Task<IReadOnlyList<string>> TakeWorkAsync(
        Sandbox sandbox, RepositoryName repository, CancellationToken cancellationToken)
    {
        var script =
            $$"""
            (
            set -e
            # The agent's work, as it is now.
            {{SnapshotClone}}
            # What Overseer's commands wrote, unchanged since, is none of it: back to what is checked out.
            git -c core.quotePath=true diff-tree -r --name-only --no-renames {{Left}} "$(git write-tree)" > "$scratch/changed-since"
            git -c core.quotePath=true diff-tree -r --name-only --no-renames {{LeftOn}} {{Left}} > "$scratch/written"
            # grep exits with 1 when no line is left.
            grep -v -x -F -f "$scratch/changed-since" "$scratch/written" > "$scratch/untouched" || [ $? -eq 1 ]
            if [ -s "$scratch/untouched" ]; then
              git --literal-pathspecs reset --quiet HEAD --pathspec-from-file="$scratch/untouched"
            fi
            git update-ref {{Work}} "$(git write-tree)"
            git diff --name-only --no-renames -z {{Verified}} {{Work}} --
            )
            """;
        return FilesAsync(sandbox, repository, script, $"Looking for changes in {repository}", cancellationToken);
    }

    /// <summary>
    /// Records whatever the repository's clone holds besides what is committed and the work as last taken as written by Overseer's
    /// commands, so that it is no part of any work: run once they have run.
    /// </summary>
    /// <returns>Why it couldn't be recorded; <c>null</c> once it is.</returns>
    public async Task<string?> SetAsideCommandOutputAsync(
        Sandbox sandbox, RepositoryName repository, CancellationToken cancellationToken)
    {
        var run = await commands.RunAsync(sandbox, repository, SetAside, RepositoryCommands.GitLimit, cancellationToken);
        return run.ErrorOf($"Setting aside what Overseer's commands wrote in {repository}");
    }

    /// <summary>
    /// Whether the work in the repository, as last taken, holds anything its default branch doesn't, since the two parted.
    /// </summary>
    /// <exception cref="InvalidOperationException">git couldn't say; the message says why.</exception>
    public async Task<bool> WorkDiffersFromDefaultBranchAsync(
        Sandbox sandbox, RepositoryName repository, CancellationToken cancellationToken)
    {
        // Against the empty tree where the two have no commit in common.
        var files = await FilesAsync(
            sandbox, repository,
            $"""
            base=$(git merge-base {DefaultBranch} HEAD || git hash-object -t tree /dev/null) &&
              git diff --name-only --no-renames -z "$base" {Work} --
            """,
            $"Comparing {repository} with its default branch", cancellationToken);
        return files.Count > 0;
    }

    /// <summary>
    /// Whether the repository's Intent branch on GitHub changes anything of its default branch's, so that there is something for a
    /// pull request: its last verified work differs from the default branch, since the two parted.
    /// </summary>
    /// <exception cref="InvalidOperationException">As for <see cref="OutputOfAsync"/>.</exception>
    private async Task<bool> PushedWorkDiffersFromDefaultBranchAsync(
        Sandbox sandbox, RepositoryName repository, CancellationToken cancellationToken)
    {
        var files = await FilesAsync(
            sandbox, repository,
            $"""
            base=$(git merge-base {DefaultBranch} {Verified} || git hash-object -t tree /dev/null) &&
              git diff --name-only --no-renames -z "$base" {Verified} --
            """,
            $"Comparing the Intent branch of {repository} with its default branch", cancellationToken);
        return files.Count > 0;
    }

    /// <summary>
    /// Which of these repositories have an Intent branch on GitHub that changes their default branch, each to have a pull request,
    /// in their order (<see cref="PushedWorkDiffersFromDefaultBranchAsync"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">git couldn't say of one; the message says why.</exception>
    public async Task<IReadOnlyList<RepositoryName>> ChangingDefaultBranchAsync(
        Sandbox sandbox, IReadOnlyList<RepositoryName> repositories, CancellationToken cancellationToken)
    {
        var changing = new List<RepositoryName>();
        foreach (var repository in repositories)
        {
            if (await PushedWorkDiffersFromDefaultBranchAsync(sandbox, repository, cancellationToken))
            {
                changing.Add(repository);
            }
        }
        return changing;
    }

    /// <summary>The paths a git script prints, each ending in a NUL, so that none needs quoting.</summary>
    /// <exception cref="InvalidOperationException">As for <see cref="OutputOfAsync"/>.</exception>
    private async Task<IReadOnlyList<string>> FilesAsync(
        Sandbox sandbox, RepositoryName repository, string script, string what, CancellationToken cancellationToken) =>
        [.. (await OutputOfAsync(sandbox, repository, script, what, cancellationToken))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct()];

    /// <summary>What a git script prints to standard output.</summary>
    /// <exception cref="InvalidOperationException">The script failed; the message says that <paramref name="what"/> did, and why.</exception>
    private async Task<string> OutputOfAsync(
        Sandbox sandbox, RepositoryName repository, string script, string what, CancellationToken cancellationToken)
    {
        var run = await commands.RunAsync(
            sandbox, repository, ShellText.OutputAlone(script), RepositoryCommands.GitLimit, cancellationToken);
        return run is CommandRun.Succeeded succeeded ? succeeded.Output : throw new InvalidOperationException(run.ErrorOf(what));
    }

    /// <summary>
    /// Commits the work as last taken (<see cref="TakeWorkAsync"/>), so as it was before it was verified, with
    /// <paramref name="message"/>, on the Intent branch, which the clone has to be on: the branch is never moved to where the clone
    /// is instead, as that could drop commits it holds. What was written since, by the commands that verified it, is set aside. The
    /// repository's commit hooks don't run: its build and test have verified the work.
    /// </summary>
    /// <returns>Why it couldn't be committed; <c>null</c> once it is.</returns>
    public async Task<string?> CommitAsync(
        Sandbox sandbox, RepositoryName repository, string branch, string message, CancellationToken cancellationToken)
    {
        var script =
            $$"""
            set -e
            if [ "$(git symbolic-ref --quiet HEAD)" != {{ShellText.Quoted($"refs/heads/{branch}")}} ]; then
              echo {{ShellText.Quoted($"The clone isn't on the Intent branch {branch}.")}}
              exit 1
            fi
            git read-tree {{Work}}
            # A merge left to commit is committed also when it changes nothing.
            if ! git diff --cached --quiet || git rev-parse --verify --quiet MERGE_HEAD > /dev/null; then
              git commit --quiet --no-verify --message {{ShellText.Quoted(message)}}
            fi
            git update-ref {{Work}} 'HEAD^{tree}'
            {{SetAside}}
            """;
        var run = await commands.RunAsync(sandbox, repository, script, RepositoryCommands.GitLimit, cancellationToken);
        return run.ErrorOf($"Committing the work in {repository}");
    }

    /// <summary>
    /// Pushes the Intent branch to GitHub, never by force, after which what it holds counts as verified. Pushing it again changes
    /// nothing.
    /// </summary>
    /// <returns>Why it couldn't be pushed; <c>null</c> once it is.</returns>
    public async Task<PushFailure?> PushAsync(
        Sandbox sandbox, RepositoryName repository, string branch, CancellationToken cancellationToken)
    {
        var reference = ShellText.Quoted($"refs/heads/{branch}");
        var script =
            $"""
            git push --quiet --set-upstream origin {ShellText.Quoted($"refs/heads/{branch}:refs/heads/{branch}")} || exit
            git update-ref {Verified} {reference}
            """;
        var run = await commands.RunAsync(sandbox, repository, script, RepositoryCommands.GitLimit, cancellationToken);
        // git push ends with 1 when what is pushed is refused: by GitHub, as where its branch holds commits the clone's doesn't or
        // a rule forbids it, or by a pre-push hook. It ends otherwise when it doesn't get that far, as when GitHub rejects the
        // credentials or can't be reached.
        return run.ErrorOf($"Pushing the Intent branch {branch} of {repository}") is { } error
            ? new PushFailure(error, Rejected: run is CommandRun.Failed { ExitCode: 1 })
            : null;
    }
}

/// <summary>A push that didn't go through.</summary>
/// <param name="Error">Why, with what GitHub said.</param>
/// <param name="Rejected">
/// Whether what was pushed was refused, so that pushing the same again would be too; otherwise pushing it later can succeed.
/// </param>
public sealed record PushFailure(string Error, bool Rejected);
