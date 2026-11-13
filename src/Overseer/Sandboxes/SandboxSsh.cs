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

    /// <summary>
    /// Starts a shell command as <see cref="SshTarget.Username"/> and returns at once, to talk to it over standard input and
    /// output while it runs. Standard error is kept apart, as it would corrupt a protocol on standard output.
    /// </summary>
    /// <exception cref="SandboxUnreachableException">No SSH connection to the Sandbox, or it presented another host key.</exception>
    Task<ISshProcess> StartAsync(SshTarget target, string command, CancellationToken cancellationToken);
}

/// <summary>A command running in a Sandbox, which the SSH connection it runs over carries. Disposing it kills the command and closes the connection.</summary>
public interface ISshProcess : IAsyncDisposable
{
    /// <summary>Writes a line to the command's standard input.</summary>
    /// <exception cref="IOException">The command no longer takes input: it has exited or the connection is gone.</exception>
    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>Reads the next line of the command's standard output, waiting for it; <c>null</c> once the command has exited and everything it wrote is read.</summary>
    /// <exception cref="SandboxUnreachableException">The SSH connection was lost.</exception>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>The end of the command's standard error, for saying what went wrong. Complete once <see cref="ReadLineAsync"/> gave <c>null</c>.</summary>
    string StandardErrorTail { get; }

    /// <summary>The command's exit code, or -1 when it was killed by a signal; completes once <see cref="ReadLineAsync"/> gave <c>null</c>.</summary>
    Task<int> ExitCode { get; }
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
        using var client = await ConnectAsync(target, cancellationToken);
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

    public async Task<ISshProcess> StartAsync(SshTarget target, string command, CancellationToken cancellationToken)
    {
        var client = await ConnectAsync(target, cancellationToken);
        try
        {
            return new SshNetProcess(client, target, command);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Connects, trusting only the target's host key. The caller disposes the client.</summary>
    private static async Task<SshClient> ConnectAsync(SshTarget target, CancellationToken cancellationToken)
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

        var client = new SshClient(connection);
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
            client.Dispose();
            throw presented is not null && presented != target.HostKeyFingerprint
                ? new SandboxUnreachableException(
                    $"{target.Host} presented the host key {presented} instead of the Sandbox's {target.HostKeyFingerprint}, so Overseer didn't log in.", exception)
                : new SandboxUnreachableException($"There is no SSH connection to {target}: {exception.Message}", exception);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return client;
    }

    /// <summary>A command over its own SSH connection, which it owns.</summary>
    private sealed class SshNetProcess : ISshProcess
    {
        private const int StandardErrorKept = 4096;

        private readonly SshClient _client;
        private readonly SshCommand _command;
        private readonly SshTarget _target;
        private readonly Stream _input;
        private readonly StreamReader _output;
        private readonly Task _standardError;
        private readonly TaskCompletionSource<int> _exitCode = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StringBuilder _errorTail = new();
        private volatile Exception? _connectionError;

        public SshNetProcess(SshClient client, SshTarget target, string command)
        {
            _client = client;
            _target = target;
            _command = client.CreateCommand(command);
            // Is raised when the connection breaks while the command runs, which would otherwise look like a command that went quiet.
            client.ErrorOccurred += (_, error) => _connectionError ??= error.Exception;
            var execution = _command.BeginExecute();
            // The input stream exists only while the command executes.
            _input = _command.CreateInputStream();
            _output = new StreamReader(_command.OutputStream, new UTF8Encoding(false));
            _standardError = Task.Run(ReadStandardErrorAsync);
            _ = Task.Run(() =>
            {
                try
                {
                    _command.EndExecute(execution);
                    _exitCode.TrySetResult(_command.ExitStatus ?? -1);
                }
                catch (Exception exception)
                {
                    _exitCode.TrySetException(Lost(exception));
                }
            });
        }

        public string StandardErrorTail
        {
            get
            {
                lock (_errorTail)
                {
                    return _errorTail.ToString().Trim();
                }
            }
        }

        public Task<int> ExitCode => _exitCode.Task;

        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            try
            {
                await _input.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellationToken);
                await _input.FlushAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is ObjectDisposedException or SshException or InvalidOperationException)
            {
                throw new IOException($"The command no longer takes input: {exception.Message}", exception);
            }
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            string? line;
            try
            {
                line = await _output.ReadLineAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is SshException or ObjectDisposedException or IOException)
            {
                throw Lost(exception);
            }
            if (line is not null)
            {
                return line;
            }

            // The output ended: the command exited, or the connection broke before it could.
            await _standardError;
            if (_connectionError is { } error || !_client.IsConnected && _command.ExitStatus is null)
            {
                throw Lost(_connectionError);
            }
            await _exitCode.Task;
            return null;
        }

        private SandboxUnreachableException Lost(Exception? exception) =>
            new($"The SSH connection to {_target} was lost{(exception is null ? "." : ": " + exception.Message)}", exception);

        private async Task ReadStandardErrorAsync()
        {
            try
            {
                using var reader = new StreamReader(_command.ExtendedOutputStream, new UTF8Encoding(false));
                var buffer = new char[1024];
                int read;
                while ((read = await reader.ReadAsync(buffer)) > 0)
                {
                    lock (_errorTail)
                    {
                        _errorTail.Append(buffer, 0, read);
                        if (_errorTail.Length > StandardErrorKept)
                        {
                            _errorTail.Remove(0, _errorTail.Length - StandardErrorKept);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is SshException or ObjectDisposedException or IOException)
            {
                // The connection is gone; ReadLineAsync reports that.
            }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                // Asks the command to end, as far as the server takes a signal; closing the connection ends it for good.
                _command.CancelAsync(forceKill: true);
            }
            catch (Exception exception) when (exception is SshException or ObjectDisposedException or InvalidOperationException)
            {
            }
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
