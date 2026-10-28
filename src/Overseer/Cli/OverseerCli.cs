using System.Collections;
using System.CommandLine;
using System.CommandLine.Invocation;
using Overseer.Server;

namespace Overseer.Cli;

/// <summary>The <c>overseer</c> command line: one executable for the server and the installer (ADR 0004).</summary>
public static class OverseerCli
{
    public static Task<int> InvokeAsync(string[] args, TextWriter output, TextWriter error)
    {
        var serve = new Command("serve", "Run the Portal and the orchestration.");
        serve.SetAction(async (_, cancellationToken) =>
        {
            try
            {
                await using var app = await OverseerServer.StartAsync(ProcessEnvironment(), cancellationToken);
                await app.WaitForShutdownAsync(cancellationToken);
                return 0;
            }
            catch (MissingConfigurationException exception)
            {
                await error.WriteLineAsync(exception.Message);
                return 1;
            }
        });

        var root = new RootCommand("Overseer: direct a team of AI agents from Intent to merged pull request.")
        {
            serve,
            NotImplementedYet("install", "Install or upgrade Overseer in Azure.", error),
            NotImplementedYet("uninstall", "Remove Overseer from Azure.", error),
            NotImplementedYet("reset-password", "Generate a new password for the Overseer user.", error),
        };
        // The built-in --version reports the entry assembly, which is not Overseer when it runs in-process (tests).
        root.Options.OfType<VersionOption>().Single().Action = new PrintVersion();

        return root.Parse(args).InvokeAsync(new InvocationConfiguration
        {
            Output = output,
            Error = error,
            // The server's host handles Ctrl+C and SIGTERM itself, so shutdown is never cut short.
            ProcessTerminationTimeout = null,
        });
    }

    private static Command NotImplementedYet(string name, string description, TextWriter error)
    {
        var command = new Command(name, description);
        command.SetAction(_ =>
        {
            error.WriteLine($"`overseer {name}` is not implemented yet.");
            return 1;
        });
        return command;
    }

    private sealed class PrintVersion : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            parseResult.InvocationConfiguration.Output.WriteLine(OverseerVersion.Current);
            return 0;
        }
    }

    private static Dictionary<string, string?> ProcessEnvironment() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(variable => (string)variable.Key, variable => (string?)variable.Value);
}
