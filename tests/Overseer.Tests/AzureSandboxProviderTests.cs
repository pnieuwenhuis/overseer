using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using Overseer.Sandboxes;
using Overseer.Secrets;

namespace Overseer.Tests;

/// <summary>The Azure Sandbox Provider against a fake <c>az</c> and a fake SSH, so without Azure or VMs.</summary>
public sealed class AzureSandboxProviderTests : IDisposable
{
    private const string Subscription = "0b0c0d0e-1f2a-4b3c-8d4e-5f6a7b8c9d0e";
    private const string ResourceGroup = "overseer-sandboxes-rg";
    private static readonly IPAddress DeveloperAddress = IPAddress.Parse("203.0.113.5");
    private const string SandboxAddress = "198.51.100.7";
    private const string PrivateSandboxAddress = "10.41.0.4";

    private readonly FakeAzureCli _az = new();
    private readonly FakeSandboxSsh _ssh = new();
    private readonly string _directory = Directory.CreateTempSubdirectory("overseer-azure-sandboxes-").FullName;
    private readonly FileSecretStore _secrets;

    public AzureSandboxProviderTests()
    {
        _secrets = new FileSecretStore(Path.Combine(_directory, "secrets.json"));
        _az.Respond(["network", "public-ip", "show"], SandboxAddress + "\n");
    }

    [Fact]
    public async Task Creating_a_Sandbox_makes_a_VM_in_the_Sandbox_resource_group_tagged_with_its_owner()
    {
        var owner = new SandboxOwner(SandboxOwnerKind.RepositorySetup, Guid.NewGuid());

        var sandbox = await Provider().NameAndCreateAsync(owner, SandboxMachine.Default, [], TestContext.Current.CancellationToken);

        var vm = _az.CallTo("vm", "create");
        Assert.Equal(Subscription, FakeAzureCli.Value(vm, "--subscription"));
        Assert.Equal(ResourceGroup, FakeAzureCli.Value(vm, "--resource-group"));
        Assert.Contains("overseer-owner-kind=RepositorySetup", FakeAzureCli.Values(vm, "--tags"));
        Assert.Contains($"overseer-owner-id={owner.Id}", FakeAzureCli.Values(vm, "--tags"));
        Assert.Equal(
            $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers/Microsoft.Compute/virtualMachines/{FakeAzureCli.Value(vm, "--name")}",
            sandbox.ResourceId);
        Assert.Equal(owner, sandbox.Owner);
    }

    [Theory]
    [InlineData("Standard_D2_v5")]
    [InlineData("Standard_D4s_v5")]
    public async Task A_Sandbox_is_a_regular_VM_of_the_size_it_is_created_with(string vmSize)
    {
        await CreateAsync(Provider(), new SandboxMachine(vmSize, Spot: false));

        var vm = _az.CallTo("vm", "create");
        Assert.Equal(vmSize, FakeAzureCli.Value(vm, "--size"));
        Assert.Equal("Regular", FakeAzureCli.Value(vm, "--priority"));
        Assert.DoesNotContain("--eviction-policy", vm);
    }

    [Fact]
    public async Task A_spot_Sandbox_is_a_spot_VM_that_eviction_deletes()
    {
        await CreateAsync(Provider(), new SandboxMachine("Standard_D2_v5", Spot: true));

        var vm = _az.CallTo("vm", "create");
        Assert.Equal("Standard_D2_v5", FakeAzureCli.Value(vm, "--size"));
        Assert.Equal("Spot", FakeAzureCli.Value(vm, "--priority"));
        Assert.Equal("Delete", FakeAzureCli.Value(vm, "--eviction-policy"));
    }

    [Fact]
    public async Task Without_spot_capacity_creation_fails_with_the_Azure_error_and_leaves_nothing_of_the_Sandbox_behind()
    {
        _az.Fail(["vm", "create"],
            "ERROR: (SkuNotAvailable) The requested VM size for resource 'Following SKUs have failed for Capacity Restrictions: Standard_D2_v5' is currently not available in location 'westeurope'.");

        var failure = await Assert.ThrowsAsync<SandboxCreationFailedException>(() =>
            CreateAsync(Provider(), new SandboxMachine("Standard_D2_v5", Spot: true)));

        Assert.Contains("(SkuNotAvailable) The requested VM size for resource 'Following SKUs have failed for Capacity Restrictions: Standard_D2_v5'", failure.Message);
        await AssertRemovedAsync(FakeAzureCli.Value(_az.CallTo("vm", "create"), "--name"));
    }

    [Fact]
    public async Task A_Sandbox_has_a_public_IP_that_only_the_developers_current_address_reaches_over_SSH()
    {
        await CreateAsync(Provider());

        var rule = Assert.Single(_az.CallsTo("network", "nsg", "rule", "create"), rule => FakeAzureCli.Value(rule, "--access") == "Allow");
        Assert.Equal("Inbound", FakeAzureCli.Value(rule, "--direction"));
        Assert.Equal(DeveloperAddress.ToString(), FakeAzureCli.Value(rule, "--source-address-prefixes"));
        Assert.Equal("22", FakeAzureCli.Value(rule, "--destination-port-ranges"));
        var nic = _az.CallTo("network", "nic", "create");
        Assert.Equal(FakeAzureCli.Value(rule, "--nsg-name"), FakeAzureCli.Value(nic, "--network-security-group"));
        Assert.Equal(FakeAzureCli.Value(_az.CallTo("network", "public-ip", "create"), "--name"), FakeAzureCli.Value(nic, "--public-ip-address"));
        Assert.Equal(FakeAzureCli.Value(nic, "--name"), FakeAzureCli.Value(_az.CallTo("vm", "create"), "--nics"));
    }

    [Fact]
    public async Task Sandboxes_on_the_VNet_they_share_cannot_reach_each_other()
    {
        await CreateAsync(Provider());

        // Azure's default rules let the whole VNet in, and agents run code nobody has reviewed yet.
        var rule = Assert.Single(_az.CallsTo("network", "nsg", "rule", "create"), rule => FakeAzureCli.Value(rule, "--access") == "Deny");
        Assert.Equal("Inbound", FakeAzureCli.Value(rule, "--direction"));
        Assert.Equal("VirtualNetwork", FakeAzureCli.Value(rule, "--source-address-prefixes"));
        Assert.Equal("*", FakeAzureCli.Value(rule, "--destination-port-ranges"));
        Assert.Equal("*", FakeAzureCli.Value(rule, "--protocol"));
    }

    [Fact]
    public async Task On_the_private_network_a_Sandbox_has_no_public_IP_and_only_Overseers_subnet_reaches_it_by_its_private_IP()
    {
        InstalledSandboxNetwork();

        await CreateAsync(Provider(privateNetwork: true));

        Assert.Empty(_az.CallsTo("network", "public-ip", "create"));
        var nic = _az.CallTo("network", "nic", "create");
        Assert.DoesNotContain("--public-ip-address", nic);
        var rule = Assert.Single(_az.CallsTo("network", "nsg", "rule", "create"), rule => FakeAzureCli.Value(rule, "--access") == "Allow");
        Assert.Equal("Inbound", FakeAzureCli.Value(rule, "--direction"));
        // The Container Apps subnet of the VNet install creates for Overseer.
        Assert.Equal("10.40.0.0/23", FakeAzureCli.Value(rule, "--source-address-prefixes"));
        Assert.Equal("22", FakeAzureCli.Value(rule, "--destination-port-ranges"));
        Assert.Equal(PrivateSandboxAddress, Assert.Single(_ssh.Runs).Target.Host);
    }

    [Fact]
    public async Task On_the_private_network_a_restarted_Overseer_still_reaches_a_Sandbox_by_its_private_IP()
    {
        InstalledSandboxNetwork();
        var sandbox = await CreateAsync(Provider(privateNetwork: true));
        var created = _ssh.Runs.Single().Target;
        _az.Respond(["vm", "show"], $"{PrivateSandboxAddress}\n{created.HostKeyFingerprint}\n");

        await Provider(privateNetwork: true).RunCommandAsync(sandbox, "git status", TestContext.Current.CancellationToken);

        Assert.Equal((created, "git status"), _ssh.Runs[^1]);
        Assert.Contains("privateIps", FakeAzureCli.Value(_az.CallTo("vm", "show"), "--query"));
    }

    [Fact]
    public async Task On_the_private_network_the_provider_does_not_make_the_Sandbox_VNet_that_install_makes_with_its_NAT_Gateway_and_peering()
    {
        var failure = await Assert.ThrowsAsync<SandboxCreationFailedException>(() => CreateAsync(Provider(privateNetwork: true)));

        Assert.Empty(_az.CallsTo("network", "vnet", "create"));
        Assert.Empty(_az.CallsTo("vm", "create"));
        Assert.Contains("overseer install", failure.Message);
    }

    [Fact]
    public async Task With_a_Managed_Identity_the_Azure_CLI_logs_in_as_it_once_before_anything_else_without_touching_the_machines_own_login()
    {
        const string clientId = "9f8e7d6c-aaaa-4bbb-8ccc-ddddeeeeffff";
        InstalledSandboxNetwork();
        var provider = Provider(privateNetwork: true, managedIdentityClientId: clientId);

        var sandbox = await CreateAsync(provider);
        await provider.ListAsync(TestContext.Current.CancellationToken);
        await provider.DestroyAsync(sandbox, TestContext.Current.CancellationToken);

        var login = Assert.Single(_az.CallsTo("login"));
        Assert.Equal("login", _az.Calls[0][0]);
        Assert.Contains("--identity", login);
        Assert.Equal(clientId, FakeAzureCli.Value(login, "--client-id"));
        Assert.All(_az.ConfigDirectories, directory => Assert.Equal(_az.ConfigDirectories[0], directory));
        Assert.NotNull(_az.ConfigDirectories[0]);
    }

    [Fact]
    public async Task An_agent_runs_opencode_acp_over_ssh_and_the_tokens_reach_it_only_over_standard_input()
    {
        var agent = new ScriptedAgentProcess();
        _ssh.Started = (_, _) => agent;
        var provider = Provider();
        var sandbox = await CreateAsync(provider);

        await using var session = await provider.StartAgentSessionAsync(
            sandbox, AgentRole.Reviewer, AgentHarnessConfiguration.Default, new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-secret" },
            TestContext.Current.CancellationToken);

        var (_, command) = _ssh.Runs[^1];
        Assert.Contains("opencode acp", command);
        Assert.DoesNotContain("sk-secret", command);
        Assert.Equal("reviewer", (string)agent.Requests[^1]["params"]!["modeId"]!);
        var sent = Encoding.UTF8.GetString(Convert.FromBase64String(agent.Input[0]));
        Assert.Contains("sk-secret", sent);
        Assert.Contains("OPENCODE_CONFIG_CONTENT", sent);
    }

    [Fact]
    public async Task An_environment_variable_that_isnt_a_name_is_refused_before_anything_starts()
    {
        var provider = Provider();
        var sandbox = await CreateAsync(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.StartAgentSessionAsync(
            sandbox, AgentRole.Developer, AgentHarnessConfiguration.Default, new Dictionary<string, string> { ["A;rm -rf /"] = "x" },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_a_Managed_Identity_the_Azure_CLI_uses_its_own_login()
    {
        await CreateAsync(Provider());

        Assert.Empty(_az.CallsTo("login"));
        Assert.All(_az.ConfigDirectories, Assert.Null);
    }

    [Fact]
    public async Task Creation_completes_once_cloud_init_has_finished_on_the_Sandbox_with_the_host_key_it_was_given()
    {
        await CreateAsync(Provider());

        var (target, command) = Assert.Single(_ssh.Runs);
        Assert.Contains("cloud-init status --wait", command);
        Assert.Equal(SandboxAddress, target.Host);
        Assert.Equal(22, target.Port);
        Assert.Equal("overseer", target.Username);
        Assert.Contains($"overseer-host-key={target.HostKeyFingerprint}", FakeAzureCli.Values(_az.CallTo("vm", "create"), "--tags"));
    }

    [Fact]
    public async Task Every_Sandbox_installs_one_exact_OpenCode_version()
    {
        await CreateAsync(Provider());

        // The setup script travels base64-encoded in the cloud-init the VM is created with.
        var cloudInit = Assert.Single(_az.CustomData);
        var encoded = Regex.Match(cloudInit, @"path: /opt/overseer/setup-sandbox\.sh\n(?:\s+\w+: .*\n)*?\s+content: (\S+)").Groups[1].Value;
        var setup = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        // An exact release, not a range or a tag such as latest, so every Sandbox runs the OpenCode the default opencode.json is for.
        var version = Regex.Match(setup, @"^opencode_version=(.*)$", RegexOptions.Multiline).Groups[1].Value;
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        var installs = Regex.Matches(setup, @"^npm install .*$", RegexOptions.Multiline).Select(install => install.Value).ToList();
        Assert.Equal(["npm install -g playwright \"opencode-ai@$opencode_version\""], installs);
    }

    [Fact]
    public async Task Each_Sandbox_gets_a_fresh_key_pair_whose_private_key_is_kept_in_the_secret_store()
    {
        var first = await CreateAsync(Provider());
        var second = await CreateAsync(Provider());

        var keys = _az.CallsTo("vm", "create").Select(vm => FakeAzureCli.Value(vm, "--ssh-key-values")).ToList();
        Assert.Equal(2, keys.Distinct().Count());
        var stored = new List<string>();
        foreach (var (sandbox, publicKey) in new[] { first, second }.Zip(keys))
        {
            var privateKey = await _secrets.ReadAsync($"{VirtualMachine(sandbox)}-ssh-key", TestContext.Current.CancellationToken);
            Assert.NotNull(privateKey);
            Assert.Equal(publicKey, await PublicKeyOfAsync(privateKey));
            stored.Add(privateKey);
        }
        Assert.Equal(stored, _ssh.Runs.Select(run => run.Target.PrivateKey));
    }

    [Fact]
    public async Task A_creation_failure_surfaces_the_Azure_error_and_leaves_nothing_of_the_Sandbox_behind()
    {
        _az.Fail(["vm", "create"],
            "ERROR: (QuotaExceeded) Operation could not be completed as it results in exceeding approved standardDSv5Family Cores quota.");

        var failure = await Assert.ThrowsAsync<SandboxCreationFailedException>(() => CreateAsync(Provider()));

        Assert.Contains("(QuotaExceeded) Operation could not be completed as it results in exceeding approved standardDSv5Family Cores quota.", failure.Message);
        await AssertRemovedAsync(FakeAzureCli.Value(_az.CallTo("vm", "create"), "--name"));
    }

    [Fact]
    public async Task A_failing_setup_fails_creation_with_what_cloud_init_reported_and_removes_the_Sandbox()
    {
        _ssh.Answer = (_, _) => new CommandResult(1, "status: error\nnpm ERR! 404 Not Found - GET https://registry.npmjs.org/opencode-ai\n");

        var failure = await Assert.ThrowsAsync<SandboxCreationFailedException>(() => CreateAsync(Provider()));

        Assert.Contains("npm ERR! 404 Not Found - GET https://registry.npmjs.org/opencode-ai", failure.Message);
        await AssertRemovedAsync(FakeAzureCli.Value(_az.CallTo("vm", "create"), "--name"));
    }

    [Fact]
    public async Task Creation_waits_for_a_booting_Sandbox_to_accept_SSH()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;
        _ssh.Answer = (_, _) => ++attempts <= 3 ? throw new SandboxUnreachableException("Connection refused") : new CommandResult(0, "status: done");

        await WhileTimePassesAsync(time, CreateAsync(Provider(time: time)));

        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task A_Sandbox_that_never_accepts_SSH_fails_creation_after_thirty_minutes_and_is_removed()
    {
        var time = new FakeTimeProvider();
        var attempts = new List<DateTimeOffset>();
        _ssh.Answer = (_, _) =>
        {
            attempts.Add(time.GetUtcNow());
            throw new SandboxUnreachableException("There is no SSH connection to overseer@198.51.100.7:22: Connection refused");
        };

        var failure = await Assert.ThrowsAsync<SandboxCreationFailedException>(() => WhileTimePassesAsync(time, CreateAsync(Provider(time: time))));

        Assert.Contains("30 minutes", failure.Message);
        Assert.Contains("Connection refused", failure.Message);
        Assert.InRange(attempts[^1] - attempts[0], TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30));
        await AssertRemovedAsync(FakeAzureCli.Value(_az.CallTo("vm", "create"), "--name"));
    }

    [Fact]
    public async Task A_setup_that_never_finishes_fails_creation_after_thirty_minutes_and_removes_the_Sandbox()
    {
        var time = new FakeTimeProvider();
        _ssh.Hangs = true;

        var failure = await Assert.ThrowsAsync<SandboxCreationFailedException>(() => WhileTimePassesAsync(time, CreateAsync(Provider(time: time))));

        Assert.Contains("30 minutes", failure.Message);
        await AssertRemovedAsync(FakeAzureCli.Value(_az.CallTo("vm", "create"), "--name"));
    }

    [Fact]
    public async Task Destroying_a_Sandbox_removes_its_VM_and_everything_created_with_it_and_its_key()
    {
        var provider = Provider();
        var sandbox = await CreateAsync(provider);

        await provider.DestroyAsync(sandbox, TestContext.Current.CancellationToken);

        await AssertRemovedAsync(VirtualMachine(sandbox));
    }

    [Fact]
    public async Task Listing_gives_the_Sandboxes_in_the_Sandbox_resource_group_with_their_owner_tags()
    {
        var intent = Guid.NewGuid();
        var setup = Guid.NewGuid();
        _az.Respond(["vm", "list"], $"""
            sandbox-1	Intent	{intent}
            sandbox-2	RepositorySetup	{setup}
            someone-elses-vm	Ours	not-a-guid

            """);

        var listed = await Provider().ListAsync(TestContext.Current.CancellationToken);

        var group = $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers/Microsoft.Compute/virtualMachines";
        Assert.Equal(
            [
                new Sandbox($"{group}/sandbox-1", new SandboxOwner(SandboxOwnerKind.Intent, intent)),
                new Sandbox($"{group}/sandbox-2", new SandboxOwner(SandboxOwnerKind.RepositorySetup, setup)),
            ],
            listed);
        var list = _az.CallTo("vm", "list");
        Assert.Equal(Subscription, FakeAzureCli.Value(list, "--subscription"));
        Assert.Equal(ResourceGroup, FakeAzureCli.Value(list, "--resource-group"));
    }

    [Fact]
    public async Task A_command_runs_over_SSH_in_the_Sandbox_and_gives_its_exit_code_and_output()
    {
        var provider = Provider();
        var sandbox = await CreateAsync(provider);
        var created = _ssh.Runs.Single().Target;
        _ssh.Answer = (_, command) => command == "dotnet test" ? new CommandResult(1, "1 test failed\n") : new CommandResult(0, "");

        var result = await provider.RunCommandAsync(sandbox, "dotnet test", TestContext.Current.CancellationToken);

        Assert.Equal(new CommandResult(1, "1 test failed\n"), result);
        Assert.Equal((created, "dotnet test"), _ssh.Runs[^1]);
    }

    [Fact]
    public async Task After_a_restart_commands_still_run_in_a_Sandbox_created_before()
    {
        var sandbox = await CreateAsync(Provider());
        var created = _ssh.Runs.Single().Target;
        _az.Respond(["vm", "show"], $"{SandboxAddress}\n{created.HostKeyFingerprint}\n");

        // A new process: only Azure and the secret store remember the Sandbox.
        await Provider().RunCommandAsync(sandbox, "git status", TestContext.Current.CancellationToken);

        Assert.Equal((created, "git status"), _ssh.Runs[^1]);
        Assert.Equal(VirtualMachine(sandbox), FakeAzureCli.Value(_az.CallTo("vm", "show"), "--name"));
    }

    [Fact]
    public async Task Taking_a_file_reads_it_over_SSH_and_removes_it_whatever_its_path_holds_for_the_shell()
    {
        var provider = Provider();
        var sandbox = await CreateAsync(provider);
        // The Sandbox's shell, played by this machine's.
        _ssh.Answer = (_, command) => RunInShell(command);
        var path = Path.Combine(_directory, "it's a \"$HOME\" `file`; rm -rf .json");
        await File.WriteAllTextAsync(path, "{ \"questions\": [] }\n", TestContext.Current.CancellationToken);
        var neighbour = Path.Combine(_directory, "round.json");
        await File.WriteAllTextAsync(neighbour, "kept", TestContext.Current.CancellationToken);

        var taken = await provider.TakeFileAsync(sandbox, path, TestContext.Current.CancellationToken);
        var again = await provider.TakeFileAsync(sandbox, path, TestContext.Current.CancellationToken);

        Assert.Equal("{ \"questions\": [] }\n", taken);
        Assert.Null(again);
        Assert.False(File.Exists(path));
        Assert.Equal("kept", await File.ReadAllTextAsync(neighbour, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Taking_a_file_that_is_there_but_cant_be_read_or_removed_fails_with_what_the_Sandbox_said()
    {
        var provider = Provider();
        var sandbox = await CreateAsync(provider);
        _ssh.Answer = (_, _) => new CommandResult(1, "rm: cannot remove '/home/overseer/.overseer/round.json': Permission denied\n");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.TakeFileAsync(sandbox, "/home/overseer/.overseer/round.json", TestContext.Current.CancellationToken));

        Assert.Equal(
            "The file /home/overseer/.overseer/round.json in the Sandbox could not be taken: rm: cannot remove '/home/overseer/.overseer/round.json': Permission denied\n",
            failure.Message);
    }

    /// <summary>Runs a command in a local shell as SSH runs it in a Sandbox, with standard error merged into standard output.</summary>
    private static CommandResult RunInShell(string command)
    {
        using var shell = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", "exec 2>&1\n" + command])
        {
            RedirectStandardOutput = true,
        })!;
        var output = shell.StandardOutput.ReadToEnd();
        shell.WaitForExit();
        return new CommandResult(shell.ExitCode, output);
    }

    /// <summary>The Sandbox VNet as install leaves it, and the private IP Azure gives a Sandbox's network interface on it.</summary>
    private void InstalledSandboxNetwork() =>
        _az.Respond(["network", "vnet", "list"], "sandboxes\n")
            .Respond(["network", "nic", "show"], PrivateSandboxAddress + "\n");

    /// <summary>
    /// The VM, its disk, its network interface, then the public IP and network security group the interface used, in an
    /// order in which nothing deleted is still in use; and the key.
    /// </summary>
    private async Task AssertRemovedAsync(string vm)
    {
        var group = $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers";
        Assert.Equal(
            [
                $"{group}/Microsoft.Compute/virtualMachines/{vm}",
                $"{group}/Microsoft.Compute/disks/{vm}-disk",
                $"{group}/Microsoft.Network/networkInterfaces/{vm}-nic",
                $"{group}/Microsoft.Network/publicIPAddresses/{vm}-ip",
                $"{group}/Microsoft.Network/networkSecurityGroups/{vm}-nsg",
            ],
            _az.CallsTo("resource", "delete").Select(call => FakeAzureCli.Value(call, "--ids")));
        Assert.False(await _secrets.IsSetAsync($"{vm}-ssh-key", TestContext.Current.CancellationToken));
    }

    /// <summary>Lets <paramref name="time"/> pass, a few seconds at a time, until <paramref name="task"/> is done.</summary>
    private static async Task<T> WhileTimePassesAsync<T>(FakeTimeProvider time, Task<T> task)
    {
        while (!task.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(5));
        }
        return await task;
    }

    private const string Token = "github_pat_11ABCDEF0_secretvalue";

    [Fact]
    public async Task Each_Repository_in_scope_is_cloned_after_the_Sandbox_is_set_up_and_creation_completes_only_then()
    {
        await GivenTokenAsync();

        var sandbox = await CloneAsync(["acme/web", "acme/api"], mayPush: true);

        var commands = _ssh.Runs.Select(run => run.Command).ToList();
        var web = commands.FindIndex(command => command.Contains("git clone") && command.Contains("acme/web"));
        var api = commands.FindIndex(command => command.Contains("git clone") && command.Contains("acme/api"));
        Assert.True(web > commands.FindIndex(command => command.Contains("cloud-init status")));
        Assert.True(api > web);
        Assert.NotNull(sandbox);
    }

    [Fact]
    public async Task The_token_is_in_no_remote_URL_and_no_clone_command()
    {
        await GivenTokenAsync();

        await CloneAsync(["acme/web"], mayPush: true);

        var clones = _ssh.Runs.Select(run => run.Command).Where(command => command.Contains("git clone")).ToList();
        Assert.NotEmpty(clones);
        Assert.All(clones, command => Assert.DoesNotContain(Token, command));
        Assert.All(clones, command => Assert.DoesNotContain("x-access-token", command));
        Assert.All(clones, command => Assert.DoesNotMatch(@"https://[^/\s]*@", command));
        Assert.Single(_ssh.Runs.Select(run => run.Command), command => command.Contains(Token));
    }

    [Fact]
    public async Task A_clone_that_fails_fails_the_creation_naming_the_Repository_and_GitHubs_error_without_the_token_and_removes_the_Sandbox()
    {
        await GivenTokenAsync();
        _ssh.Answer = (_, command) => command.Contains("git clone") && command.Contains("acme/missing")
            ? new CommandResult(128, $"fatal: unable to access 'https://github.com/acme/missing/': The requested URL returned error: 404 {Token}\n")
            : new CommandResult(0, "");

        var failure = await Assert.ThrowsAsync<SandboxCreationFailedException>(() => CloneAsync(["acme/web", "acme/missing"], mayPush: false));

        Assert.Contains("acme/missing", failure.Message);
        Assert.Contains("returned error: 404", failure.Message);
        Assert.DoesNotContain(Token, failure.Message);
        await AssertRemovedAsync(FakeAzureCli.Value(_az.CallTo("vm", "create"), "--name"));
    }

    [Fact]
    public async Task Without_a_GitHub_token_a_Sandbox_with_Repositories_cannot_be_created()
    {
        var failure = await Assert.ThrowsAsync<SandboxCreationFailedException>(() => CloneAsync(["acme/web"], mayPush: false));

        Assert.Contains("GitHub token", failure.Message);
        Assert.DoesNotContain(_ssh.Runs, run => run.Command.Contains("git clone"));
    }

    [Fact]
    public async Task A_Sandbox_without_push_access_has_its_credentials_taken_away_once_cloning_is_done()
    {
        await GivenTokenAsync();

        await CloneAsync(["acme/web"], mayPush: false);

        var commands = _ssh.Runs.Select(run => run.Command).ToList();
        var clone = commands.FindLastIndex(command => command.Contains("git clone"));
        var removal = commands.FindIndex(command => command.Contains(".overseer/github-token") && command.Contains("rm -f") && !command.Contains(Token));
        Assert.True(removal > clone);
        Assert.Contains("gh auth logout", commands[removal]);
        Assert.Contains("credential.helper", commands[removal]);
    }

    [Fact]
    public async Task A_Sandbox_with_push_access_keeps_its_credentials()
    {
        await GivenTokenAsync();

        await CloneAsync(["acme/web"], mayPush: true);

        Assert.DoesNotContain(_ssh.Runs, run => run.Command.Contains("gh auth logout"));
    }

    [Fact]
    public async Task A_Repository_name_that_is_not_owner_slash_name_is_refused_before_it_reaches_a_shell()
    {
        await GivenTokenAsync();

        var failure = await Assert.ThrowsAsync<SandboxCreationFailedException>(() => CloneAsync(["acme/web; rm -rf ~"], mayPush: false));

        Assert.Contains("acme/web; rm -rf ~", failure.Message);
        Assert.DoesNotContain(_ssh.Runs, run => run.Command.Contains("rm -rf ~"));
    }

    private Task GivenTokenAsync() => _secrets.SetAsync("github-token", Token, TestContext.Current.CancellationToken);

    private Task<Sandbox> CloneAsync(string[] repositories, bool mayPush) =>
        Provider().NameAndCreateAsync(
            new SandboxOwner(SandboxOwnerKind.Intent, Guid.NewGuid()), SandboxMachine.Default, repositories, TestContext.Current.CancellationToken, mayPush);

    private Task<Sandbox> CreateAsync(AzureSandboxProvider provider, SandboxMachine? machine = null) =>
        provider.NameAndCreateAsync(
            new SandboxOwner(SandboxOwnerKind.Intent, Guid.NewGuid()), machine ?? SandboxMachine.Default, [],
            TestContext.Current.CancellationToken);

    private static string VirtualMachine(Sandbox sandbox) => sandbox.ResourceId.Split('/').Last();

    /// <summary>The public key OpenSSH derives from a private key, as an independent check on the pair.</summary>
    private async Task<string> PublicKeyOfAsync(string privateKey)
    {
        var file = Path.Combine(_directory, $"{Guid.NewGuid():N}.key");
        await using (var stream = UserOnlyFile.CreateNew(file))
        {
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(privateKey));
        }
        using var keygen = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ssh-keygen", ["-y", "-f", file])
        {
            RedirectStandardOutput = true,
        })!;
        var publicKey = await keygen.StandardOutput.ReadToEndAsync();
        await keygen.WaitForExitAsync();
        return publicKey.Trim();
    }

    private AzureSandboxProvider Provider(TimeProvider? time = null, bool privateNetwork = false, string? managedIdentityClientId = null) => new(
        new AzureSandboxSettings(Subscription, ResourceGroup, privateNetwork, managedIdentityClientId),
        _az.Environment(),
        _secrets,
        _secrets,
        _ssh,
        // A deployed Overseer has no use for its public address: nothing reaches a Sandbox from the internet.
        _ => privateNetwork ? throw new InvalidOperationException("A private-network Sandbox lets in no public address.") : Task.FromResult(DeveloperAddress),
        time ?? TimeProvider.System);

    public void Dispose()
    {
        _az.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}

/// <summary>SSH that records what runs where and answers as <see cref="Answer"/> says: by default, success without output.</summary>
public sealed class FakeSandboxSsh : ISandboxSsh
{
    private readonly ConcurrentQueue<(SshTarget Target, string Command)> _runs = new();

    /// <summary>Answers a command run on a target; throw <see cref="SandboxUnreachableException"/> to play an unreachable Sandbox.</summary>
    public Func<SshTarget, string, CommandResult> Answer { get; set; } = (_, _) => new CommandResult(0, "");

    /// <summary>Makes every command run until it is cancelled, as <c>cloud-init status --wait</c> does when setup is stuck.</summary>
    public bool Hangs { get; set; }

    public IReadOnlyList<(SshTarget Target, string Command)> Runs => [.. _runs];

    public async Task<CommandResult> RunAsync(SshTarget target, string command, CancellationToken cancellationToken)
    {
        _runs.Enqueue((target, command));
        if (Hangs)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        return Answer(target, command);
    }

    /// <summary>Starts what <see cref="Started"/> makes of a command; by default there is none to start.</summary>
    public Func<SshTarget, string, ISshProcess> Started { get; set; } =
        (_, _) => throw new NotSupportedException("No process is scripted.");

    public Task<ISshProcess> StartAsync(SshTarget target, string command, CancellationToken cancellationToken)
    {
        _runs.Enqueue((target, command));
        return Task.FromResult(Started(target, command));
    }
}
