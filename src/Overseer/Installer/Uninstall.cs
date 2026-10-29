namespace Overseer.Installer;

/// <summary>
/// <c>overseer uninstall</c>: removes the installation with a prefix from the current Azure subscription, everything
/// install created and nothing else, once the user confirms by typing the prefix.
/// </summary>
internal static class Uninstall
{
    public static async Task<int> RunAsync(
        string prefix, bool skipConfirmation, IReadOnlyDictionary<string, string?> environment,
        TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
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

        var steps = new AzureSteps(azure, output, cancellationToken);
        try
        {
            var found = await steps.RunAsync($"Looking for the installation with the prefix {prefix}", "find-removable-resources", new()
            {
                ["ResourceGroup"] = names.ResourceGroup,
                ["SandboxResourceGroup"] = names.SandboxResourceGroup,
                ["KeyVault"] = names.KeyVault,
                ["Prefix"] = prefix,
            });
            var (overseerGroup, sandboxGroup, deletedVault) = (found[0] == "overseer", found[1] == "overseer", found[2] == "deleted");
            // The Key Vault is in the Overseer resource group, or soft-deleted from it by an uninstall that stopped before purging it.
            var removeVault = overseerGroup || deletedVault;

            foreach (var (group, status) in new[] { (names.ResourceGroup, found[0]), (names.SandboxResourceGroup, found[1]) })
            {
                if (status == "other")
                {
                    await output.WriteLineAsync(
                        $"The resource group {group} exists, but Overseer didn't create it: it lacks the tag overseer-prefix={prefix}. Uninstall leaves it alone.");
                }
            }
            if (!overseerGroup && !sandboxGroup && !removeVault)
            {
                await output.WriteLineAsync(
                    $"There is no Overseer installation with the prefix {prefix} in the subscription {account.SubscriptionName}, so there is nothing to remove.");
                return 0;
            }

            await output.WriteLineAsync($"{Environment.NewLine}Uninstall removes the installation with the prefix {prefix}, for good:{Environment.NewLine}");
            if (overseerGroup)
            {
                await output.WriteLineAsync(
                    $"  - the resource group {names.ResourceGroup}, with everything in it: the Portal and its logs, the database with every Intent, " +
                    "the Key Vault with every secret, and the Managed Identity, whose role assignments go too");
            }
            if (sandboxGroup)
            {
                await output.WriteLineAsync($"  - the resource group {names.SandboxResourceGroup}, with every Sandbox in it");
            }
            if (removeVault)
            {
                await output.WriteLineAsync($"  - the Key Vault {names.KeyVault}, purged once deleted, so that a later install can use its name again");
            }
            await output.WriteLineAsync();

            if (!skipConfirmation)
            {
                await output.WriteAsync($"Type the prefix ({prefix}) to remove it: ");
                var typed = await input.ReadLineAsync(cancellationToken);
                if (typed?.Trim() != prefix)
                {
                    await output.WriteLineAsync();
                    await error.WriteLineAsync(typed is null
                        ? "Nothing was typed, so nothing was removed. A script confirms with --yes."
                        : $"That isn't {prefix}, so nothing was removed.");
                    return 1;
                }
            }

            if (overseerGroup)
            {
                await steps.RunAsync("Removing the Managed Identity's role assignments", "delete-role-assignments", new()
                {
                    ["ResourceGroup"] = names.ResourceGroup,
                    ["Identity"] = names.Identity,
                });
            }
            if (sandboxGroup)
            {
                await steps.RunAsync($"Deleting the resource group {names.SandboxResourceGroup} and every Sandbox in it", "delete-resource-group", new()
                {
                    ["ResourceGroup"] = names.SandboxResourceGroup,
                    ["Prefix"] = prefix,
                });
            }
            if (overseerGroup)
            {
                await steps.RunAsync("Deleting the Log Analytics workspace for good, with the Portal's logs", "delete-log-analytics-workspace", new()
                {
                    ["ResourceGroup"] = names.ResourceGroup,
                    ["LogAnalyticsWorkspace"] = names.LogAnalyticsWorkspace,
                });
                await steps.RunAsync($"Deleting the resource group {names.ResourceGroup} (this takes 10 to 20 minutes)", "delete-resource-group", new()
                {
                    ["ResourceGroup"] = names.ResourceGroup,
                    ["Prefix"] = prefix,
                });
            }
            if (removeVault)
            {
                await steps.RunAsync($"Purging the deleted Key Vault {names.KeyVault}", "purge-key-vault", new()
                {
                    ["KeyVault"] = names.KeyVault,
                });
            }

            await output.WriteLineAsync($"{Environment.NewLine}Overseer with the prefix {prefix} is uninstalled.");
            return 0;
        }
        catch (StepFailedException exception)
        {
            await error.WriteLineAsync($"""

                Uninstall stopped at: {exception.Step}. The Azure CLI reported:

                {AzureSteps.Indent(exception.AzureError)}

                {AzureErrorAdvice.ForUninstall(exception.AzureError)} Then run `overseer uninstall{InstallationNames.PrefixOption(prefix)}` again: it removes what is left.
                """);
            return 1;
        }
    }
}
