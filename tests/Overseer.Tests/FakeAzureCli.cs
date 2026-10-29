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
        // A response given in turns answers with its next output each time, and with its last one once they run out.
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
              if [ -e "$match.turns" ]; then
                turn=$(( $(cat "$match.turn") + 1 ))
                [ "$turn" -le "$(cat "$match.turns")" ] || turn=$(cat "$match.turns")
                echo "$turn" > "$match.turn"
                cat "$match.out.$turn"
              else
                cat "$match.out"
              fi
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

    /// <summary>
    /// Answers later calls whose arguments start with <paramref name="command"/> with each of <paramref name="outputs"/>
    /// in turn, then with the last one, as Azure answers before and after a change.
    /// </summary>
    public FakeAzureCli RespondInTurn(string[] command, params string[] outputs)
    {
        Respond(command);
        var path = Path.Combine(_directory, "responses", $"{_responses:D4}");
        for (var turn = 1; turn <= outputs.Length; turn++)
        {
            File.WriteAllText($"{path}.out.{turn}", outputs[turn - 1]);
        }
        File.WriteAllText(path + ".turns", outputs.Length.ToString());
        File.WriteAllText(path + ".turn", "0");
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

    /// <summary>The calls that start with <paramref name="command"/>, such as <c>containerapp create</c>.</summary>
    public IReadOnlyList<IReadOnlyList<string>> CallsTo(params string[] command) =>
        Calls.Where(call => call.Take(command.Length).SequenceEqual(command)).ToList();

    /// <summary>The one call that starts with <paramref name="command"/>.</summary>
    public IReadOnlyList<string> CallTo(params string[] command) => Assert.Single(CallsTo(command));

    /// <summary>The calls that change something in Azure, as opposed to looking.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Changes =>
        Calls.Where(call => Command(call).Split(' ').Last() is not ("list" or "list-deleted" or "show" or "exists")).ToList();

    /// <summary>The command a call runs, such as <c>containerapp env create</c>: its arguments up to the first option.</summary>
    public static string Command(IReadOnlyList<string> call) => string.Join(' ', call.TakeWhile(argument => !argument.StartsWith("--")));

    /// <summary>The values that follow <paramref name="option"/> in <paramref name="call"/>, up to the next option.</summary>
    public static IReadOnlyList<string> Values(IReadOnlyList<string> call, string option) =>
        call.SkipWhile(argument => argument != option).Skip(1).TakeWhile(argument => !argument.StartsWith("--")).ToList();

    /// <summary>The one value that follows <paramref name="option"/> in <paramref name="call"/>.</summary>
    public static string Value(IReadOnlyList<string> call, string option) => Assert.Single(Values(call, option));

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
