using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Sandboxes;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;
using static Overseer.Tests.SpecTests;

namespace Overseer.Tests;

/// <summary>
/// Implementation: when an Intent moves to Implementing, Overseer prepares a Sandbox for the Squad, which the Intent's detail page
/// follows.
/// </summary>
public class ImplementationTests(PostgresServer postgres)
{
    [Fact]
    public async Task Approve_and_start_creates_a_new_Sandbox_the_Intent_owns_with_push_access_and_its_Repositories_in_scope_on_the_machine_chosen_in_Settings()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api", "acme/docs");
        var address = await AwaitingApprovalAsync(overseer, "Export data", "acme/web", "acme/api");
        var refinement = Assert.Single(overseer.Sandboxes.AgentSessionsStarted).Sandbox;
        using var machine = await overseer.SubmitFormAsync("/settings", "sandbox-machine", new Dictionary<string, string>
        {
            ["SandboxVmSize"] = "Standard_D2_v5",
            ["SandboxSpot"] = "true",
        });

        using var approved = await ApproveAsync(overseer, address, start: true);

        var sandbox = await SandboxAsync(overseer);
        Assert.NotEqual(refinement, sandbox);
        Assert.Equal(new SandboxOwner(SandboxOwnerKind.Intent, IdOf(address)), sandbox.Owner);
        Assert.True(overseer.Sandboxes.MayPushOf(sandbox));
        Assert.Equal(["acme/api", "acme/web"], overseer.Sandboxes.RepositoriesInScopeOf(sandbox).Order());
        Assert.Equal(new SandboxMachine("Standard_D2_v5", Spot: true), overseer.Sandboxes.MachineOf(sandbox));
    }

    [Fact]
    public async Task Starting_an_Approved_Intent_prepares_its_Sandbox_too()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var address = await AwaitingApprovalAsync(overseer, "Export data", "acme/web");
        using var approved = await ApproveAsync(overseer, address, start: false);
        Assert.Empty(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));

        using var started = await StartAsync(overseer, address);

        var sandbox = await SandboxAsync(overseer);
        Assert.Equal(new SandboxOwner(SandboxOwnerKind.Intent, IdOf(address)), sandbox.Owner);
        Assert.True(overseer.Sandboxes.MayPushOf(sandbox));
    }

    [Fact]
    public async Task Each_Repository_in_scope_is_put_on_the_Intent_branch_which_has_the_same_name_in_all_of_them_and_stays_the_same()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api");
        var address = await AwaitingApprovalAsync(overseer, "Export data", "acme/web", "acme/api");
        // The first preparation fails after putting acme/api on the branch, so that a Retry puts it there again.
        overseer.Sandboxes.ScriptCommandsContaining("acme/web' || exit", new CommandResult(1, "boom"), new CommandResult(0, ""));
        using var approved = await ApproveAsync(overseer, address, start: true);
        await FailedAsync(overseer, address);

        using var retried = await RetryAsync(overseer, address);

        await ReadyAsync(overseer, address);
        var branch = $"overseer/intent-{IdOf(address):N}";
        Assert.All(["acme/api", "acme/web"], repository =>
        {
            var checkOuts = CommandsIn(overseer, repository).Where(command => command.Contains("git checkout")).ToList();
            Assert.Equal(2, checkOuts.Count);
            Assert.All(checkOuts, checkOut => Assert.Contains($"git checkout --quiet -b '{branch}'", checkOut));
        });
        Assert.Equal(branch, ElementText(await GetAsync(overseer, address), "intent-branch"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_Intent_branch_is_created_from_the_default_branch_or_checked_out_when_it_already_exists(bool exists)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var address = await ImplementingAsync(overseer, "Export data", "acme/web");
        await ReadyAsync(overseer, address);
        var branch = $"overseer/intent-{IdOf(address):N}";
        var checkOut = Assert.Single(CommandsIn(overseer, "acme/web"), command => command.Contains("git checkout"));
        // What GitHub holds: a commit on the default branch and, where an earlier Sandbox pushed it, the Intent branch one ahead.
        var directory = Directory.CreateTempSubdirectory("overseer-intent-branch-").FullName;
        try
        {
            var origin = Path.Combine(directory, "origin");
            var clone = Path.Combine(directory, "clone");
            Git(directory, "init --quiet --initial-branch=trunk origin");
            Git(origin, "commit --quiet --allow-empty -m 'On the default branch'");
            if (exists)
            {
                Git(origin, $"checkout --quiet -b {branch}");
                Git(origin, "commit --quiet --allow-empty -m 'On the Intent branch'");
                Git(origin, "checkout --quiet trunk");
            }
            Git(directory, "clone --quiet origin clone");

            // The command Overseer ran, in this clone rather than the Sandbox's.
            Run(directory, checkOut.Replace(Sandbox.CloneDirectory(new RepositoryName("acme", "web")), clone));

            Assert.Equal(branch, Git(clone, "symbolic-ref --short HEAD"));
            Assert.Equal(exists ? "On the Intent branch" : "On the default branch", Git(clone, "log -1 --format=%s"));
            if (exists)
            {
                Assert.Equal($"origin/{branch}", Git(clone, "rev-parse --abbrev-ref HEAD@{upstream}"));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static string Git(string directory, string arguments) =>
            Run(directory, $"git -c user.name=Test -c user.email=test@example.com -c commit.gpgsign=false {arguments}");
    }

    /// <summary>Runs a shell script in a directory of this machine, which must succeed, and gives what it printed.</summary>
    private static string Run(string directory, string script)
    {
        using var shell = Process.Start(new ProcessStartInfo("bash", ["-c", "exec 2>&1\n" + script])
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
        })!;
        var output = shell.StandardOutput.ReadToEnd();
        shell.WaitForExit();
        Assert.True(shell.ExitCode == 0, output);
        return output.Trim();
    }

    [Fact]
    public async Task For_each_Repository_in_scope_with_a_Repository_Manifest_setup_is_run_and_the_services_of_its_Compose_file_are_started_and_awaited()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api", "acme/docs");
        AddManifest(overseer, "acme/web", "setup: npm ci\nbuild: npm run build\ntest: npm test\ncompose: docker/compose.yml\n");
        AddManifest(overseer, "acme/api", "setup: dotnet restore\nbuild: dotnet build\ntest: dotnet test\n");
        var address = await ImplementingAsync(overseer, "Export data", "acme/web", "acme/api", "acme/docs");

        await ReadyAsync(overseer, address);

        var web = CommandsIn(overseer, "acme/web");
        Assert.Equal(3, web.Count);
        Assert.Contains("npm ci", web[1]);
        Assert.Contains("docker compose --file 'docker/compose.yml' up --detach --wait", web[2]);
        var api = CommandsIn(overseer, "acme/api");
        Assert.Equal(2, api.Count);
        Assert.Contains("dotnet restore", api[1]);
        // A repository without a manifest is only put on the Intent branch: a Task may still create one.
        Assert.Single(CommandsIn(overseer, "acme/docs"));
        // Building and testing come with the Tasks.
        Assert.DoesNotContain(overseer.Sandboxes.CommandsRun, run => run.Command.Contains("npm test") || run.Command.Contains("dotnet build"));
    }

    // Story 96, for implementation.
    [Fact]
    public async Task When_the_Sandbox_cant_be_created_the_Intent_stays_Implementing_in_its_slot_red_with_the_providers_error_and_offers_Retry()
    {
        const string quota = "Operation could not be completed as it results in exceeding approved standardDSv5Family Cores quota.";
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await SetMaxActiveIntentsAsync(overseer, 1);
        var address = await AwaitingApprovalAsync(overseer, "Export data", "acme/web");
        using var other = await SaveAsync(overseer, "Import data", "acme/web");
        overseer.Sandboxes.FailNextCreation(quota);

        using var approved = await ApproveAsync(overseer, address, start: true);

        var failed = await FailedAsync(overseer, address);
        Assert.Equal(
            new ShownImplementation("Failed", $"The Sandbox could not be created: {quota}", ("Red", "Failed"), $"intents/{IdOf(address)}/implementation/retry"),
            failed);
        Assert.Equal("Implementing", ElementText(await GetAsync(overseer, address), "intent-state"));
        Assert.Matches("<span class=\"intent-light\" data-light=\"Red\">\\s*Failed\\s*</span>", await GetAsync(overseer, "/"));
        Assert.Equal(
            "All Active Intent slots are taken, by \"Export data\". You can raise the maximum in Settings.",
            Alert(await GetAsync(overseer, await AddressAsync(overseer, "Import data"))));
        await RefinementTests.NoSandboxLeftAsync(overseer);
    }

    [Theory]
    [InlineData("git checkout", "Putting acme/web on the Intent branch overseer/intent-{0:N} failed with exit code 3:\nboom")]
    [InlineData("npm ci", "The setup command of acme/web failed with exit code 3:\nboom")]
    [InlineData("docker compose", "Starting the Compose services of acme/web failed with exit code 3:\nboom")]
    public async Task A_command_that_fails_turns_the_Intent_red_with_its_output_naming_the_repository_and_the_Sandbox_is_destroyed(
        string command, string error)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api", "acme/web");
        AddManifest(overseer, "acme/web", "setup: npm ci\nbuild: npm run build\ntest: npm test\ncompose: compose.yml\n");
        var address = await AwaitingApprovalAsync(overseer, "Export data", "acme/api", "acme/web");
        // acme/api, which comes first, is fine; so the repository at fault is the second.
        overseer.Sandboxes.ScriptCommandsContaining(command, new CommandResult(3, "boom\n"));
        if (command == "git checkout")
        {
            overseer.Sandboxes.ScriptCommandsContaining("acme/api' || exit", new CommandResult(0, ""));
        }

        using var approved = await ApproveAsync(overseer, address, start: true);

        var failed = await FailedAsync(overseer, address);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, error, IdOf(address)), failed.Status);
        Assert.Equal(("Red", "Failed"), failed.Light);
        Assert.NotNull(failed.RetryAction);
        Assert.Equal("Implementing", ElementText(await GetAsync(overseer, address), "intent-state"));
        await RefinementTests.NoSandboxLeftAsync(overseer);
        // Nothing comes after the step that failed.
        Assert.Contains(command, overseer.Sandboxes.CommandsRun[^1].Command);
    }

    [Fact]
    public async Task An_invalid_Repository_Manifest_turns_the_Intent_red_naming_the_repository_and_what_is_wrong_before_any_setup_runs()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api", "acme/web");
        AddManifest(overseer, "acme/api", "setup: dotnet restore\nbuild: dotnet build\ntest: dotnet test\n");
        AddManifest(overseer, "acme/web", "setup: npm ci\n");

        var address = await ImplementingAsync(overseer, "Export data", "acme/api", "acme/web");

        var failed = await FailedAsync(overseer, address);
        Assert.Equal(
            "The Repository Manifest of acme/web (.overseer.yml) is invalid:\n- `build` is missing.\n- `test` is missing.",
            failed.Status);
        Assert.Equal(("Red", "Failed"), failed.Light);
        Assert.DoesNotContain(overseer.Sandboxes.CommandsRun, run => run.Command.Contains("dotnet restore"));
        await RefinementTests.NoSandboxLeftAsync(overseer);
    }

    [Fact]
    public async Task A_Repository_Manifest_that_cant_be_read_turns_the_Intent_red_naming_the_repository()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.FailReading("/home/overseer/repositories/acme/web/.overseer.yml", "cat: .overseer.yml: Is a directory");

        var address = await ImplementingAsync(overseer, "Export data", "acme/web");

        var failed = await FailedAsync(overseer, address);
        Assert.Equal(
            "The Repository Manifest of acme/web (.overseer.yml) could not be read: cat: .overseer.yml: Is a directory",
            failed.Status);
        await RefinementTests.NoSandboxLeftAsync(overseer);
    }

    [Fact]
    public async Task Of_a_long_output_the_end_is_kept_with_the_error_whatever_characters_it_holds()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        AddManifest(overseer, "acme/web", "setup: npm ci\nbuild: npm run build\ntest: npm test\n");
        var address = await AwaitingApprovalAsync(overseer, "Export data", "acme/web");
        // PostgreSQL stores no NUL character, and the cut falls between the two halves of a character.
        overseer.Sandboxes.ScriptCommandsContaining(
            "npm ci", new CommandResult(1, "The start\n" + string.Concat(Enumerable.Repeat("😀", 4000)) + "\0 the end"));

        using var approved = await ApproveAsync(overseer, address, start: true);

        var failed = await FailedAsync(overseer, address);
        Assert.StartsWith("The setup command of acme/web failed with exit code 1:\n[…]\n😀", failed.Status);
        Assert.EndsWith("😀 the end", failed.Status);
        Assert.DoesNotContain("The start", failed.Status);
        Assert.InRange(failed.Status.Length, 4000, 4100);
    }

    [Theory]
    [InlineData("git checkout", 5, "Putting acme/web on the Intent branch overseer/intent-{0:N} didn't finish within 5 minutes.")]
    [InlineData("npm ci", 30, "The setup command of acme/web didn't finish within 30 minutes.")]
    [InlineData("docker compose", 30, "Starting the Compose services of acme/web didn't finish within 30 minutes.")]
    public async Task A_command_that_hangs_turns_the_Intent_red_once_its_time_limit_passes_and_the_Sandbox_is_destroyed(
        string command, int minutes, string error)
    {
        var time = new FakeTimeProvider();
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        AddManifest(overseer, "acme/web", "setup: npm ci\nbuild: npm run build\ntest: npm test\ncompose: compose.yml\n");
        overseer.Sandboxes.HoldCommandsContaining(command, new TaskCompletionSource().Task);
        var address = await ImplementingAsync(overseer, "Export data", "acme/web");
        await EventuallyAsync(
            () => Task.FromResult(overseer.Sandboxes.CommandsRun.Any(run => run.Command.Contains(command)) ? "" : null), "the command to run");

        time.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));

        Assert.NotEqual("Failed", ImplementationOn(await GetAsync(overseer, address))!.Phase);

        time.Advance(TimeSpan.FromSeconds(1));

        var failed = await FailedAsync(overseer, address);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, error, IdOf(address)), failed.Status);
        Assert.Equal(("Red", "Failed"), failed.Light);
        Assert.NotNull(failed.RetryAction);
        await RefinementTests.NoSandboxLeftAsync(overseer);
    }

    [Fact]
    public async Task Compose_has_a_limit_of_its_own_on_how_long_it_waits_for_the_services()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        AddManifest(overseer, "acme/web", "setup: npm ci\nbuild: npm run build\ntest: npm test\ncompose: compose.yml\n");
        var address = await ImplementingAsync(overseer, "Export data", "acme/web");

        await ReadyAsync(overseer, address);

        // Ten minutes, in seconds: what a service that never gets healthy fails with, naming it, before Overseer gives up on the command.
        Assert.EndsWith("up --detach --wait --wait-timeout 600", CommandsIn(overseer, "acme/web")[^1]);
    }

    // Two projects of one name, as from two docker/compose.yml files or the same top-level name, would replace each other's services.
    [Fact]
    public async Task Repositories_whose_Compose_files_give_the_same_project_name_each_start_their_services_under_a_name_of_their_own()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api.v2", "acme/docs", "acme/web");
        foreach (var repository in new[] { "acme/api.v2", "acme/docs", "acme/web" })
        {
            AddManifest(overseer, repository, "setup: make\nbuild: make\ntest: make test\ncompose: docker/compose.yml\n");
        }
        // What Compose says each project is called, in the order of the repositories: acme/docs's file names itself.
        overseer.Sandboxes.ScriptCommandsContaining(
            "config --format json", ComposeConfig("docker"), ComposeConfig("handbook"), ComposeConfig("docker"));

        var address = await ImplementingAsync(overseer, "Export data", "acme/api.v2", "acme/docs", "acme/web");

        await ReadyAsync(overseer, address);
        Assert.EndsWith(
            "docker compose --file 'docker/compose.yml' --project-name 'docker-acme-api-v2' up --detach --wait --wait-timeout 600",
            // A project name takes no dots.
            CommandsIn(overseer, "acme/api.v2")[^1]);
        Assert.EndsWith(
            "docker compose --file 'docker/compose.yml' --project-name 'docker-acme-web' up --detach --wait --wait-timeout 600",
            CommandsIn(overseer, "acme/web")[^1]);
        // The one no other shares keeps the name Compose gives it, so that docker compose in the repository finds its services.
        Assert.EndsWith(
            "docker compose --file 'docker/compose.yml' up --detach --wait --wait-timeout 600", CommandsIn(overseer, "acme/docs")[^1]);
        // Every project's name is known before any starts, and asked after setup, which may write what the file reads.
        var commands = overseer.Sandboxes.CommandsRun.Select(run => run.Command).ToList();
        Assert.True(commands.FindLastIndex(command => command.Contains("config --format json")) < commands.FindIndex(command => command.Contains(" up --detach")));
        Assert.True(commands.FindLastIndex(command => command.Contains("\nmake\n")) < commands.FindIndex(command => command.Contains("config --format json")));
    }

    [Fact]
    public async Task A_name_of_its_own_is_never_one_that_another_project_has()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api", "acme/docs", "acme/web");
        foreach (var repository in new[] { "acme/api", "acme/docs", "acme/web" })
        {
            AddManifest(overseer, repository, "setup: make\nbuild: make\ntest: make test\ncompose: compose.yml\n");
        }
        overseer.Sandboxes.ScriptCommandsContaining(
            "config --format json", ComposeConfig("shop"), ComposeConfig("shop-acme-api"), ComposeConfig("shop"));

        var address = await ImplementingAsync(overseer, "Export data", "acme/api", "acme/docs", "acme/web");

        await ReadyAsync(overseer, address);
        Assert.Contains("--project-name 'shop-acme-api-2' up", CommandsIn(overseer, "acme/api")[^1]);
        Assert.DoesNotContain("--project-name", CommandsIn(overseer, "acme/docs")[^1]);
        Assert.Contains("--project-name 'shop-acme-web' up", CommandsIn(overseer, "acme/web")[^1]);
    }

    [Theory]
    [InlineData(15, "validating compose.yml: services must be a mapping", "Reading the Compose file of acme/web failed with exit code 15:\nvalidating compose.yml: services must be a mapping")]
    [InlineData(0, "services: {}", "Reading the Compose file of acme/web failed: Compose gave no project name for it.")]
    public async Task A_Compose_file_whose_project_name_cant_be_told_turns_the_Intent_red_naming_the_repository_before_anything_starts(
        int exitCode, string output, string error)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/api", "acme/web");
        AddManifest(overseer, "acme/api", "setup: make\nbuild: make\ntest: make test\ncompose: compose.yml\n");
        AddManifest(overseer, "acme/web", "setup: make\nbuild: make\ntest: make test\ncompose: compose.yml\n");
        overseer.Sandboxes.ScriptCommandsContaining("config --format json", ComposeConfig("api"), new CommandResult(exitCode, output));

        var address = await ImplementingAsync(overseer, "Export data", "acme/api", "acme/web");

        var failed = await FailedAsync(overseer, address);
        Assert.Equal(error, failed.Status);
        Assert.DoesNotContain(overseer.Sandboxes.CommandsRun, run => run.Command.Contains(" up --detach"));
        await RefinementTests.NoSandboxLeftAsync(overseer);
    }

    /// <summary>What <c>docker compose config --format json</c> prints of a project of this name.</summary>
    private static CommandResult ComposeConfig(string name) => new(0, $$"""{ "name": "{{name}}", "services": {} }""");

    // PostgreSQL stores neither a NUL character nor half of a surrogate pair: an error holding one must still turn the Intent red.
    [Fact]
    public async Task A_manifest_error_that_PostgreSQL_cant_store_as_it_is_still_turns_the_Intent_red()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        // A key with a NUL in it, which YAML writes as an escape, and which the error then names.
        AddManifest(overseer, "acme/web", "setup: a\nbuild: b\ntest: c\n\"ex\\0tra\": d\n");

        var address = await ImplementingAsync(overseer, "Export data", "acme/web");

        var failed = await FailedAsync(overseer, address);
        Assert.Equal(
            "The Repository Manifest of acme/web (.overseer.yml) is invalid:\n"
            + "- `extra` is not part of a Repository Manifest, which holds setup, build, test and, optionally, compose.",
            failed.Status);
    }

    [Fact]
    public async Task An_exceptions_message_that_PostgreSQL_cant_store_as_it_is_still_turns_the_Intent_red()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var address = await AwaitingApprovalAsync(overseer, "Export data", "acme/web");
        overseer.Sandboxes.FailNextCreation("Quota\0 exceeded \uD800.");

        using var approved = await ApproveAsync(overseer, address, start: true);

        Assert.Equal("The Sandbox could not be created: Quota exceeded �.", (await FailedAsync(overseer, address)).Status);
        // And what comes from reading a file.
        overseer.Sandboxes.FailReading("/home/overseer/repositories/acme/web/.overseer.yml", "cat: \0broken");
        using var retried = await RetryAsync(overseer, address);
        await EventuallyAsync(
            async () => ImplementationOn(await GetAsync(overseer, address)) is { Phase: "Failed", Status: var status }
                && status == "The Repository Manifest of acme/web (.overseer.yml) could not be read: cat: broken" ? status : null,
            "the implementation to fail reading the manifest");
    }

    [Fact]
    public async Task A_commands_output_never_puts_the_GitHub_token_in_the_error_or_the_database()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        AddManifest(overseer, "acme/web", "setup: npm ci\nbuild: npm run build\ntest: npm test\n");
        var address = await AwaitingApprovalAsync(overseer, "Export data", "acme/web");
        overseer.Sandboxes.ScriptCommandsContaining("npm ci", new CommandResult(1, $"Authorization: Bearer {Token} was refused"));

        using var approved = await ApproveAsync(overseer, address, start: true);

        var failed = await FailedAsync(overseer, address);
        Assert.Equal("The setup command of acme/web failed with exit code 1:\nAuthorization: Bearer *** was refused", failed.Status);
        Assert.DoesNotContain(await overseer.DumpDatabaseAsync(), table => table.Contains(Token));
    }

    [Fact]
    public async Task Retry_prepares_again_in_a_fresh_Sandbox_and_the_failed_one_is_destroyed()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        AddManifest(overseer, "acme/web", "setup: npm ci\nbuild: npm run build\ntest: npm test\n");
        var address = await AwaitingApprovalAsync(overseer, "Export data", "acme/web");
        overseer.Sandboxes.ScriptCommandsContaining("npm ci", new CommandResult(1, "The registry is down."), new CommandResult(0, ""));
        // Destroying the failed Sandbox fails too, so it is still there when the user retries.
        overseer.Sandboxes.FailNextDestruction("Azure could not be reached.");
        using var approved = await ApproveAsync(overseer, address, start: true);
        await FailedAsync(overseer, address);
        var failed = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));

        using var retried = await RetryAsync(overseer, address);

        // Back on the detail page, so reloading it doesn't retry again.
        Assert.Equal(address, retried.FinalPath());
        var ready = await ReadyAsync(overseer, address);
        Assert.Equal(
            new ShownImplementation("Ready", "The Intent is ready: its Sandbox is prepared for the Squad.", ("Green", "Working"), null),
            ready);
        var fresh = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(failed, fresh);
        Assert.Equal(failed.Owner, fresh.Owner);
        Assert.True(overseer.Sandboxes.MayPushOf(fresh));
        await using var database = overseer.OpenDatabase();
        Assert.Equal(
            [fresh.ResourceId],
            await database.Sandboxes.Where(sandbox => sandbox.DestroyedAt == null).Select(sandbox => sandbox.ResourceId)
                .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Retry_does_nothing_to_an_implementation_that_hasnt_failed()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var address = await AwaitingApprovalAsync(overseer, "Export data", "acme/web");
        overseer.Sandboxes.FailNextCreation("QuotaExceeded");
        using var approved = await ApproveAsync(overseer, address, start: true);
        await FailedAsync(overseer, address);
        var opened = await overseer.OpenAsync(address);
        using var retried = await RetryAsync(overseer, address);
        await ReadyAsync(overseer, address);
        var prepared = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));

        // As from a page opened while it had failed.
        using var again = await opened.PostAsync($"{address}/implementation/retry");

        Assert.Equal("Ready", ImplementationOn(await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.Phase);
        Assert.Equal([prepared], await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_restart_during_provisioning_leaves_the_implementation_failed_with_a_Retry_that_works_and_destroys_the_Sandbox_it_was_provisioning()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        string interrupted;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"], new FakeTimeProvider()))
        {
            address = await AwaitingApprovalAsync(first, "Export data", "acme/web");
            // The clock never moves, so the Sandbox is still being provisioned.
            first.Sandboxes.ProvisioningDelay = TimeSpan.FromMinutes(4);
            using var approved = await ApproveAsync(first, address, start: true);
            interrupted = await EventuallyAsync(async () =>
            {
                await using var database = first.OpenDatabase();
                return (await database.Sandboxes.SingleOrDefaultAsync(sandbox => sandbox.DestroyedAt == null, TestContext.Current.CancellationToken))?.ResourceId;
            }, "the Sandbox to be recorded");
        }

        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        // Failed from the moment the Portal answers.
        var failed = ImplementationOn(await GetAsync(second, address));
        Assert.Equal(Interrupted(address), failed);
        await RefinementTests.NoSandboxLeftAsync(second);

        using var retried = await RetryAsync(second, address);

        await ReadyAsync(second, address);
        var fresh = Assert.Single(await second.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(interrupted, fresh.ResourceId);
    }

    [Fact]
    public async Task A_Retry_while_the_restart_is_still_destroying_the_old_Sandbox_waits_for_that_rather_than_destroy_it_too()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        Sandbox old;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            address = await ImplementingAsync(first, "Export data", "acme/web");
            await ReadyAsync(first, address);
            old = Assert.Single(await first.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        }
        var destroying = new TaskCompletionSource();
        await using var second = await StartWithRepositoriesAsync(connectionString, [], sandboxes: sandboxes => sandboxes.HoldDestructions(destroying.Task));
        await EventuallyAsync(() => Task.FromResult(second.Sandboxes.DestructionsBegun.Count > 0 ? "" : null), "the restart to destroy the old Sandbox");

        using var retried = await RetryAsync(second, address);

        // Nothing announces that the Retry has got as far as the old Sandbox, so this gives it the time to have.
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal([old], second.Sandboxes.DestructionsBegun);
        Assert.Empty(await second.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("ProvisioningSandbox", ImplementationOn(await GetAsync(second, address))!.Phase);

        destroying.SetResult();

        await ReadyAsync(second, address);
        Assert.Equal([old], second.Sandboxes.DestructionsBegun);
        Assert.NotEqual(old, Assert.Single(await second.Sandboxes.ListAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_Retry_that_cant_destroy_the_failed_Sandbox_creates_no_other_and_offers_Retry_again()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var address = await AwaitingApprovalAsync(overseer, "Export data", "acme/web");
        overseer.Sandboxes.ScriptCommandsContaining("git checkout", new CommandResult(1, "boom"), new CommandResult(0, ""));
        // Destroying it fails as the implementation fails, and again on the first Retry.
        overseer.Sandboxes.FailNextDestruction("Azure could not be reached.");
        overseer.Sandboxes.FailNextDestruction("The resource group is locked.");
        using var approved = await ApproveAsync(overseer, address, start: true);
        await FailedAsync(overseer, address);
        var failed = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));

        using var retried = await RetryAsync(overseer, address);

        await EventuallyAsync(
            async () => ImplementationOn(await GetAsync(overseer, address)) is { Phase: "Failed", Status: var status }
                && status == "The failed Sandbox could not be destroyed, so no new one was created: The resource group is locked." ? status : null,
            "the Retry to fail");
        Assert.Equal([failed], await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));

        using var again = await RetryAsync(overseer, address);

        await ReadyAsync(overseer, address);
        Assert.NotEqual(failed, Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Stopping_serve_during_setup_destroys_the_Sandbox_and_leaves_the_implementation_failed_after_the_restart()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        FakeSandboxProvider firstSandboxes;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            firstSandboxes = first.Sandboxes;
            AddManifest(first, "acme/web", "setup: npm ci\nbuild: npm run build\ntest: npm test\n");
            first.Sandboxes.HoldCommandsContaining("npm ci", new TaskCompletionSource().Task);
            address = await ImplementingAsync(first, "Export data", "acme/web");
            await InPhaseAsync(first, address, "RunningSetup");
        }
        Assert.Empty(await firstSandboxes.ListAsync(TestContext.Current.CancellationToken));

        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        Assert.Equal(Interrupted(address), ImplementationOn(await GetAsync(second, address)));
        Assert.Empty(second.Sandboxes.CommandsRun);
    }

    [Fact]
    public async Task After_a_restart_a_prepared_implementation_is_failed_too_as_its_Sandbox_goes()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            address = await ImplementingAsync(first, "Export data", "acme/web");
            await ReadyAsync(first, address);
        }

        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        Assert.Equal(Interrupted(address), ImplementationOn(await GetAsync(second, address)));
        await RefinementTests.NoSandboxLeftAsync(second);
    }

    [Fact]
    public async Task An_Intent_that_was_Implementing_before_Overseer_prepared_Sandboxes_for_it_is_failed_with_a_Retry_when_serve_starts()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            address = await AwaitingApprovalAsync(first, "Export data", "acme/web");
            using var approved = await ApproveAsync(first, address, start: false);
            // As an earlier version left it: Implementing, without anything of an implementation.
            await SetStateAsync(first, "Export data", IntentState.Implementing);
        }

        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        Assert.Equal(Interrupted(address), ImplementationOn(await GetAsync(second, address)));
        using var retried = await RetryAsync(second, address);
        await ReadyAsync(second, address);
        Assert.True(second.Sandboxes.MayPushOf(Assert.Single(await second.Sandboxes.ListAsync(TestContext.Current.CancellationToken))));
    }

    [Fact]
    public async Task A_restart_leaves_a_failed_implementation_failed_with_its_error()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            address = await AwaitingApprovalAsync(first, "Export data", "acme/web");
            first.Sandboxes.FailNextCreation("QuotaExceeded");
            using var approved = await ApproveAsync(first, address, start: true);
            await FailedAsync(first, address);
        }

        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        Assert.Equal("The Sandbox could not be created: QuotaExceeded", ImplementationOn(await GetAsync(second, address))!.Status);
        Assert.Empty(await second.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>An implementation that a restart of serve interrupted, as its Intent's detail page shows it.</summary>
    private static ShownImplementation Interrupted(string address) => new(
        "Failed", "Overseer restarted, which interrupted the implementation. Retry to prepare a fresh Sandbox.", ("Red", "Failed"),
        $"intents/{IdOf(address)}/implementation/retry");

    /// <summary>The Repository Manifest the repository has in every Sandbox that holds it from now on.</summary>
    private static void AddManifest(RunningOverseer overseer, string repository, string manifest) =>
        overseer.Sandboxes.AddRepositoryFile(repository, ".overseer.yml", manifest);

    /// <summary>
    /// The commands run in the repository's directory in a Sandbox, in order, but for setting aside what they wrote, which ends
    /// the preparation.
    /// </summary>
    private static IReadOnlyList<string> CommandsIn(RunningOverseer overseer, string repository) =>
        [.. overseer.Sandboxes.CommandsRun
            .Where(run => run.Command.StartsWith($"cd '/home/overseer/repositories/{repository}' ", StringComparison.Ordinal))
            .Select(run => run.Command)
            .Where(command => !command.Contains("# What Overseer's commands wrote.", StringComparison.Ordinal))];

    /// <summary>Has an Intent with these Repositories in scope approved and started, and gives its address.</summary>
    internal static async Task<string> ImplementingAsync(RunningOverseer overseer, string text, params string[] repositories)
    {
        var address = await AwaitingApprovalAsync(overseer, text, repositories);
        using var approved = await ApproveAsync(overseer, address, start: true);
        return address;
    }

    /// <summary>Waits for the Intent's detail page to show its Sandbox prepared.</summary>
    internal static Task<ShownImplementation> ReadyAsync(RunningOverseer overseer, string address) => InPhaseAsync(overseer, address, "Ready");

    /// <summary>Waits for the Intent's detail page to show its implementation failed, with its light red.</summary>
    internal static Task<ShownImplementation> FailedAsync(RunningOverseer overseer, string address) =>
        EventuallyAsync(
            // Both: the page reads the light a moment before where the implementation stands.
            async () => ImplementationOn(await GetAsync(overseer, address)) is { Phase: "Failed", Light.Colour: "Red" } shown ? shown : null,
            "the implementation to fail");

    internal static Task<ShownImplementation> InPhaseAsync(RunningOverseer overseer, string address, string phase) =>
        EventuallyAsync(
            async () => ImplementationOn(await GetAsync(overseer, address)) is { } shown && shown.Phase == phase ? shown : null,
            $"the implementation to be {phase}");

    /// <summary>Presses the Retry button on the Intent's detail page, as a browser would.</summary>
    internal static Task<HttpResponseMessage> RetryAsync(RunningOverseer overseer, string address) => RefinementTests.RetryAsync(overseer, address);

    /// <summary>Where the Intent's implementation stands as its detail page shows it, with its light; <c>null</c> when the page shows none.</summary>
    internal static ShownImplementation? ImplementationOn(string page)
    {
        var implementation = Regex.Match(page, "<section class=\"implementation\" data-phase=\"([^\"]+)\"[^>]*>(.*?)</section>", RegexOptions.Singleline);
        if (!implementation.Success)
        {
            return null;
        }
        var retry = Regex.Match(implementation.Groups[2].Value, "<form[^>]*action=\"([^\"]*retry)\"", RegexOptions.Singleline);
        return new ShownImplementation(
            implementation.Groups[1].Value,
            ElementText(implementation.Groups[2].Value, "implementation-status"),
            LightOn(page),
            retry.Success ? WebUtility.HtmlDecode(retry.Groups[1].Value) : null);
    }

    /// <summary>The Intent's implementation as its detail page shows it.</summary>
    /// <param name="Status">What the page says about it, such as the error it failed with.</param>
    /// <param name="Light">The colour and label of the Intent's light; <c>null</c> when it shows none.</param>
    /// <param name="RetryAction">Where its Retry button posts to; <c>null</c> when it has none.</param>
    internal sealed record ShownImplementation(string Phase, string Status, (string Colour, string Label)? Light, string? RetryAction);

    /// <summary>
    /// Captures an Intent with these Repositories in scope, one of them acme/web, and has the Analyst hand in its Spec; gives its
    /// address once it awaits approval and the Analyst's Sandbox is gone.
    /// </summary>
    internal static Task<string> AwaitingApprovalAsync(RunningOverseer overseer, string text, params string[] repositories) =>
        AwaitingApprovalAsync(overseer, Spec(), text, repositories);

    /// <summary>As <see cref="AwaitingApprovalAsync(RunningOverseer, string, string[])"/>, with this Spec, which suits the repositories.</summary>
    internal static async Task<string> AwaitingApprovalAsync(RunningOverseer overseer, JsonObject spec, string text, params string[] repositories)
    {
        // Any Intent started afterwards keeps refining.
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(spec)), new FakeAgentSession(FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, text, repositories);
        var address = await AddressAsync(overseer, text);
        using var started = await StartAsync(overseer, address);
        await AwaitingSpecApprovalAsync(overseer, address);
        await RefinementTests.NoSandboxLeftAsync(overseer);
        return address;
    }

    /// <summary>Approves version 1 of the Intent's Spec, and starts its implementation too when <paramref name="start"/>.</summary>
    internal static Task<HttpResponseMessage> ApproveAsync(RunningOverseer overseer, string address, bool start) =>
        overseer.SubmitFormAsync(address, "approve-spec",
            [new("SpecVersion", 1.ToString(CultureInfo.InvariantCulture)), .. start ? [new KeyValuePair<string, string>("Start", "true")] : Array.Empty<KeyValuePair<string, string>>()]);

    /// <summary>Waits for the one Sandbox there is.</summary>
    private static Task<Sandbox> SandboxAsync(RunningOverseer overseer) =>
        EventuallyAsync(async () => (await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken)).SingleOrDefault(), "the Sandbox");

    private static Guid IdOf(string address) => Guid.Parse(address["/intents/".Length..]);
}
