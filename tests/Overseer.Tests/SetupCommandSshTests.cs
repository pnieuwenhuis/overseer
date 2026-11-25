using Overseer.Sandboxes;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;

namespace Overseer.Tests;

/// <summary>The command Overseer runs a repository's <c>setup</c> with, run over SSH against a real OpenSSH server standing in for a Sandbox.</summary>
public class SetupCommandSshTests(PostgresServer postgres, OpenSshServer server) : IClassFixture<OpenSshServer>
{
    [Fact]
    public async Task A_setup_that_leaves_a_process_running_in_the_background_ends_when_its_script_does_with_its_output()
    {
        var result = await RunSetupAsync("sleep 3101 &\necho started\necho warned >&2");

        Assert.Equal(new CommandResult(0, "started\nwarned\n"), result);
        // The process it left runs on.
        var running = await new SshNetSandboxSsh().RunAsync(server.Target, "pgrep -f '[s]leep 3101'", TestContext.Current.CancellationToken);
        Assert.Equal(0, running.ExitCode);
    }

    [Fact]
    public async Task A_setup_stops_at_its_first_failing_line_with_its_exit_code_and_output()
    {
        var result = await RunSetupAsync("sleep 3102 &\necho \"it's $((1 + 1))\"\n(exit 7)\necho never");

        Assert.Equal(new CommandResult(7, "it's 2\n"), result);
    }

    [Fact]
    public async Task A_setup_reads_nothing_so_that_nothing_waits_for_an_answer()
    {
        var result = await RunSetupAsync("read answer || echo nothing to read");

        Assert.Equal(new CommandResult(0, "nothing to read\n"), result);
    }

    /// <summary>
    /// Has Overseer prepare a repository whose manifest has this <c>setup</c>, and runs the command it ran for it, from <c>/tmp</c>
    /// instead of the repository's clone.
    /// </summary>
    private async Task<CommandResult> RunSetupAsync(string setup)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var manifest = $"setup: |\n{string.Concat(setup.Split('\n').Select(line => $"  {line}\n"))}build: make\ntest: make test\n";
        overseer.Sandboxes.AddRepositoryFile("acme/web", ".overseer.yml", manifest);
        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");
        await ImplementationTests.ReadyAsync(overseer, address);
        // After the one that puts the repository on the Intent branch.
        var command = overseer.Sandboxes.CommandsRun[1].Command;
        var clone = "/home/overseer/repositories/acme/web";
        Assert.Contains(clone, command);

        // A command that waits for what it left running would never come back.
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        patience.CancelAfter(TimeSpan.FromSeconds(30));
        return await new SshNetSandboxSsh().RunAsync(server.Target, command.Replace(clone, "/tmp"), patience.Token);
    }
}
