using System.Diagnostics;
using System.Text.RegularExpressions;
using Overseer.Cli;

namespace Overseer.Tests;

/// <summary>
/// Opt-in: installs Overseer for real, into the subscription your Azure CLI is logged in to, when
/// <c>TEST_INSTALL_VERSION</c> names a released version (its image must be published). It uses a fresh prefix,
/// logs in to the Portal, resets the password, runs install again as an upgrade would, uninstalls, installs again with
/// the same prefix, and uninstalls once more. Whatever is left after a failure is removed afterwards. It takes about an
/// hour and a half and costs a little.
/// </summary>
public class InstallSmokeTest : IDisposable
{
    private const string VersionVariable = "TEST_INSTALL_VERSION";
    private const string RegionVariable = "TEST_INSTALL_REGION";

    private readonly string _directory = Directory.CreateTempSubdirectory("overseer-install-").FullName;
    private readonly string _prefix = $"smoke{Guid.NewGuid():N}"[..12];

    [Fact]
    public async Task Install_into_a_real_subscription_gives_a_Portal_the_printed_credentials_log_in_to_also_after_a_reset_running_it_again_and_reinstalling_after_uninstall()
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
            var portal = PortalUrl(output.ToString());
            var installed = ResetPasswordOutput.Parse(output.ToString());
            using (var browser = new PortalBrowser(portal))
            {
                await browser.LogInAsync(installed.Username, installed.Password);
                Assert.True(await browser.IsLoggedInAsync());
            }

            var reset = new StringWriter();
            exitCode = await OverseerCli.InvokeAsync(["reset-password", "--prefix", _prefix], environment, reset, reset, version);

            Assert.True(exitCode == 0, reset.ToString());
            var credentials = ResetPasswordOutput.Parse(reset.ToString());
            using (var browser = new PortalBrowser(portal))
            {
                await browser.LogInAsync(installed.Username, installed.Password);
                Assert.False(await browser.IsLoggedInAsync());
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

            await UninstallAsync(environment, version);

            // Only a purged Key Vault frees its name, which the same prefix gives the new installation's vault.
            var reinstall = new StringWriter();
            exitCode = await OverseerCli.InvokeAsync(["install", "--prefix", _prefix, "--region", region], environment, reinstall, reinstall, version);

            Assert.True(exitCode == 0, reinstall.ToString());
            var reinstalled = ResetPasswordOutput.Parse(reinstall.ToString());
            using (var browser = new PortalBrowser(PortalUrl(reinstall.ToString())))
            {
                await browser.LogInAsync(reinstalled.Username, reinstalled.Password);
                Assert.True(await browser.IsLoggedInAsync());
            }

            await UninstallAsync(environment, version);
        }
        finally
        {
            await RemoveInstallationAsync();
        }
    }

    private static Uri PortalUrl(string installOutput) =>
        new(Regex.Match(installOutput, @"^\s*Portal:\s*(\S+)\s*$", RegexOptions.Multiline).Groups[1].Value);

    /// <summary>
    /// Uninstalls without asking, and checks that the Managed Identity's role assignments and both resource groups are
    /// gone and the Key Vault is purged.
    /// </summary>
    private async Task UninstallAsync(Dictionary<string, string?> environment, string version)
    {
        var principalId = (await AzAsync(
            "identity", "show", "--resource-group", $"{_prefix}-rg", "--name", $"{_prefix}-identity", "--query", "principalId", "--output", "tsv")).Trim();
        Assert.NotEmpty(principalId);
        var output = new StringWriter();
        var exitCode = await OverseerCli.InvokeAsync(["uninstall", "--prefix", _prefix, "--yes"], environment, output, output, version);

        Assert.True(exitCode == 0, output.ToString());
        Assert.Empty((await AzAsync(
            "role", "assignment", "list", "--all", "--fill-principal-name", "false", "--query", $"[?principalId=='{principalId}'].id", "--output", "tsv")).Trim());
        Assert.Equal("false", (await AzAsync("group", "exists", "--name", $"{_prefix}-rg")).Trim());
        Assert.Equal("false", (await AzAsync("group", "exists", "--name", $"{_prefix}-sandboxes-rg")).Trim());
        Assert.Empty(await DeletedKeyVaultsAsync());
    }

    /// <summary>The installation's Key Vaults that are soft-deleted, and not yet purged.</summary>
    private async Task<string[]> DeletedKeyVaultsAsync() =>
        (await AzAsync("keyvault", "list-deleted", "--resource-type", "vault", "--query", $"[?starts_with(name, '{_prefix}-kv-')].name", "--output", "tsv"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private Task<string> LatestRevisionAsync() =>
        AzAsync("containerapp", "show", "--resource-group", $"{_prefix}-rg", "--name", _prefix, "--query", "properties.latestRevisionName", "--output", "tsv");

    /// <summary>
    /// Deletes both resource groups and purges the soft-deleted Key Vault, so its name is free again, without relying on
    /// uninstall: this cleans up after it too. Nothing is left when the test passes.
    /// </summary>
    private async Task RemoveInstallationAsync()
    {
        await Task.WhenAll(
            AzAsync("group", "delete", "--name", $"{_prefix}-rg", "--yes"),
            AzAsync("group", "delete", "--name", $"{_prefix}-sandboxes-rg", "--yes"));
        foreach (var vault in await DeletedKeyVaultsAsync())
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
