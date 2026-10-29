using System.Diagnostics;
using System.Text.RegularExpressions;
using Overseer.Cli;

namespace Overseer.Tests;

/// <summary>
/// Opt-in: installs Overseer for real, into the subscription your Azure CLI is logged in to, when
/// <c>TEST_INSTALL_VERSION</c> names a released version (its image must be published). It uses a fresh prefix,
/// logs in to the Portal, runs install again as an upgrade would, and removes everything it created afterwards. It takes about half an hour and costs a little.
/// </summary>
public class InstallSmokeTest : IDisposable
{
    private const string VersionVariable = "TEST_INSTALL_VERSION";
    private const string RegionVariable = "TEST_INSTALL_REGION";

    private readonly string _directory = Directory.CreateTempSubdirectory("overseer-install-").FullName;
    private readonly string _prefix = $"smoke{Guid.NewGuid():N}"[..12];

    [Fact]
    public async Task Install_into_a_real_subscription_gives_a_Portal_the_printed_credentials_log_in_to_also_after_running_it_again()
    {
        var version = Environment.GetEnvironmentVariable(VersionVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(version), $"Set {VersionVariable} to a released version to install it into your current Azure subscription.");
        var region = Environment.GetEnvironmentVariable(RegionVariable) is { Length: > 0 } configured ? configured : "westeurope";
        // Your own config file stays out of it, so the default username applies.
        var environment = new Dictionary<string, string?>(OverseerCli.ProcessEnvironment())
        {
            ["OVERSEER_CONFIG_FILE"] = Path.Combine(_directory, "config.json"),
        };

        try
        {
            var output = new StringWriter();
            var exitCode = await OverseerCli.InvokeAsync(["install", "--prefix", _prefix, "--region", region], environment, output, output, version);

            Assert.True(exitCode == 0, output.ToString());
            var portal = new Uri(Regex.Match(output.ToString(), @"^\s*Portal:\s*(\S+)\s*$", RegexOptions.Multiline).Groups[1].Value);
            var credentials = ResetPasswordOutput.Parse(output.ToString());
            using (var browser = new PortalBrowser(portal))
            {
                await browser.LogInAsync(credentials.Username, credentials.Password);
                Assert.True(await browser.IsLoggedInAsync());
            }

            // Every resource exists now, so this checks them all, keeps the password and, at the same version, changes nothing.
            var revision = await LatestRevisionAsync();
            var again = new StringWriter();
            exitCode = await OverseerCli.InvokeAsync(["install", "--prefix", _prefix], environment, again, again, version);

            Assert.True(exitCode == 0, again.ToString());
            Assert.Contains("password is kept", again.ToString());
            Assert.Equal(revision, await LatestRevisionAsync());
            using (var browser = new PortalBrowser(portal))
            {
                await browser.LogInAsync(credentials.Username, credentials.Password);
                Assert.True(await browser.IsLoggedInAsync());
            }
        }
        finally
        {
            await RemoveInstallationAsync();
        }
    }

    private Task<string> LatestRevisionAsync() =>
        AzAsync("containerapp", "show", "--resource-group", $"{_prefix}-rg", "--name", _prefix, "--query", "properties.latestRevisionName", "--output", "tsv");

    /// <summary>Deletes both resource groups and purges the soft-deleted Key Vault, so its name is free again.</summary>
    private async Task RemoveInstallationAsync()
    {
        var vaults = await AzAsync("keyvault", "list", "--resource-group", $"{_prefix}-rg", "--query", "[].name", "--output", "tsv");
        await Task.WhenAll(
            AzAsync("group", "delete", "--name", $"{_prefix}-rg", "--yes"),
            AzAsync("group", "delete", "--name", $"{_prefix}-sandboxes-rg", "--yes"));
        foreach (var vault in vaults.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await AzAsync("keyvault", "purge", "--name", vault);
        }
    }

    /// <summary>Runs the real Azure CLI; a failure (such as a resource group that was never created) only returns no output.</summary>
    private static async Task<string> AzAsync(params string[] args)
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

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
