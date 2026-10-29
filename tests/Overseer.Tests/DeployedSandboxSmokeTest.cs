using System.Net;
using System.Net.Http.Json;
using Overseer.Sandboxes;
using Overseer.Server;
using static Overseer.Tests.RealInstallation;

namespace Overseer.Tests;

/// <summary>
/// Opt-in: installs Overseer for real (see <see cref="RealInstallation"/>), has the deployed Overseer create a Sandbox,
/// run a command in it and destroy it, and uninstalls. A normal installation can't be made to create a Sandbox this
/// way: the test enables the smoke-test endpoints on the Container App itself. It takes about an hour and costs a little.
/// </summary>
public class DeployedSandboxSmokeTest
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task The_deployed_Overseer_creates_a_Sandbox_without_a_public_IP_that_reaches_the_internet_runs_a_command_in_it_and_destroys_it()
    {
        await using var installation = new RealInstallation();
        var output = await installation.InstallAsync();
        var credentials = ResetPasswordOutput.Parse(output);
        await AssertTheManagedIdentitysOnlyRoleIsContributorOnTheSandboxResourceGroupAsync(installation);
        await AssertPeeredBothWaysAsync(installation);

        await AzAsync(
            "containerapp", "update", "--resource-group", installation.ResourceGroup, "--name", installation.Prefix,
            "--set-env-vars", "OVERSEER_SMOKETEST__ENABLED=true", "--output", "none");
        using var browser = new PortalBrowser(PortalUrl(output));
        var sandbox = await CreateSandboxAsync(browser, credentials);
        var ready = await WaitForAsync(browser, sandbox, "Ready", TimeSpan.FromMinutes(35));
        var vm = ready.ResourceId!.Split('/')[^1];

        // Nothing on the internet reaches it: no public IP, and SSH only from Overseer's subnet.
        Assert.Empty((await AzAsync(
            "vm", "show", "--show-details", "--resource-group", installation.SandboxResourceGroup, "--name", vm, "--query", "publicIps", "--output", "tsv")).Trim());
        Assert.Equal(["sandboxes-nat-ip"], Lines(await AzAsync(
            "network", "public-ip", "list", "--resource-group", installation.SandboxResourceGroup, "--query", "[].name", "--output", "tsv")));
        Assert.Equal([AzureSandboxProvider.OverseerSubnet], Lines(await AzAsync(
            "network", "nsg", "rule", "list", "--resource-group", installation.SandboxResourceGroup, "--nsg-name", $"{vm}-nsg",
            "--query", "[?direction=='Inbound' && access=='Allow'].sourceAddressPrefix", "--output", "tsv")));

        // Overseer reaches it by private IP, and it reaches the internet through the NAT Gateway.
        using var ran = await browser.Http.PostAsJsonAsync($"{sandbox}/commands", new { Command = "curl -fsS https://api.ipify.org" }, TestContext.Current.CancellationToken);
        ran.EnsureSuccessStatusCode();
        var result = await ran.Content.ReadFromJsonAsync<CommandResult>(TestContext.Current.CancellationToken);
        Assert.True(result!.ExitCode == 0, result.Output);
        var natAddress = (await AzAsync(
            "network", "public-ip", "show", "--resource-group", installation.SandboxResourceGroup, "--name", "sandboxes-nat-ip",
            "--query", "ipAddress", "--output", "tsv")).Trim();
        Assert.Equal(natAddress, result.Output.Trim());

        using var destroyed = await browser.Http.DeleteAsync(sandbox, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, destroyed.StatusCode);
        await WaitForAsync(browser, sandbox, "Destroyed", TimeSpan.FromMinutes(15));
        Assert.Empty(Lines(await AzAsync(
            "resource", "list", "--resource-group", installation.SandboxResourceGroup, "--query", $"[?starts_with(name, '{vm}')].name", "--output", "tsv")));

        await installation.UninstallAsync();
    }

    private static async Task AssertTheManagedIdentitysOnlyRoleIsContributorOnTheSandboxResourceGroupAsync(RealInstallation installation)
    {
        var principalId = await installation.IdentityPrincipalIdAsync();
        var assignments = Lines(await AzAsync(
            "role", "assignment", "list", "--all", "--fill-principal-name", "false",
            "--query", $"[?principalId=='{principalId}'].[roleDefinitionName, scope] | [].join(' ', @)", "--output", "tsv"));
        var assignment = Assert.Single(assignments);
        Assert.StartsWith("Contributor ", assignment);
        Assert.EndsWith($"/resourceGroups/{installation.SandboxResourceGroup}", assignment, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertPeeredBothWaysAsync(RealInstallation installation)
    {
        foreach (var (group, vnet) in new[] { (installation.ResourceGroup, $"{installation.Prefix}-vnet"), (installation.SandboxResourceGroup, "sandboxes") })
        {
            Assert.Equal(["Connected"], Lines(await AzAsync(
                "network", "vnet", "peering", "list", "--resource-group", group, "--vnet-name", vnet, "--query", "[].peeringState", "--output", "tsv")));
        }
    }

    /// <summary>
    /// Starts creating a Sandbox once the revision with the smoke-test endpoints serves, logging in to it: the revision
    /// before has neither the endpoints nor the keys to its login cookie. Returns the Sandbox's address.
    /// </summary>
    private static async Task<string> CreateSandboxAsync(PortalBrowser browser, ResetPasswordOutput credentials)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15);
        while (true)
        {
            await browser.LogInAsync(credentials.Username, credentials.Password);
            using var created = await browser.Http.PostAsync("/smoke-test/sandboxes", null, TestContext.Current.CancellationToken);
            if (created.StatusCode == HttpStatusCode.Accepted)
            {
                return created.Headers.Location!.ToString();
            }
            Assert.True(DateTimeOffset.UtcNow < deadline, $"The smoke-test endpoints did not come up: creating a Sandbox still gives {created.StatusCode}.");
            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<SmokeTestSandbox> WaitForAsync(PortalBrowser browser, string sandbox, string state, TimeSpan within)
    {
        var deadline = DateTimeOffset.UtcNow + within;
        while (true)
        {
            var status = await browser.Http.GetFromJsonAsync<SmokeTestSandbox>(sandbox, TestContext.Current.CancellationToken);
            Assert.True(status!.State != "Failed", status.Error);
            if (status.State == state)
            {
                return status;
            }
            Assert.True(DateTimeOffset.UtcNow < deadline, $"The Sandbox is still {status.State}, not {state}, after {within.TotalMinutes:0} minutes.");
            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        }
    }
}
