using System.Text.RegularExpressions;
using Npgsql;
using Overseer.Login;

namespace Overseer.Installer;

/// <summary><c>overseer install</c>: a working Overseer in the current Azure subscription, deployed at the CLI's own version.</summary>
internal static partial class Install
{
    /// <summary>Where the release workflow publishes the container image, tagged with the version.</summary>
    private const string ImageRepository = "ghcr.io/pnieuwenhuis/overseer";

    /// <summary>Where a new installation goes without <c>--region</c>; an existing one stays where it is.</summary>
    public const string DefaultRegion = "westeurope";

    public static async Task<int> RunAsync(
        string? region, string prefix, string version,
        IReadOnlyDictionary<string, string?> environment, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (version == OverseerVersion.Development)
        {
            await error.WriteLineAsync(
                $"This is a development build ({version}), and no container image is published for it. " +
                "Install with a released `overseer` from https://github.com/pnieuwenhuis/overseer/releases.");
            return 1;
        }
        if (InstallationNames.PrefixProblem(prefix) is { } prefixProblem)
        {
            await error.WriteLineAsync(prefixProblem);
            return 1;
        }
        if (region is not null && !ValidRegion().IsMatch(region))
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
            var found = await steps.RunAsync($"Looking for an installation with the prefix {prefix}", "find-installation", new()
            {
                ["ResourceGroup"] = names.ResourceGroup,
                ["ContainerApp"] = names.ContainerApp,
                ["Prefix"] = prefix,
            });
            var existing = found.Length > 0;
            // The image of the Container App, when there is one: then install updates it, keeping its password.
            string? deployedImage = null;
            if (!existing)
            {
                region ??= DefaultRegion;
                await output.WriteLineAsync($"Installing Overseer {version} in {region} with the prefix {prefix}.");
            }
            else
            {
                var (existingRegion, tag, image) = (found[0], found[1], found[2]);
                if (tag != prefix)
                {
                    await error.WriteLineAsync(
                        $"The resource group {names.ResourceGroup} exists, but Overseer didn't create it: it lacks the tag overseer-prefix={prefix}. " +
                        "Choose another --prefix.");
                    return 1;
                }
                if (region is not null && !region.Equals(existingRegion, StringComparison.OrdinalIgnoreCase))
                {
                    await error.WriteLineAsync(
                        $"The installation with the prefix {prefix} is in {existingRegion}, not {region}, and an installation can't move. " +
                        $"Leave out --region to upgrade it where it is, or choose another --prefix for a new installation in {region}.");
                    return 1;
                }
                region = existingRegion;
                deployedImage = image == "-" ? null : image;
                var deployedVersion = deployedImage?.StartsWith($"{ImageRepository}:") == true ? deployedImage[(ImageRepository.Length + 1)..] : null;
                if (deployedVersion is not null && ReleaseVersion.Parse(deployedVersion) is { } deployed
                    && ReleaseVersion.Parse(version) is { } installing && deployed.CompareTo(installing) > 0)
                {
                    await error.WriteLineAsync(
                        $"The installation with the prefix {prefix} runs Overseer {deployedVersion}, which is newer than this CLI ({version}). " +
                        $"Install never goes back to an older version, because database migrations only go forward. " +
                        $"Upgrade with `overseer` {deployedVersion} or later from https://github.com/pnieuwenhuis/overseer/releases.");
                    return 1;
                }
                await output.WriteLineAsync(deployedImage is null
                    ? $"Completing the installation with the prefix {prefix} in {region}, which an earlier install left unfinished, with Overseer {version}."
                    : $"Upgrading the installation with the prefix {prefix} in {region} from " +
                      $"{(deployedVersion is null ? deployedImage : $"Overseer {deployedVersion}")} to Overseer {version}.");
            }
            // An existing installation may lack what an install that stopped early didn't create, so every step looks first.
            string CreatingOrChecking(string what, string howLong = "") => existing ? $"Checking {what}" : $"Creating {what}{howLong}";

            await steps.RunAsync("Registering the Azure resource providers Overseer uses", "register-providers", []);

            await steps.RunAsync(CreatingOrChecking($"the resource groups {names.ResourceGroup} and {names.SandboxResourceGroup}"), "create-resource-groups", new()
            {
                ["ResourceGroup"] = names.ResourceGroup,
                ["SandboxResourceGroup"] = names.SandboxResourceGroup,
                ["Location"] = region,
                ["Prefix"] = prefix,
            });

            var identity = await steps.RunAsync(CreatingOrChecking("the Managed Identity, with Contributor on the Sandbox resource group only"), "create-identity", new()
            {
                ["SubscriptionId"] = names.SubscriptionId,
                ["ResourceGroup"] = names.ResourceGroup,
                ["SandboxResourceGroup"] = names.SandboxResourceGroup,
                ["Identity"] = names.Identity,
                ["Location"] = region,
            });
            var (identityId, identityPrincipalId, identityClientId) = (identity[0], identity[1], identity[2]);

            var vaultUri = (await steps.RunAsync(CreatingOrChecking("the Key Vault"), "create-key-vault", new()
            {
                ["ResourceGroup"] = names.ResourceGroup,
                ["KeyVault"] = names.KeyVault,
                ["Location"] = region,
                ["IdentityPrincipalId"] = identityPrincipalId,
            }))[0];

            await steps.RunAsync(CreatingOrChecking("the virtual network"), "create-network", new()
            {
                ["ResourceGroup"] = names.ResourceGroup,
                ["VirtualNetwork"] = names.VirtualNetwork,
                ["Location"] = region,
            });

            var databaseHost = (await steps.RunAsync(CreatingOrChecking("the PostgreSQL server", " (this takes 5 to 15 minutes)"), "create-database", new()
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

            await steps.RunAsync(CreatingOrChecking("the Container Apps environment", " (this takes a few minutes)"), "create-container-app-environment", new()
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
            string portalHost;
            string credentials;
            if (deployedImage is null)
            {
                // Only the hash leaves this machine, as a Container App secret (ADR 0005).
                var password = Passwords.Generate();
                try
                {
                    portalHost = (await steps.RunAsync($"Creating the Container App running {ImageRepository}:{version}", "create-container-app", new()
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
                }
                catch (StepFailedException)
                {
                    // Azure may have created the Container App all the same. A re-run then keeps its password, so it's now or never.
                    await output.WriteLineAsync($"""

                        If Azure created the Container App before it failed, this is its password. It is shown only now.

                          Username: {username}
                          Password: {password}
                        """);
                    throw;
                }
                credentials = $"""

                      Portal:   https://{portalHost}/
                      Username: {username}
                      Password: {password}

                    This is the only time the password is shown.
                    """;
            }
            else
            {
                // The Container App's secrets stay as they are, and with them the password.
                portalHost = (await steps.RunAsync($"Updating the Container App to {ImageRepository}:{version}", "update-container-app", new()
                {
                    ["ResourceGroup"] = names.ResourceGroup,
                    ["ContainerApp"] = names.ContainerApp,
                    ["Image"] = $"{ImageRepository}:{version}",
                    ["IdentityClientId"] = identityClientId,
                    ["ConnectionString"] = connectionString.ConnectionString,
                    ["KeyVaultUri"] = vaultUri,
                    ["Username"] = username,
                }))[0];
                credentials = $"""

                      Portal:   https://{portalHost}/
                      Username: {username}

                    The existing password is kept. If you lost it, `overseer reset-password{InstallationNames.PrefixOption(prefix)}` sets a new one.
                    """;
            }
            try
            {
                await steps.RunAsync("Waiting for the Portal to start", "wait-for-portal", new()
                {
                    ["ResourceGroup"] = names.ResourceGroup,
                    ["ContainerApp"] = names.ContainerApp,
                });
            }
            catch (StepFailedException)
            {
                // A new Container App's password is set either way, and it is shown only now.
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

                {AzureSteps.Indent(exception.AzureError)}

                {AzureErrorAdvice.For(exception.AzureError, region ?? DefaultRegion)}
                """);
            return 1;
        }
    }

    [GeneratedRegex("^[a-z0-9]+$")]
    private static partial Regex ValidRegion();
}
