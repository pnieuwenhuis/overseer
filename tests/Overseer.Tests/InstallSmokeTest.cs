namespace Overseer.Tests;

/// <summary>
/// Opt-in: installs Overseer for real (see <see cref="RealInstallation"/>), logs in to the Portal, resets the password,
/// runs install again as an upgrade would, uninstalls, installs again with the same prefix, and uninstalls once more.
/// It takes about an hour and a half and costs a little.
/// </summary>
public class InstallSmokeTest
{
    [Fact]
    public async Task Install_into_a_real_subscription_gives_a_Portal_the_printed_credentials_log_in_to_also_after_a_reset_running_it_again_and_reinstalling_after_uninstall()
    {
        await using var installation = new RealInstallation();

        var output = await installation.InstallAsync();

        var portal = RealInstallation.PortalUrl(output);
        var installed = ResetPasswordOutput.Parse(output);
        using (var browser = new PortalBrowser(portal))
        {
            await browser.LogInAsync(installed.Username, installed.Password);
            Assert.True(await browser.IsLoggedInAsync());
        }

        var (exitCode, reset) = await installation.RunAsync("reset-password", "--prefix", installation.Prefix);

        Assert.True(exitCode == 0, reset);
        var credentials = ResetPasswordOutput.Parse(reset);
        using (var browser = new PortalBrowser(portal))
        {
            await browser.LogInAsync(installed.Username, installed.Password);
            Assert.False(await browser.IsLoggedInAsync());
            await browser.LogInAsync(credentials.Username, credentials.Password);
            Assert.True(await browser.IsLoggedInAsync());
        }

        // Every resource exists now, so this checks them all, keeps the password and, at the same version, changes nothing.
        var revision = await LatestRevisionAsync(installation);
        var again = await installation.InstallAsync(withRegion: false);

        Assert.Contains("password is kept", again);
        Assert.Equal(revision, await LatestRevisionAsync(installation));
        using (var browser = new PortalBrowser(portal))
        {
            await browser.LogInAsync(credentials.Username, credentials.Password);
            Assert.True(await browser.IsLoggedInAsync());
        }

        await installation.UninstallAsync();

        // Only a purged Key Vault frees its name, which the same prefix gives the new installation's vault.
        var reinstall = await installation.InstallAsync();

        var reinstalled = ResetPasswordOutput.Parse(reinstall);
        using (var browser = new PortalBrowser(RealInstallation.PortalUrl(reinstall)))
        {
            await browser.LogInAsync(reinstalled.Username, reinstalled.Password);
            Assert.True(await browser.IsLoggedInAsync());
        }

        await installation.UninstallAsync();
    }

    private static Task<string> LatestRevisionAsync(RealInstallation installation) => RealInstallation.AzAsync(
        "containerapp", "show", "--resource-group", installation.ResourceGroup, "--name", installation.Prefix,
        "--query", "properties.latestRevisionName", "--output", "tsv");
}
