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
    public async Task In_an_empty_repository_every_file_is_changed()
    {
        var commands = await CommandsOverseerRunsAsync();
        var clone = Path.Combine(_directory, "clone");
        Git(_directory, "init --quiet --bare --initial-branch=trunk origin.git");
        Git(_directory, "clone --quiet origin.git clone");
        Run(clone, commands.CheckOut);
        Write(clone, "README.md", "# New");

        Assert.Equal(["README.md"], ChangedFiles(clone, commands));
        Run(clone, commands.Commit);
        Assert.Equal(["README.md"], ChangedFiles(clone, commands));
        Run(clone, commands.Push);
        Assert.Empty(ChangedFiles(clone, commands));
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
        overseer.Sandboxes.ScriptCommandsContaining("git ls-files", new CommandResult(0, "a.txt\0"));
        var spec = SpecTests.Spec();
        spec["tasks"] = new System.Text.Json.Nodes.JsonArray(SpecTests.TaskJson(Title, "Every order as CSV.", ["acme/web"]));
        var address = await ImplementationTests.AwaitingApprovalAsync(overseer, spec, "Export data", "acme/web");
        using var approved = await ImplementationTests.ApproveAsync(overseer, address, start: true);
        await TaskTests.StatesAsync(overseer, address, "Done");

        var clone = Path.Combine(_directory, "clone");
        string Command(string holding) =>
            Assert.Single(overseer.Sandboxes.CommandsRun, run => run.Command.Contains(holding)).Command
                .Replace(Sandbox.CloneDirectory(new RepositoryName("acme", "web")), clone);
        return new Commands(
            $"overseer/intent-{address["/intents/".Length..].Replace("-", "")}",
            Command("git config user.name"), Command("git ls-files"), Command("git commit"), Command("git push"));
    }

    private IReadOnlyList<string> ChangedFiles(string clone, Commands commands) =>
        Run(clone, commands.ChangedFiles).Split('\0', StringSplitOptions.RemoveEmptyEntries);

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
    private sealed record Commands(string Branch, string CheckOut, string ChangedFiles, string Commit, string Push);
}
