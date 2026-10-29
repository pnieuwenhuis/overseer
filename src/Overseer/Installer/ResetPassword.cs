using Overseer.Login;

namespace Overseer.Installer;

/// <summary>
/// <c>overseer reset-password</c> for an installation in Azure: a new password whose hash replaces the Container App's
/// password-hash secret, as install sets it (ADR 0005). The server reads the hash when it starts, so the active revision
/// restarts. Nothing else in the installation changes.
/// </summary>
internal static class ResetPassword
{
    public static async Task<int> RunAsync(
        string prefix, IReadOnlyDictionary<string, string?> environment, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (InstallationNames.PrefixProblem(prefix) is { } prefixProblem)
        {
            await error.WriteLineAsync(prefixProblem);
            return 1;
        }

        var azure = new AzureScripts("Installer", environment);
        AzureAccount account;
        try
        {
            account = await AzureAccount.CurrentAsync(azure, cancellationToken);
        }
        catch (AzureScriptException exception)
        {
            // The check's own messages say what to do.
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
        var names = new InstallationNames(prefix, account.SubscriptionId);
        await output.WriteLineAsync(account.Using);

        string Stopped(StepFailedException exception, string advice) => $"""

            Reset-password stopped at: {exception.Step}. The Azure CLI reported:

            {AzureSteps.Indent(exception.AzureError)}

            {advice}
            """;

        var steps = new AzureSteps(azure, output, cancellationToken);
        string username;
        // Only the hash leaves this machine (ADR 0005).
        var password = Passwords.Generate();
        try
        {
            var found = await steps.RunAsync($"Looking for the installation with the prefix {prefix}", "find-installation", new()
            {
                ["ResourceGroup"] = names.ResourceGroup,
                ["ContainerApp"] = names.ContainerApp,
                ["Prefix"] = prefix,
            });
            // A resource group of that name without the prefix's tag isn't Overseer's.
            if (found.Length == 0 || found[1] != prefix)
            {
                await error.WriteLineAsync(
                    $"There is no Overseer installation with the prefix {prefix} in the subscription {account.SubscriptionName}. " +
                    "Name the installation's prefix with --prefix, or select its subscription with `az account set`.");
                return 1;
            }
            if (found[2] == "-")
            {
                await error.WriteLineAsync(
                    $"The installation with the prefix {prefix} has no Container App yet: an install stopped before creating it. " +
                    $"Run `overseer install{InstallationNames.PrefixOption(prefix)}` to complete it; it prints a new password.");
                return 1;
            }

            username = (await steps.RunAsync("Setting the new password's hash as the Container App's secret", "set-password-hash", new()
            {
                ["ResourceGroup"] = names.ResourceGroup,
                ["ContainerApp"] = names.ContainerApp,
                ["PasswordHash"] = Passwords.Hash(password),
            }))[0];
        }
        catch (StepFailedException exception)
        {
            await error.WriteLineAsync(Stopped(exception,
                $"Fix the cause and run `overseer reset-password{InstallationNames.PrefixOption(prefix)}` again. It needs Contributor on {names.ResourceGroup}."));
            return 1;
        }

        var credentials = $"""

              Username: {username}
              Password: {password}

            This is the only time the password is shown.
            """;
        try
        {
            await steps.RunAsync("Restarting the Container App, so that it uses the new password", "restart-container-app", new()
            {
                ["ResourceGroup"] = names.ResourceGroup,
                ["ContainerApp"] = names.ContainerApp,
            });
        }
        catch (StepFailedException exception)
        {
            // The hash is set, so the next restart makes this the only password that works. Another reset would replace it.
            await output.WriteLineAsync(
                $"{Environment.NewLine}The new password is set, but takes effect only once the Container App restarts.{credentials}");
            await error.WriteLineAsync(Stopped(exception,
                "Restart the Container App yourself, and the password above works: " +
                $"az containerapp revision restart --resource-group {names.ResourceGroup} --name {names.ContainerApp} " +
                $"--revision $(az containerapp show --resource-group {names.ResourceGroup} --name {names.ContainerApp} " +
                "--query properties.latestReadyRevisionName --output tsv)"));
            return 1;
        }

        await output.WriteLineAsync($"{Environment.NewLine}The password has been reset.{credentials}");
        return 0;
    }
}
