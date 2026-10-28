using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Overseer.Installer;

/// <summary>
/// Runs the installer's bash scripts, embedded in the executable, which manage Azure only through the Azure CLI.
/// A script is a template: each <c>{{Name}}</c> in it becomes the value of that parameter as a quoted shell word, so
/// scripts assign them to variables (<c>location={{Location}}</c>) and use only the variables.
/// </summary>
internal sealed partial class AzureScripts(IReadOnlyDictionary<string, string?> environment)
{
    /// <summary>Runs <c>Scripts/<paramref name="name"/>.sh</c> and returns what it printed.</summary>
    /// <exception cref="AzureScriptException">The script failed; the exception holds what it printed to stderr.</exception>
    public async Task<string> RunAsync(string name, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            ArgumentList = { "-c", Render(name, parameters), name },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // The environment map is the only environment, as everywhere else in the CLI.
        startInfo.Environment.Clear();
        foreach (var (variable, value) in environment)
        {
            startInfo.Environment[variable] = value;
        }

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return process.ExitCode == 0 ? await output : throw new AzureScriptException(await error);
    }

    private static string Render(string name, IReadOnlyDictionary<string, string> parameters)
    {
        using var stream = typeof(AzureScripts).Assembly.GetManifestResourceStream($"Overseer.Installer.Scripts.{name}.sh")
            ?? throw new ArgumentException($"There is no installer script {name}.", nameof(name));
        var template = new StreamReader(stream).ReadToEnd();
        return Placeholder().Replace(template, placeholder => parameters.TryGetValue(placeholder.Groups[1].Value, out var value)
            ? ShellWord(value)
            : throw new ArgumentException($"The installer script {name} needs the parameter {placeholder.Groups[1].Value}.", nameof(parameters)));
    }

    /// <summary>Single quotes keep every character literal; a quote inside ends the word, adds an escaped quote and resumes.</summary>
    private static string ShellWord(string value) => $"'{value.Replace("'", @"'\''")}'";

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex Placeholder();
}

/// <summary>An installer script failed. <see cref="Exception.Message"/> is what it printed to stderr, which includes the Azure error.</summary>
internal sealed class AzureScriptException(string error) : Exception(error.Trim());
