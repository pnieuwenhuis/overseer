using System.Collections.Concurrent;
using System.Net;
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

        var sandbox = await Provider().CreateAsync(owner, ["pnieuwenhuis/overseer"], TestContext.Current.CancellationToken);

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
    [InlineData(null, "Standard_D4s_v5")]
    [InlineData("Standard_D8s_v5", "Standard_D8s_v5")]
    public async Task A_Sandbox_has_the_configured_VM_size_or_the_default(string? configured, string expected)
    {
        await CreateAsync(Provider(configured));

        Assert.Equal(expected, FakeAzureCli.Value(_az.CallTo("vm", "create"), "--size"));
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

    private Task<Sandbox> CreateAsync(AzureSandboxProvider provider) =>
        provider.CreateAsync(new SandboxOwner(SandboxOwnerKind.Intent, Guid.NewGuid()), ["pnieuwenhuis/overseer"], TestContext.Current.CancellationToken);

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

    private AzureSandboxProvider Provider(string? vmSize = null, TimeProvider? time = null) => new(
        new AzureSandboxSettings(Subscription, ResourceGroup, vmSize ?? AzureSandboxSettings.DefaultVmSize),
        _az.Environment(),
        _secrets,
        _secrets,
        _ssh,
        _ => Task.FromResult(DeveloperAddress),
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
}
