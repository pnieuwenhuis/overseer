using System.Text.RegularExpressions;
using Npgsql;
using Overseer.Cli;
using Overseer.Secrets;
using Overseer.Server;

namespace Overseer.Tests;

/// <summary><c>overseer install</c> against a fake Azure CLI, as a released executable would run it.</summary>
public class InstallTests : IDisposable
{
    private const string Version = "1.2.3";
    private const string SubscriptionId = "6f0c1a2b-3c4d-4e5f-8a9b-0c1d2e3f4a5b";
    private const string IdentityId = "/subscriptions/6f0c1a2b-3c4d-4e5f-8a9b-0c1d2e3f4a5b/resourceGroups/overseer-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/overseer-identity";
    private const string IdentityPrincipalId = "0a1b2c3d-1111-4222-8333-444455556666";
    private const string IdentityClientId = "9f8e7d6c-aaaa-4bbb-8ccc-ddddeeeeffff";
    private const string DatabaseHost = "overseer-db-0123abcd.postgres.database.azure.com";
    private const string VaultUri = "https://overseer-kv-0123abcd.vault.azure.net/";
    private const string PortalHost = "overseer.calmwave-1a2b3c4d.westeurope.azurecontainerapps.io";

    private readonly PostgresServer _postgres;
    private readonly FakeAzureCli _az = new();

    /// <summary>Azure as a logged-in user with enough rights sees it: every step succeeds.</summary>
    public InstallTests(PostgresServer postgres)
    {
        _postgres = postgres;
        _az.Respond(["account", "show"], $"{SubscriptionId}\nPay-As-You-Go\nuser@example.com\n")
            .Respond(["identity", "create"], $"{IdentityId}\n{IdentityPrincipalId}\n{IdentityClientId}\n")
            .Respond(["keyvault", "create"], VaultUri + "\n")
            .Respond(["postgres", "flexible-server", "show"], DatabaseHost + "\n")
            .Respond(["containerapp", "create"], PortalHost + "\n")
            .Respond(["containerapp", "replica", "list"], "true\n");
    }

    [Fact]
    public async Task Install_from_a_development_build_fails_because_it_has_no_published_image()
    {
        var output = new StringWriter();

        var exitCode = await OverseerCli.InvokeAsync(["install"], _az.Environment(), output, output);

        Assert.Equal(1, exitCode);
        Assert.Contains("development build", output.ToString());
        Assert.Contains("0.0.0-dev", output.ToString());
        Assert.Empty(_az.Calls);
    }

    [Fact]
    public async Task Install_without_the_Azure_CLI_says_where_to_get_it()
    {
        var (exitCode, output) = await Install([], _az.Environment(installed: false));

        Assert.Equal(1, exitCode);
        Assert.Contains("Azure CLI", output);
        Assert.Contains("https://learn.microsoft.com/cli/azure/install-azure-cli", output);
    }

    [Fact]
    public async Task Install_when_the_Azure_CLI_is_not_logged_in_says_to_log_in_and_creates_nothing()
    {
        _az.Fail(["account", "show"], "ERROR: Please run 'az login' to setup account.");

        var (exitCode, output) = await Install();

        Assert.Equal(1, exitCode);
        Assert.Contains("az login", output);
        Assert.Equal([["account", "show"]], _az.Calls.Select(call => call.Take(2)));
    }

    [Fact]
    public async Task Install_shows_the_subscription_it_installs_into()
    {
        var (_, output) = await Install();

        var subscription = Assert.Single(output.Split('\n'), line => line.Contains(SubscriptionId));
        Assert.Contains("Pay-As-You-Go", subscription);
    }

    [Fact]
    public async Task Install_registers_the_resource_providers_it_needs_before_creating_anything()
    {
        await Install();

        var registered = _az.Calls.TakeWhile(call => !call.Contains("create")).Where(call => call.Take(2).SequenceEqual(["provider", "register"]));
        Assert.Equal(
            ["Microsoft.App", "Microsoft.DBforPostgreSQL", "Microsoft.KeyVault", "Microsoft.ManagedIdentity", "Microsoft.Network", "Microsoft.OperationalInsights"],
            registered.Select(call => Value(call, "--namespace")).Order());
    }

    public static TheoryData<string[], string, string[]> FailingSteps => new()
    {
        {
            ["postgres", "flexible-server", "create"],
            "ERROR: (QuotaExceeded) Operation could not be completed as it results in exceeding approved Total Regional vCores quota.",
            ["quota", "--region"]
        },
        {
            ["role", "assignment", "create"],
            "ERROR: (AuthorizationFailed) The client 'user@example.com' does not have authorization to perform action 'Microsoft.Authorization/roleAssignments/write'.",
            ["Owner", "User Access Administrator"]
        },
        {
            ["containerapp", "env", "create"],
            "ERROR: (LocationNotAvailableForResourceType) The provided location 'westeurope' is not available for resource type 'Microsoft.App/managedEnvironments'.",
            ["--region"]
        },
    };

    [Theory]
    [MemberData(nameof(FailingSteps))]
    public async Task A_failing_step_stops_install_with_the_Azure_error_and_what_to_do_about_it(string[] command, string azureError, string[] advice)
    {
        _az.Fail(command, azureError);

        var (exitCode, output) = await Install();

        Assert.Equal(1, exitCode);
        Assert.Contains(azureError, output);
        Assert.All(advice, words => Assert.Contains(words, output));
        Assert.Empty(Calls("containerapp", "create"));
        Assert.DoesNotContain("Password:", output);
    }

    [Fact]
    public async Task Install_waits_for_the_Portal_to_be_ready_after_creating_the_Container_App()
    {
        await Install();

        var created = _az.Calls.ToList().FindIndex(call => call.Take(2).SequenceEqual(["containerapp", "create"]));
        var readiness = _az.Calls.ToList().FindLastIndex(call => call.Take(3).SequenceEqual(["containerapp", "replica", "list"]));
        Assert.InRange(created, 0, readiness - 1);
        Assert.Equal(readiness, _az.Calls.Count - 1);
    }

    [Fact]
    public async Task Install_prints_the_Portal_URL_and_a_username_and_password_that_log_in()
    {
        var (exitCode, output) = await Install();

        Assert.Equal(0, exitCode);
        Assert.Contains(output.Split('\n'), line => Regex.IsMatch(line, $@"^\s*Portal:\s*https://{Regex.Escape(PortalHost)}/?\s*$"));
        var credentials = ResetPasswordOutput.Parse(output);
        Assert.Equal("overseer", credentials.Username);
        Assert.DoesNotContain(_az.Calls.SelectMany(call => call), argument => argument.Contains(credentials.Password));

        // The Container App's secret is what the deployed server reads as its password hash.
        await using var overseer = await RunningOverseer.StartAsync(
            await _postgres.CreateEmptyDatabaseAsync(),
            new Dictionary<string, string?> { ["OVERSEER_USER__PASSWORDHASH"] = ContainerAppSecret("password-hash") });
        await overseer.LogInAsync(credentials.Username, credentials.Password);
        Assert.True(await overseer.IsLoggedInAsync());
    }

    [Fact]
    public async Task By_default_install_creates_the_Overseer_and_Sandbox_resource_groups_in_westeurope()
    {
        var (exitCode, _) = await Install();

        Assert.Equal(0, exitCode);
        Assert.Equal(["overseer-rg", "overseer-sandboxes-rg"], Calls("group", "create").Select(call => Value(call, "--name")).Order());
        Assert.All(_az.Calls.Where(call => call.Contains("--location")), call => Assert.Equal("westeurope", Value(call, "--location")));
    }

    [Fact]
    public async Task Region_and_prefix_set_the_location_and_the_resource_names()
    {
        var (exitCode, _) = await Install("--region", "swedencentral", "--prefix", "acme");

        Assert.Equal(0, exitCode);
        Assert.Equal(["acme-rg", "acme-sandboxes-rg"], Calls("group", "create").Select(call => Value(call, "--name")).Order());
        Assert.Equal("acme", Value(Call("containerapp", "create"), "--name"));
        var locations = _az.Calls.Where(call => call.Contains("--location")).ToList();
        Assert.NotEmpty(locations);
        Assert.All(locations, call => Assert.Equal("swedencentral", Value(call, "--location")));
        var resourceGroups = _az.Calls.Where(call => call.Contains("--resource-group")).ToList();
        Assert.NotEmpty(resourceGroups);
        Assert.All(resourceGroups, call => Assert.StartsWith("acme-", Value(call, "--resource-group")));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("Acme")]
    [InlineData("1acme")]
    [InlineData("acme-")]
    [InlineData("ac--me")]
    [InlineData("acme_dev")]
    [InlineData("thirteenchars")]
    public async Task A_prefix_Azure_cannot_name_resources_with_is_refused_before_anything_happens(string prefix)
    {
        var (exitCode, output) = await Install("--prefix", prefix);

        Assert.Equal(1, exitCode);
        Assert.Contains("--prefix", output);
        Assert.Empty(_az.Calls);
    }

    [Fact]
    public async Task The_Managed_Identitys_only_role_assignment_is_Contributor_on_the_Sandbox_resource_group()
    {
        var (exitCode, _) = await Install();

        Assert.Equal(0, exitCode);
        var identity = Call("identity", "create");
        Assert.Equal("overseer-identity", Value(identity, "--name"));
        Assert.Equal("overseer-rg", Value(identity, "--resource-group"));
        var assignment = Assert.Single(Calls("role", "assignment", "create"));
        Assert.Equal(IdentityPrincipalId, Value(assignment, "--assignee-object-id"));
        Assert.Equal("ServicePrincipal", Value(assignment, "--assignee-principal-type"));
        Assert.Equal("Contributor", Value(assignment, "--role"));
        Assert.Equal($"/subscriptions/{SubscriptionId}/resourceGroups/overseer-sandboxes-rg", Value(assignment, "--scope"));
    }

    [Fact]
    public async Task The_database_is_a_Burstable_PostgreSQL_on_the_VNet_that_only_the_Managed_Identity_can_log_in_to()
    {
        var (exitCode, _) = await Install();

        Assert.Equal(0, exitCode);
        var network = Call("network", "vnet", "create");
        Assert.Equal("overseer-vnet", Value(network, "--name"));
        Assert.Equal("overseer-rg", Value(network, "--resource-group"));
        var subnet = Assert.Single(Calls("network", "vnet", "subnet", "create"), call => Value(call, "--name") == "postgres");
        Assert.Equal("Microsoft.DBforPostgreSQL/flexibleServers", Value(subnet, "--delegations"));

        var server = Call("postgres", "flexible-server", "create");
        Assert.Equal("overseer-rg", Value(server, "--resource-group"));
        Assert.Equal("Burstable", Value(server, "--tier"));
        Assert.Equal(
            $"/subscriptions/{SubscriptionId}/resourceGroups/overseer-rg/providers/Microsoft.Network/virtualNetworks/overseer-vnet/subnets/postgres",
            Value(server, "--subnet"));
        Assert.Equal("Enabled", Value(server, "--microsoft-entra-auth"));
        Assert.Equal("Disabled", Value(server, "--password-auth"));
        Assert.DoesNotContain(server, argument => argument.StartsWith("--admin-password") || argument.StartsWith("--admin-user"));
        Assert.Equal(IdentityPrincipalId, Value(server, "--admin-object-id"));
        Assert.Equal("ServicePrincipal", Value(server, "--admin-type"));
        Assert.Equal("overseer-identity", Value(server, "--admin-display-name"));
        Assert.Equal("overseer", Value(Call("postgres", "flexible-server", "db", "create"), "--database-name"));
    }

    [Fact]
    public async Task The_Key_Vault_gives_the_Managed_Identity_access_to_its_secrets_only()
    {
        var (exitCode, _) = await Install();

        Assert.Equal(0, exitCode);
        var vault = Call("keyvault", "create");
        Assert.Equal("overseer-rg", Value(vault, "--resource-group"));
        // Access policies instead of Azure RBAC, so Contributor on the Sandbox resource group stays the identity's only role.
        Assert.Equal("false", Value(vault, "--enable-rbac-authorization"));
        Assert.Contains("--no-self-perms", vault);
        var policy = Assert.Single(Calls("keyvault", "set-policy"));
        Assert.Equal(Value(vault, "--name"), Value(policy, "--name"));
        Assert.Equal(IdentityPrincipalId, Value(policy, "--object-id"));
        Assert.Equal(["delete", "get", "list", "recover", "set"], Values(policy, "--secret-permissions").Order());
        Assert.DoesNotContain(policy, argument => argument is "--key-permissions" or "--certificate-permissions" or "--storage-permissions");
    }

    [Fact]
    public async Task Key_Vault_and_PostgreSQL_names_are_unique_per_subscription_and_stay_the_same_for_a_prefix()
    {
        await Install();
        await Install();
        _az.Respond(["account", "show"], "11111111-2222-4333-8444-555566667777\nOther subscription\nuser@example.com\n");
        await Install();

        var vaults = Calls("keyvault", "create").Select(call => Value(call, "--name")).ToList();
        var servers = Calls("postgres", "flexible-server", "create").Select(call => Value(call, "--name")).ToList();
        Assert.All(vaults, name => Assert.Matches("^overseer-kv-[0-9a-f]{8}$", name));
        Assert.All(servers, name => Assert.Matches("^overseer-db-[0-9a-f]{8}$", name));
        Assert.Equal(vaults[0], vaults[1]);
        Assert.NotEqual(vaults[0], vaults[2]);
        Assert.Equal(servers[0], servers[1]);
        Assert.NotEqual(servers[0], servers[2]);
    }

    [Fact]
    public async Task The_Container_App_runs_the_CLIs_version_as_a_single_replica_in_an_environment_on_the_VNet()
    {
        var (exitCode, _) = await Install();

        Assert.Equal(0, exitCode);
        var environment = Call("containerapp", "env", "create");
        Assert.Equal("overseer-rg", Value(environment, "--resource-group"));
        Assert.Equal(
            $"/subscriptions/{SubscriptionId}/resourceGroups/overseer-rg/providers/Microsoft.Network/virtualNetworks/overseer-vnet/subnets/container-apps",
            Value(environment, "--infrastructure-subnet-resource-id"));
        var subnet = Assert.Single(Calls("network", "vnet", "subnet", "create"), call => Value(call, "--name") == "container-apps");
        Assert.Equal("Microsoft.App/environments", Value(subnet, "--delegations"));

        var app = Call("containerapp", "create");
        Assert.Equal(Value(environment, "--name"), Value(app, "--environment"));
        Assert.Equal("ghcr.io/pnieuwenhuis/overseer:1.2.3", Value(app, "--image"));
        Assert.Equal("1", Value(app, "--min-replicas"));
        Assert.Equal("1", Value(app, "--max-replicas"));
        Assert.Equal("external", Value(app, "--ingress"));
        Assert.Equal("8080", Value(app, "--target-port"));
    }

    [Fact]
    public async Task The_application_reaches_PostgreSQL_and_Key_Vault_as_the_Managed_Identity_without_a_database_password()
    {
        var (exitCode, _) = await Install();

        Assert.Equal(0, exitCode);
        var app = Call("containerapp", "create");
        Assert.Equal(IdentityId, Value(app, "--user-assigned"));
        var settings = ContainerAppEnvironment();
        var connectionString = new NpgsqlConnectionStringBuilder(settings["OVERSEER_CONNECTIONSTRINGS__OVERSEER"]);
        Assert.Equal(DatabaseHost, connectionString.Host);
        Assert.Equal("overseer", connectionString.Database);
        Assert.Equal("overseer-identity", connectionString.Username);
        Assert.Null(connectionString.Password);
        Assert.Equal(SslMode.Require, connectionString.SslMode);
        Assert.Equal("true", settings["OVERSEER_DATABASE__ENTRAAUTHENTICATION"]);
        Assert.Equal(IdentityClientId, settings["OVERSEER_DATABASE__MANAGEDIDENTITYCLIENTID"]);
        Assert.Equal(IdentityClientId, settings["OVERSEER_SECRETS__MANAGEDIDENTITYCLIENTID"]);
        Assert.DoesNotContain(_az.Calls.SelectMany(call => call), argument => argument.Contains("password=", StringComparison.OrdinalIgnoreCase));

        await using var server = OverseerServer.Build(settings);
        var secrets = Assert.IsType<KeyVaultSecretStore>(server.Services.GetService(typeof(ISecretReader)));
        Assert.Equal(new Uri(VaultUri), secrets.VaultUri);
    }

    private IReadOnlyList<IReadOnlyList<string>> Calls(params string[] command) =>
        _az.Calls.Where(call => call.Take(command.Length).SequenceEqual(command)).ToList();

    private IReadOnlyList<string> Call(params string[] command) =>
        Assert.Single(_az.Calls, call => call.Take(command.Length).SequenceEqual(command));

    /// <summary>The values that follow <paramref name="option"/> in <paramref name="call"/>, up to the next option.</summary>
    private static IReadOnlyList<string> Values(IReadOnlyList<string> call, string option) =>
        call.SkipWhile(argument => argument != option).Skip(1).TakeWhile(argument => !argument.StartsWith("--")).ToList();

    private static string Value(IReadOnlyList<string> call, string option) => Assert.Single(Values(call, option));

    /// <summary>The Container App's environment variables, with secret references resolved as Container Apps does.</summary>
    private Dictionary<string, string?> ContainerAppEnvironment() =>
        Values(Call("containerapp", "create"), "--env-vars")
            .Select(variable => variable.Split('=', 2))
            .ToDictionary(
                variable => variable[0],
                string? (variable) => variable[1].StartsWith("secretref:") ? ContainerAppSecret(variable[1]["secretref:".Length..]) : variable[1]);

    private string ContainerAppSecret(string name) =>
        Assert.Single(Values(Call("containerapp", "create"), "--secrets"), secret => secret.StartsWith(name + "="))[(name.Length + 1)..];

    private Task<(int ExitCode, string Output)> Install(params string[] args) => Install(args, _az.Environment());

    private static async Task<(int ExitCode, string Output)> Install(string[] args, IReadOnlyDictionary<string, string?> environment)
    {
        var output = new StringWriter();
        var exitCode = await OverseerCli.InvokeAsync(["install", .. args], environment, output, output, Version);
        return (exitCode, output.ToString());
    }

    public void Dispose() => _az.Dispose();
}
