namespace Overseer.Tests;

/// <summary>
/// An <c>az</c> executable that records every call and answers from scripted responses, for running the installer's
/// scripts without Azure. Hand <see cref="Environment"/> to the CLI so its scripts find this <c>az</c> first.
/// </summary>
public sealed class FakeAzureCli : IDisposable
{
    // Arguments are separated by the ASCII unit separator and calls by the record separator; neither occurs in arguments.
    private const char ArgumentSeparator = '\x1f';
    private const char CallSeparator = '\x1e';

    private readonly string _directory = Directory.CreateTempSubdirectory("overseer-az-").FullName;
    private int _responses;

    public FakeAzureCli()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "bin"));
        Directory.CreateDirectory(Path.Combine(_directory, "responses"));
        var az = Path.Combine(_directory, "bin", "az");
        // The last response whose arguments start the call answers it; without one, the call succeeds without output.
        File.WriteAllText(az, $$"""
            #!/bin/bash
            dir='{{_directory}}'
            call=$(printf '%s\x1f' "$@")
            printf '%s\x1e' "$call" >> "$dir/calls"
            match=
            for response in "$dir"/responses/*.args; do
              [ -e "$response" ] || continue
              prefix=$(cat "$response")
              case "$call" in "$prefix"*) match=${response%.args} ;; esac
            done
            if [ -n "$match" ]; then
              cat "$match.out"
              cat "$match.err" >&2
              exit "$(cat "$match.code")"
            fi
            """);
        // The installer runs bash, so these tests run on macOS and Linux only.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(az, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// The environment for the CLI: this <c>az</c> first on the PATH, then the system's tools. With
    /// <paramref name="installed"/> false there is no <c>az</c> at all, not even one the machine has.
    /// </summary>
    public Dictionary<string, string?> Environment(bool installed = true) => new()
    {
        ["PATH"] = installed ? $"{Path.Combine(_directory, "bin")}:/usr/bin:/bin" : Path.Combine(_directory, "empty"),
    };

    /// <summary>Answers every later call whose arguments start with <paramref name="command"/>, overriding earlier responses.</summary>
    public FakeAzureCli Respond(string[] command, string output = "", string error = "", int exitCode = 0)
    {
        var path = Path.Combine(_directory, "responses", $"{++_responses:D4}");
        File.WriteAllText(path + ".args", string.Concat(command.Select(argument => argument + ArgumentSeparator)));
        File.WriteAllText(path + ".out", output);
        File.WriteAllText(path + ".err", error);
        File.WriteAllText(path + ".code", exitCode.ToString());
        return this;
    }

    /// <summary>Fails every later call whose arguments start with <paramref name="command"/>, the way <c>az</c> reports an Azure error.</summary>
    public FakeAzureCli Fail(string[] command, string error) => Respond(command, error: error + "\n", exitCode: 1);

    /// <summary>Every call so far, in order, as its arguments.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Calls
    {
        get
        {
            var file = Path.Combine(_directory, "calls");
            return File.Exists(file)
                ? File.ReadAllText(file).Split(CallSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Select(call => call.Split(ArgumentSeparator)[..^1])
                    .ToList()
                : [];
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
