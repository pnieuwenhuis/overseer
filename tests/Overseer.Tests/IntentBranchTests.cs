using System.Diagnostics;
using Overseer.GitHub;
using Overseer.Sandboxes;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;

namespace Overseer.Tests;

/// <summary>
/// The git commands Overseer runs on the Intent branch in a Sandbox, run against real local git: what it takes as changed, and
/// what it commits and pushes.
/// </summary>
public sealed class IntentBranchTests(PostgresServer postgres) : IDisposable
{
    private const string Title = "Add the \"orders\" export: it's $(not) run";

    private readonly string _directory = Directory.CreateTempSubdirectory("overseer-intent-branch-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task What_changed_is_whatever_differs_from_the_last_verified_work_committed_uncommitted_or_untracked_but_not_ignored()
    {
        var commands = await CommandsOverseerRunsAsync();
        var clone = Clone();
        Run(clone, commands.CheckOut);
        Assert.Empty(ChangedFiles(clone, commands));

        Write(clone, "committed.txt", "by the agent");
        Git(clone, "add committed.txt");
        Git(clone, "commit --quiet -m 'By the agent'");
        // Pushed too, though an agent is told not to: it still isn't verified.
        Git(clone, $"push --quiet origin {commands.Branch}");
        Write(clone, "tracked.txt", "edited\n");
        File.Delete(Path.Combine(clone, "deleted.txt"));
        Write(clone, ".overseer.yml", "build: make\n");
        Write(clone, "new folder/it's \"new\" ü.txt", "untracked");
        Write(clone, "ignored.log", "noise");
        // Makes git warn, on standard error, about the file it compares.
        Git(clone, "config core.autocrlf true");

        Assert.Equal(
            [".overseer.yml", "committed.txt", "deleted.txt", "new folder/it's \"new\" ü.txt", "tracked.txt"],
            ChangedFiles(clone, commands).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Committing_and_pushing_puts_everything_on_the_Intent_branch_on_GitHub_under_Overseers_name_and_it_then_counts_as_verified()
    {
        var commands = await CommandsOverseerRunsAsync();
        var clone = Clone();
        Run(clone, commands.CheckOut);
        // The agent commits some of its work, as itself, and leaves the Intent branch.
        Write(clone, "committed.txt", "by the agent");
        Git(clone, "add committed.txt");
        Run(clone, "git commit --quiet -m 'By the agent'");
        Git(clone, "checkout --quiet --detach");
        Write(clone, "tracked.txt", "edited\n");
        File.Delete(Path.Combine(clone, "deleted.txt"));
        Write(clone, "untracked.txt", "new");
        // A hook that refuses every commit: build and test are what verify the work.
        var hook = Path.Combine(clone, ".git", "hooks", "pre-commit");
        Write(clone, ".git/hooks/pre-commit", "#!/bin/sh\nexit 1\n");
        Run(clone, $"chmod +x '{hook}'");

        Run(clone, commands.Commit);

        Assert.Equal(commands.Branch, Git(clone, "symbolic-ref --short HEAD"));
        Assert.Empty(Git(clone, "status --porcelain"));
        Assert.Equal($"{Title}\nBy the agent\nOn the default branch", Git(clone, "log --format=%s"));
        Assert.Equal(
            "Overseer <overseer@overseer.invalid> Overseer <overseer@overseer.invalid>",
            Git(clone, "log -1 --format='%an <%ae> %cn <%ce>'"));
        // The agent's own commit is by the same identity, which it needs to commit at all.
        Assert.Equal("Overseer <overseer@overseer.invalid>", Git(clone, "log -1 --skip=1 --format='%an <%ae>'"));
        // Not verified before GitHub has it.
        Assert.NotEmpty(ChangedFiles(clone, commands));

        Run(clone, commands.Push);

        var origin = Path.Combine(_directory, "origin.git");
        Assert.Equal(Git(clone, "rev-parse HEAD"), Git(origin, $"rev-parse refs/heads/{commands.Branch}"));
        Assert.Equal($"origin/{commands.Branch}", Git(clone, "rev-parse --abbrev-ref HEAD@{upstream}"));
        Assert.Empty(ChangedFiles(clone, commands));
        // Pushing and committing again, as a Retry may, changes nothing.
        var head = Git(clone, "rev-parse HEAD");
        Run(clone, commands.Commit);
        Run(clone, commands.Push);
        Assert.Equal(head, Git(clone, "rev-parse HEAD"));
    }

    [Fact]
    public async Task A_push_GitHub_refuses_leaves_the_work_unverified_and_says_why()
    {
        var commands = await CommandsOverseerRunsAsync();
        var clone = Clone();
        Run(clone, commands.CheckOut);
        Write(clone, "untracked.txt", "new");
        Run(clone, commands.Commit);
        var hook = Path.Combine(_directory, "origin.git", "hooks", "pre-receive");
        File.WriteAllText(hook, "#!/bin/sh\necho 'refused by the rules of this repository'\nexit 1\n");
        Run(clone, $"chmod +x '{hook}'");

        var (exitCode, output) = Execute(clone, commands.Push);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("refused by the rules of this repository", output);
        Assert.Equal(["untracked.txt"], ChangedFiles(clone, commands));
    }

    [Fact]
    public async Task An_empty_repository_is_refused_before_anything_can_be_pushed_to_it_saying_what_to_do()
    {
        var commands = await CommandsOverseerRunsAsync();
        var clone = Path.Combine(_directory, "clone");
        Git(_directory, "init --quiet --bare --initial-branch=trunk origin.git");
        Git(_directory, "clone --quiet origin.git clone");

        var (exitCode, output) = Execute(clone, commands.CheckOut);

        // Pushed first, the Intent branch would become the default branch on GitHub, with nothing to open a pull request into.
        Assert.NotEqual(0, exitCode);
        Assert.Equal(
            "The repository has no commit on its default branch, as when it is empty. The Intent branch would become its default "
            + "branch, and no pull request could be opened. Add a first commit to it on GitHub, such as a README, then Retry.",
            output.Trim());
    }

    [Fact]
    public async Task The_Intent_branch_changes_the_default_branch_once_work_it_doesnt_have_is_pushed_whatever_the_default_branch_gains()
    {
        var commands = await CommandsOverseerRunsAsync();
        var clone = Clone();
        Run(clone, commands.CheckOut);
        // The default branch moves on, which changes nothing of the Intent's.
        var seed = Path.Combine(_directory, "seed");
        Write(seed, "elsewhere.txt", "by someone else");
        Git(seed, "add --all");
        Git(seed, "commit --quiet -m 'By someone else'");
        Git(seed, $"push --quiet '{Path.Combine(_directory, "origin.git")}' trunk");
        Git(clone, "fetch --quiet origin");
        Assert.Empty(Files(clone, commands.Differs));
        Assert.Empty(Files(clone, commands.IntentBranchChanges));

        Write(clone, "untracked.txt", "new");
        Write(clone, "ignored.log", "noise");
        File.Delete(Path.Combine(clone, "deleted.txt"));

        Assert.Equal(["deleted.txt", "untracked.txt"], Files(clone, commands.Differs).Order(StringComparer.Ordinal));
        // Not on GitHub yet.
        Assert.Empty(Files(clone, commands.IntentBranchChanges));

        Run(clone, commands.Commit);
        Run(clone, commands.Push);
        // As a build leaves behind where .gitignore doesn't cover it: no part of the Intent branch.
        Write(clone, "build.out", "built");

        Assert.Equal(["deleted.txt", "untracked.txt"], Files(clone, commands.IntentBranchChanges).Order(StringComparer.Ordinal));

        // Taken back again: nothing is left for a pull request, though the branch has commits of its own.
        File.Delete(Path.Combine(clone, "build.out"));
        File.Delete(Path.Combine(clone, "untracked.txt"));
        Git(clone, "checkout --quiet origin/trunk -- deleted.txt");
        Assert.Empty(Files(clone, commands.Differs));
        Run(clone, commands.Commit);
        Run(clone, commands.Push);
        Assert.Empty(Files(clone, commands.IntentBranchChanges));
    }

    /// <summary>
    /// A clone, on its default branch, of a repository on "GitHub" holding one commit, with no git identity of its own.
    /// </summary>
    private string Clone()
    {
        var seed = Path.Combine(_directory, "seed");
        Git(_directory, "init --quiet --initial-branch=trunk seed");
        Write(seed, "tracked.txt", "as it was\n");
        Write(seed, "deleted.txt", "to go");
        Write(seed, ".gitignore", "*.log\n");
        Git(seed, "add --all");
        Git(seed, "commit --quiet -m 'On the default branch'");
        Git(_directory, "clone --quiet --bare seed origin.git");
        Git(_directory, "clone --quiet origin.git clone");
        return Path.Combine(_directory, "clone");
    }

    /// <summary>The commands Overseer runs on a repository for one Task that changed it, as they would run in a local clone of it.</summary>
    private async Task<Commands> CommandsOverseerRunsAsync()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done())));
        const string changedFilesAsked = "base=$(git rev-parse --verify --quiet refs/overseer/verified";
        overseer.Sandboxes.ScriptCommandsContaining(changedFilesAsked, new CommandResult(0, "a.txt\0"));
        var spec = SpecTests.Spec();
        spec["tasks"] = new System.Text.Json.Nodes.JsonArray(SpecTests.TaskJson(Title, "Every order as CSV.", ["acme/web"]));
        var address = await ImplementationTests.AwaitingApprovalAsync(overseer, spec, "Export data", "acme/web");
        using var approved = await ImplementationTests.ApproveAsync(overseer, address, start: true);
        // Once the final verification has compared the repository, which has no manifest here, with its default branch.
        await FinalVerificationTests.InReviewAsync(overseer, address);

        var clone = Path.Combine(_directory, "clone");
        string Command(string holding) =>
            Assert.Single(overseer.Sandboxes.CommandsRun, run => run.Command.Contains(holding)).Command
                .Replace(Sandbox.CloneDirectory(new RepositoryName("acme", "web")), clone);
        return new Commands(
            $"overseer/intent-{address["/intents/".Length..].Replace("-", "")}",
            Command("git config user.name"), Command(changedFilesAsked), Command("git commit"), Command("git push"),
            Command("base=$(git merge-base refs/remotes/origin/HEAD HEAD"), Command("if git rev-parse --verify --quiet refs/overseer/verified"));
    }

    private IReadOnlyList<string> ChangedFiles(string clone, Commands commands) => Files(clone, commands.ChangedFiles);

    /// <summary>The files a command that asks git for some prints.</summary>
    private IReadOnlyList<string> Files(string clone, string command) =>
        Run(clone, command).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static void Write(string directory, string path, string content)
    {
        var file = Path.Combine(directory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    /// <summary>Runs git as the test, which has an identity of its own.</summary>
    private string Git(string directory, string arguments) =>
        Run(directory, $"git -c user.name=Test -c user.email=test@example.com -c commit.gpgsign=false {arguments}");

    /// <summary>Runs a shell script in a directory of this machine, which must succeed, and gives what it printed.</summary>
    private string Run(string directory, string script)
    {
        var (exitCode, output) = Execute(directory, script);
        Assert.True(exitCode == 0, output);
        return output.Trim();
    }

    /// <summary>
    /// Runs a shell script as a Sandbox does, with standard error and output together, and with none of this machine's git
    /// configuration, such as an identity.
    /// </summary>
    private (int ExitCode, string Output) Execute(string directory, string script)
    {
        var start = new ProcessStartInfo("bash", ["-c", "exec 2>&1\n" + script])
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
        };
        start.Environment["HOME"] = _directory;
        start.Environment.Remove("XDG_CONFIG_HOME");
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        foreach (var identity in (string[])["GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", "EMAIL"])
        {
            start.Environment.Remove(identity);
        }
        using var shell = Process.Start(start)!;
        var output = shell.StandardOutput.ReadToEnd();
        shell.WaitForExit();
        return (shell.ExitCode, output);
    }

    /// <param name="Branch">The Intent branch.</param>
    /// <param name="Differs">Asks what the clone holds differently from the default branch.</param>
    /// <param name="IntentBranchChanges">Asks what the Intent branch changes of the default branch.</param>
    private sealed record Commands(
        string Branch, string CheckOut, string ChangedFiles, string Commit, string Push, string Differs, string IntentBranchChanges);
}
