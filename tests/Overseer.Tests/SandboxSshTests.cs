using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Overseer.Sandboxes;

namespace Overseer.Tests;

/// <summary>Running commands over SSH, against a real OpenSSH server standing in for an Azure Sandbox.</summary>
public class SandboxSshTests(OpenSshServer server) : IClassFixture<OpenSshServer>
{
    private readonly SshNetSandboxSsh _ssh = new();

    [Fact]
    public async Task A_command_returns_its_exit_code_and_its_output_with_errors_in_between()
    {
        var result = await _ssh.RunAsync(server.Target, "echo first; echo failed >&2; echo last; exit 4", TestContext.Current.CancellationToken);

        Assert.Equal(new CommandResult(4, "first\nfailed\nlast\n"), result);
    }

    [Fact]
    public async Task A_host_presenting_another_host_key_is_refused()
    {
        var expected = SshKeyPair.Generate().Fingerprint;

        var refused = await Assert.ThrowsAsync<SandboxUnreachableException>(() =>
            _ssh.RunAsync(server.Target with { HostKeyFingerprint = expected }, "echo hello", TestContext.Current.CancellationToken));

        Assert.Contains(expected, refused.Message);
    }

    [Fact]
    public async Task A_host_that_does_not_answer_is_unreachable()
    {
        int closedPort;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        await Assert.ThrowsAsync<SandboxUnreachableException>(() =>
            _ssh.RunAsync(server.Target with { Host = "127.0.0.1", Port = closedPort }, "echo hello", TestContext.Current.CancellationToken));
    }
}

/// <summary>
/// An OpenSSH server with a host key and an authorized user key that Overseer generated, as a Sandbox's cloud-init
/// sets them up, so only that host key is presented.
/// </summary>
public sealed class OpenSshServer : IAsyncLifetime
{
    private const int Port = 2222;
    private const string HostKeys = "/config/ssh_host_keys";
    private const UnixFileModes OwnerOnly = UnixFileModes.UserRead | UnixFileModes.UserWrite;

    private readonly SshKeyPair _hostKey = SshKeyPair.Generate();
    private readonly SshKeyPair _userKey = SshKeyPair.Generate();
    private readonly IContainer _container;

    public OpenSshServer() => _container = new ContainerBuilder("lscr.io/linuxserver/openssh-server:10.3_p1-r1-ls240")
        .WithEnvironment("USER_NAME", "overseer")
        .WithEnvironment("PUBLIC_KEY", _userKey.PublicKey)
        .WithEnvironment("PUID", "1000")
        .WithEnvironment("PGID", "1000")
        // With its host key folder present, the image uses the keys in it instead of generating its own.
        .WithResourceMapping(Encoding.UTF8.GetBytes(_hostKey.PrivateKey), $"{HostKeys}/ssh_host_rsa_key", 1000, 1000, OwnerOnly)
        .WithResourceMapping(Encoding.UTF8.GetBytes(_hostKey.PublicKey), $"{HostKeys}/ssh_host_rsa_key.pub", 1000, 1000, OwnerOnly)
        .WithPortBinding(Port, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(Port))
        .Build();

    /// <summary>Logs in as the authorized user and expects the generated host key.</summary>
    public SshTarget Target => new(_container.Hostname, _container.GetMappedPublicPort(Port), "overseer", _userKey.PrivateKey, _hostKey.Fingerprint);

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
