using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Overseer.Sandboxes;

/// <summary>Runs commands in a Sandbox over SSH.</summary>
public interface ISandboxSsh
{
    /// <summary>Runs a shell command as <see cref="SshTarget.Username"/> and waits for it to finish.</summary>
    /// <exception cref="SandboxUnreachableException">No SSH connection to the Sandbox, or it presented another host key.</exception>
    Task<CommandResult> RunAsync(SshTarget target, string command, CancellationToken cancellationToken);
}

/// <summary>Where and as whom to log in to a Sandbox, and the host key it must present.</summary>
/// <param name="PrivateKey">The user's private key, PEM-encoded.</param>
/// <param name="HostKeyFingerprint">The host key's SHA256 fingerprint as OpenSSH shows it: <c>SHA256:</c> and unpadded base64.</param>
public sealed record SshTarget(string Host, int Port, string Username, string PrivateKey, string HostKeyFingerprint)
{
    // Leaves the private key out, should a target ever be logged.
    public override string ToString() => $"{Username}@{Host}:{Port}";
}

/// <summary>A Sandbox could not be reached over SSH, or it was not the machine it should be. The message says why.</summary>
public sealed class SandboxUnreachableException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>SSH through SSH.NET: the private key stays in memory, and only the expected host key is trusted.</summary>
public sealed class SshNetSandboxSsh : ISandboxSsh
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    // Keeps a quiet session, such as a long build, from being dropped as idle on the way, as Azure does after 4 minutes.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    /// <returns>The exit code, or -1 when the command was killed by a signal, and standard error merged into standard output.</returns>
    public async Task<CommandResult> RunAsync(SshTarget target, string command, CancellationToken cancellationToken)
    {
        using var key = new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(target.PrivateKey)));
        var connection = new Renci.SshNet.ConnectionInfo(target.Host, target.Port, target.Username, new PrivateKeyAuthenticationMethod(target.Username, key))
        {
            Timeout = ConnectTimeout,
        };
        // A Sandbox has an RSA host key only; asking for nothing else keeps a server from presenting another key it might have.
        foreach (var algorithm in connection.HostKeyAlgorithms.Keys.Where(name => !name.StartsWith("rsa-sha2-", StringComparison.Ordinal)).ToList())
        {
            connection.HostKeyAlgorithms.Remove(algorithm);
        }

        using var client = new SshClient(connection);
        client.KeepAliveInterval = KeepAliveInterval;
        string? presented = null;
        client.HostKeyReceived += (_, host) =>
        {
            presented = $"SHA256:{host.FingerPrintSHA256}";
            host.CanTrust = presented == target.HostKeyFingerprint;
        };
        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is SocketException or SshException)
        {
            throw presented is not null && presented != target.HostKeyFingerprint
                ? new SandboxUnreachableException(
                    $"{target.Host} presented the host key {presented} instead of the Sandbox's {target.HostKeyFingerprint}, so Overseer didn't log in.", exception)
                : new SandboxUnreachableException($"There is no SSH connection to {target}: {exception.Message}", exception);
        }

        // Merging standard error into standard output in the shell keeps them in the order they were written.
        using var run = client.CreateCommand($"exec 2>&1\n{command}");
        try
        {
            await run.ExecuteAsync(cancellationToken);
        }
        catch (SshConnectionException exception)
        {
            throw new SandboxUnreachableException($"The SSH connection to {target} was lost: {exception.Message}", exception);
        }
        return new CommandResult(run.ExitStatus ?? -1, run.Result);
    }
}
