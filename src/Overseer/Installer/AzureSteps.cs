namespace Overseer.Installer;

/// <summary>Runs the installer's scripts as the steps of a command, saying what each one does before it starts.</summary>
internal sealed class AzureSteps(AzureScripts azure, TextWriter output, CancellationToken cancellationToken)
{
    /// <summary>Runs <paramref name="script"/> and returns the lines it printed.</summary>
    /// <exception cref="StepFailedException">The script failed.</exception>
    public async Task<string[]> RunAsync(string description, string script, Dictionary<string, string> parameters)
    {
        await output.WriteLineAsync($"{description}...");
        try
        {
            return Lines(await azure.RunAsync(script, parameters, cancellationToken));
        }
        catch (AzureScriptException exception)
        {
            throw new StepFailedException(description, exception.Message);
        }
    }

    public static string[] Lines(string output) => output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static string Indent(string text) => string.Join(Environment.NewLine, text.Split('\n').Select(line => "    " + line.TrimEnd()));
}

/// <summary>A step of an installer command failed, with the Azure error its script reported.</summary>
internal sealed class StepFailedException(string step, string azureError) : Exception($"{step}: {azureError}")
{
    public string Step { get; } = step;
    public string AzureError { get; } = azureError;
}
