using System.Collections;
using System.CommandLine;
using System.CommandLine.Invocation;
using Overseer.Installer;
using Overseer.Server;

namespace Overseer.Cli;

/// <summary>The <c>overseer</c> command line: one executable for the server and the installer (ADR 0004).</summary>
public static class OverseerCli
{
    /// <summary>
    /// Runs one command. <paramref name="environment"/> is the only environment it reads, so tests can run in parallel.
    /// It reads no input: a command that asks for confirmation gets none.
    /// </summary>
    public static Task<int> InvokeAsync(string[] args, IReadOnlyDictionary<string, string?> environment, TextWriter output, TextWriter error) =>
        InvokeAsync(args, environment, TextReader.Null, output, error, OverseerVersion.Current);

    /// <summary>Runs one command, reading what the user types, such as a confirmation, from <paramref name="input"/>.</summary>
    /// <inheritdoc cref="InvokeAsync(string[], IReadOnlyDictionary{string, string?}, TextWriter, TextWriter)"/>
    public static Task<int> InvokeAsync(
        string[] args, IReadOnlyDictionary<string, string?> environment, TextReader input, TextWriter output, TextWriter error) =>
        InvokeAsync(args, environment, input, output, error, OverseerVersion.Current);

    /// <summary>Runs one command as the executable of <paramref name="version"/> would, such as a release build in tests.</summary>
    /// <inheritdoc cref="InvokeAsync(string[], IReadOnlyDictionary{string, string?}, TextWriter, TextWriter)"/>
    public static Task<int> InvokeAsync(
        string[] args, IReadOnlyDictionary<string, string?> environment, TextWriter output, TextWriter error, string version) =>
        InvokeAsync(args, environment, TextReader.Null, output, error, version);

    private static Task<int> InvokeAsync(
        string[] args, IReadOnlyDictionary<string, string?> environment, TextReader input, TextWriter output, TextWriter error, string version)
    {
        var serve = new Command("serve", "Run the Portal and the orchestration.");
        serve.SetAction(async (_, cancellationToken) =>
        {
            try
            {
                await using var app = await OverseerServer.StartAsync(environment, cancellationToken);
                await app.WaitForShutdownAsync(cancellationToken);
                return 0;
            }
            catch (MissingConfigurationException exception)
            {
                await error.WriteLineAsync(exception.Message);
                return 1;
            }
        });

        var region = new Option<string>("--region")
        {
            Description = $"The Azure region to install into: {Install.DefaultRegion} when left out. An existing installation stays in its region.",
        };
        var prefix = new Option<string>("--prefix")
        {
            Description = "Starts the name of every resource Overseer creates; it identifies the installation.",
            DefaultValueFactory = _ => InstallationNames.DefaultPrefix,
        };
        var install = new Command("install", "Install Overseer in Azure.") { region, prefix };
        install.SetAction((parseResult, cancellationToken) => Install.RunAsync(
            parseResult.GetValue(region), parseResult.GetRequiredValue(prefix), version, environment, output, error, cancellationToken));

        var local = new Option<bool>("--local")
        {
            Description = "Reset the password of an Overseer you run yourself with `serve`: its hash goes into the local config file.",
        };
        var resetPassword = new Command("reset-password", "Generate a new password for the Overseer user, of an installation in Azure unless --local.")
        {
            prefix,
            local,
        };
        resetPassword.Validators.Add(result =>
        {
            if (result.GetValue(local) && result.GetResult(prefix) is { Implicit: false })
            {
                result.AddError("--prefix names an installation in Azure, so it can't go with --local.");
            }
        });
        resetPassword.SetAction((parseResult, cancellationToken) => parseResult.GetValue(local)
            ? LocalResetPassword.RunAsync(environment, output, error)
            : ResetPassword.RunAsync(parseResult.GetRequiredValue(prefix), environment, output, error, cancellationToken));

        var yes = new Option<bool>("--yes")
        {
            Description = "Remove without asking you to type the prefix first, for scripts.",
        };
        var uninstall = new Command("uninstall", "Remove Overseer from Azure: everything install created, and nothing else.") { prefix, yes };
        uninstall.SetAction((parseResult, cancellationToken) => Uninstall.RunAsync(
            parseResult.GetRequiredValue(prefix), parseResult.GetValue(yes), environment, input, output, error, cancellationToken));

        var root = new RootCommand("Overseer: direct a team of AI agents from Intent to merged pull request.")
        {
            serve,
            install,
            uninstall,
            resetPassword,
        };
        // The built-in --version reports the entry assembly, which is not Overseer when it runs in-process (tests).
        root.Options.OfType<VersionOption>().Single().Action = new PrintVersion(version);

        return root.Parse(args).InvokeAsync(new InvocationConfiguration
        {
            Output = output,
            Error = error,
            // The server's host handles Ctrl+C and SIGTERM itself, so shutdown is never cut short.
            ProcessTerminationTimeout = null,
        });
    }

    private sealed class PrintVersion(string version) : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            parseResult.InvocationConfiguration.Output.WriteLine(version);
            return 0;
        }
    }

    public static Dictionary<string, string?> ProcessEnvironment() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(variable => (string)variable.Key, variable => (string?)variable.Value);
}
