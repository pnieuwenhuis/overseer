using System.Diagnostics;
using System.Text.RegularExpressions;
using Overseer.Cli;

namespace Overseer.Tests;

/// <summary>
/// For the opt-in smoke tests: an installation, with a fresh prefix, in the subscription your Azure CLI is logged in
/// to, of the released version <c>TEST_INSTALL_VERSION</c> names (its image must be published), in
/// <c>TEST_INSTALL_REGION</c> or westeurope. Disposing it removes whatever a failure left behind.
/// </summary>
public sealed class RealInstallation : IAsyncDisposable
{
    private const string VersionVariable = "TEST_INSTALL_VERSION";
    private const string RegionVariable = "TEST_INSTALL_REGION";

    private readonly string _directory = Directory.CreateTempSubdirectory("overseer-install-").FullName;

    /// <summary>Skips the test unless <c>TEST_INSTALL_VERSION</c> names a version to install.</summary>
    public RealInstallation()
    {
        var version = System.Environment.GetEnvironmentVariable(VersionVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(version), $"Set {VersionVariable} to a released version to install it into your current Azure subscription.");
        Version = version;
        Region = System.Environment.GetEnvironmentVariable(RegionVariable) is { Length: > 0 } region ? region : "westeurope";
        // Your own config file stays out of it, so the default username applies.
        Environment = new Dictionary<string, string?>(OverseerCli.ProcessEnvironment())
        {
            ["OVERSEER_CONFIG_FILE"] = Path.Combine(_directory, "config.json"),
        };
    }

    public string Prefix { get; } = $"smoke{Guid.NewGuid():N}"[..12];
    public string Version { get; }
    public string Region { get; }
    public Dictionary<string, string?> Environment { get; }

    public string ResourceGroup => $"{Prefix}-rg";
    public string SandboxResourceGroup => $"{Prefix}-sandboxes-rg";

    /// <summary>Runs <c>overseer</c> with <paramref name="args"/> as the released version would, and returns what it printed.</summary>
    public async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var exitCode = await OverseerCli.InvokeAsync(args, Environment, output, output, Version);
        return (exitCode, output.ToString());
    }

    /// <summary>Installs, or runs install again, and checks that it succeeded.</summary>
    public async Task<string> InstallAsync(bool withRegion = true)
    {
        var (exitCode, output) = await RunAsync(["install", "--prefix", Prefix, .. withRegion ? new[] { "--region", Region } : []]);
        Assert.True(exitCode == 0, output);
        return output;
    }

    public static Uri PortalUrl(string installOutput) =>
        new(Regex.Match(installOutput, @"^\s*Portal:\s*(\S+)\s*$", RegexOptions.Multiline).Groups[1].Value);

    /// <summary>The Managed Identity's principal ID.</summary>
    public async Task<string> IdentityPrincipalIdAsync() =>
        (await AzAsync("identity", "show", "--resource-group", ResourceGroup, "--name", $"{Prefix}-identity", "--query", "principalId", "--output", "tsv")).Trim();

    /// <summary>
    /// Uninstalls without asking, and checks that the Managed Identity's role assignments and both resource groups are
    /// gone and the Key Vault is purged.
    /// </summary>
    public async Task UninstallAsync()
    {
        var principalId = await IdentityPrincipalIdAsync();
        Assert.NotEmpty(principalId);

        var (exitCode, output) = await RunAsync("uninstall", "--prefix", Prefix, "--yes");

        Assert.True(exitCode == 0, output);
        Assert.Empty((await AzAsync(
            "role", "assignment", "list", "--all", "--fill-principal-name", "false", "--query", $"[?principalId=='{principalId}'].id", "--output", "tsv")).Trim());
        Assert.Equal("false", (await AzAsync("group", "exists", "--name", ResourceGroup)).Trim());
        Assert.Equal("false", (await AzAsync("group", "exists", "--name", SandboxResourceGroup)).Trim());
        Assert.Empty(await DeletedKeyVaultsAsync());
    }

    /// <summary>The installation's Key Vaults that are soft-deleted, and not yet purged.</summary>
    private async Task<string[]> DeletedKeyVaultsAsync() =>
        Lines(await AzAsync("keyvault", "list-deleted", "--resource-type", "vault", "--query", $"[?starts_with(name, '{Prefix}-kv-')].name", "--output", "tsv"));

    /// <summary>Runs the real Azure CLI; a failure (such as a resource group that was never created) only returns no output.</summary>
    public static async Task<string> AzAsync(params string[] args)
    {
        var startInfo = new ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 ? await output : "";
    }

    public static string[] Lines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Deletes both resource groups and purges the soft-deleted Key Vault, so its name is free again, without relying on
    /// uninstall: this cleans up after it too. Nothing is left when the test passes.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(
            AzAsync("group", "delete", "--name", ResourceGroup, "--yes"),
            AzAsync("group", "delete", "--name", SandboxResourceGroup, "--yes"));
        foreach (var vault in await DeletedKeyVaultsAsync())
        {
            await AzAsync("keyvault", "purge", "--name", vault);
        }
        Directory.Delete(_directory, recursive: true);
    }
}
