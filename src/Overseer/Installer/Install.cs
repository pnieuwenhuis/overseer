using System.Text.RegularExpressions;
using Npgsql;
using Overseer.Login;

namespace Overseer.Installer;

/// <summary><c>overseer install</c>: a working Overseer in the current Azure subscription, deployed at the CLI's own version.</summary>
internal static partial class Install
{
    /// <summary>Where the release workflow publishes the container image, tagged with the version.</summary>
    private const string ImageRepository = "ghcr.io/pnieuwenhuis/overseer";

    public static async Task<int> RunAsync(
        string region, string prefix, string version,
        IReadOnlyDictionary<string, string?> environment, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (version == OverseerVersion.Development)
        {
            await error.WriteLineAsync(
                $"This is a development build ({version}), and no container image is published for it. " +
                "Install with a released `overseer` from https://github.com/pnieuwenhuis/overseer/releases.");
            return 1;
        }
        if (!ValidPrefix().IsMatch(prefix))
        {
            await error.WriteLineAsync(
                $"--prefix {prefix} can't start Azure resource names. Use 3 to 12 lowercase letters, digits and single hyphens, " +
                "starting with a letter and not ending with a hyphen, such as `overseer` or `acme-dev`.");
            return 1;
        }
        if (!ValidRegion().IsMatch(region))
        {
            await error.WriteLineAsync(
                $"--region {region} is not an Azure region name such as `westeurope`. `az account list-locations --output table` lists them.");
            return 1;
        }

        string username;
        try
        {
            username = UserCredential.ConfiguredName(new ConfigurationBuilder().AddOverseerSettings(environment).Build());
        }
        catch (Exception exception) when (exception is MissingConfigurationException or InvalidDataException)
        {
            await error.WriteLineAsync(exception.Message);
            return 1;
        }

        var azure = new AzureScripts(environment);
        string[] account;
        try
        {
            account = Lines(await azure.RunAsync("check-azure-cli", new Dictionary<string, string>(), cancellationToken));
        }
        catch (AzureScriptException exception)
        {
            // The check's own messages say what to do.
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
        var (subscriptionId, subscriptionName, user) = (account[0], account[1], account[2]);
        var names = new InstallationNames(prefix, subscriptionId);
        await output.WriteLineAsync(
            $"Installing Overseer {version} into the subscription {subscriptionName} ({subscriptionId}) as {user}, " +
            $"in {region} with the prefix {prefix}. `az account set` selects another subscription.");

        async Task<string[]> Step(string description, string script, Dictionary<string, string> parameters)
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

        try
        {
            await Step("Registering the Azure resource providers Overseer uses", "register-providers", []);

            await Step($"Creating the resource groups {names.ResourceGroup} and {names.SandboxResourceGroup}", "create-resource-groups", new()
            {
                ["ResourceGroup"] = names.ResourceGroup,
                ["SandboxResourceGroup"] = names.SandboxResourceGroup,
                ["Location"] = region,
                ["Prefix"] = prefix,
            });

            var identity = await Step("Creating the Managed Identity, with Contributor on the Sandbox resource group only", "create-identity", new()
            {
                ["SubscriptionId"] = names.SubscriptionId,
                ["ResourceGroup"] = names.ResourceGroup,
                ["SandboxResourceGroup"] = names.SandboxResourceGroup,
                ["Identity"] = names.Identity,
                ["Location"] = region,
            });
            var (identityId, identityPrincipalId, identityClientId) = (identity[0], identity[1], identity[2]);

            var vaultUri = (await Step("Creating the Key Vault", "create-key-vault", new()
            {
                ["ResourceGroup"] = names.ResourceGroup,
                ["KeyVault"] = names.KeyVault,
                ["Location"] = region,
                ["IdentityPrincipalId"] = identityPrincipalId,
            }))[0];

            await Step("Creating the virtual network", "create-network", new()
            {
                ["ResourceGroup"] = names.ResourceGroup,
                ["VirtualNetwork"] = names.VirtualNetwork,
                ["Location"] = region,
            });

            var databaseHost = (await Step("Creating the PostgreSQL server (this takes 5 to 15 minutes)", "create-database", new()
            {
                ["SubscriptionId"] = names.SubscriptionId,
                ["ResourceGroup"] = names.ResourceGroup,
                ["VirtualNetwork"] = names.VirtualNetwork,
                ["DatabaseServer"] = names.DatabaseServer,
                ["Database"] = names.Database,
                ["Location"] = region,
                ["Identity"] = names.Identity,
                ["IdentityPrincipalId"] = identityPrincipalId,
            }))[0];

            await Step("Creating the Container Apps environment (this takes a few minutes)", "create-container-app-environment", new()
            {
                ["SubscriptionId"] = names.SubscriptionId,
                ["ResourceGroup"] = names.ResourceGroup,
                ["VirtualNetwork"] = names.VirtualNetwork,
                ["ContainerAppsEnvironment"] = names.ContainerAppsEnvironment,
                ["LogAnalyticsWorkspace"] = names.LogAnalyticsWorkspace,
                ["Location"] = region,
            });

            // No password: the server logs in with a Microsoft Entra token for the Managed Identity, whose name is its PostgreSQL login.
            var connectionString = new NpgsqlConnectionStringBuilder
            {
                Host = databaseHost,
                Database = names.Database,
                Username = names.Identity,
                SslMode = SslMode.Require,
            };
            // Only the hash leaves this machine, as a Container App secret (ADR 0005).
            var password = Passwords.Generate();
            var portalHost = (await Step($"Creating the Container App running {ImageRepository}:{version}", "create-container-app", new()
            {
                ["ResourceGroup"] = names.ResourceGroup,
                ["ContainerAppsEnvironment"] = names.ContainerAppsEnvironment,
                ["ContainerApp"] = names.ContainerApp,
                ["Image"] = $"{ImageRepository}:{version}",
                ["IdentityId"] = identityId,
                ["IdentityClientId"] = identityClientId,
                ["ConnectionString"] = connectionString.ConnectionString,
                ["KeyVaultUri"] = vaultUri,
                ["Username"] = username,
                ["PasswordHash"] = Passwords.Hash(password),
            }))[0];

            var credentials = $"""

                  Portal:   https://{portalHost}/
                  Username: {username}
                  Password: {password}

                This is the only time the password is shown.
                """;
            try
            {
                await Step("Waiting for the Portal to start", "wait-for-portal", new()
                {
                    ["ResourceGroup"] = names.ResourceGroup,
                    ["ContainerApp"] = names.ContainerApp,
                });
            }
            catch (StepFailedException)
            {
                // The password is set either way, and it is shown only now.
                await output.WriteLineAsync($"{Environment.NewLine}Overseer {version} is deployed, but its Portal did not start.{credentials}");
                throw;
            }

            await output.WriteLineAsync($"{Environment.NewLine}Overseer {version} is installed.{credentials}");
            return 0;
        }
        catch (StepFailedException exception)
        {
            await error.WriteLineAsync($"""

                Install stopped at: {exception.Step}. The Azure CLI reported:

                {Indent(exception.AzureError)}

                {AzureErrorAdvice.For(exception.AzureError, region)}
                """);
            return 1;
        }
    }

    // The Key Vault name is the tightest: at most 24 characters, of which the prefix gets 12 (see InstallationNames).
    [GeneratedRegex("^(?=.{3,12}$)[a-z][a-z0-9]*(-[a-z0-9]+)*$")]
    private static partial Regex ValidPrefix();

    [GeneratedRegex("^[a-z0-9]+$")]
    private static partial Regex ValidRegion();

    private static string[] Lines(string output) => output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string Indent(string text) => string.Join(Environment.NewLine, text.Split('\n').Select(line => "    " + line.TrimEnd()));

    private sealed class StepFailedException(string step, string azureError) : Exception($"{step}: {azureError}")
    {
        public string Step { get; } = step;
        public string AzureError { get; } = azureError;
    }
}
