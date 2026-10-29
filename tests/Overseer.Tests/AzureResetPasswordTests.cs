using Overseer.Cli;
using Overseer.Login;
using static Overseer.Tests.FakeAzureCli;

namespace Overseer.Tests;

/// <summary><c>overseer reset-password</c> for an installation in Azure, against a fake Azure CLI.</summary>
public class AzureResetPasswordTests : IDisposable
{
    private const string SubscriptionId = "6f0c1a2b-3c4d-4e5f-8a9b-0c1d2e3f4a5b";
    private const string ActiveRevision = "overseer--0000002";

    private readonly PostgresServer _postgres;
    private readonly FakeAzureCli _az = new();

    /// <summary>Azure with the installation an earlier install with the default prefix left: its Container App runs one replica.</summary>
    public AzureResetPasswordTests(PostgresServer postgres)
    {
        _postgres = postgres;
        _az.Respond(["account", "show"], $"{SubscriptionId}\nPay-As-You-Go\nuser@example.com\n")
            .Respond(["group", "list"], "westeurope\noverseer\n")
            .Respond(["containerapp", "list"], "ghcr.io/pnieuwenhuis/overseer:1.2.3\n")
            .Respond(["containerapp", "show"], "overseer\n")
            .Respond(["containerapp", "revision", "list"], ActiveRevision + "\n");
        TheNextRestartReplacesTheReplica();
    }

    [Fact]
    public async Task After_a_reset_the_new_password_logs_in_and_the_old_one_is_rejected()
    {
        var (_, first) = await ResetPassword();
        TheNextRestartReplacesTheReplica();
        var (exitCode, output) = await ResetPassword();

        Assert.Equal(0, exitCode);
        var old = ResetPasswordOutput.Parse(first);
        var reset = ResetPasswordOutput.Parse(output);
        Assert.Equal("overseer", reset.Username);
        Assert.NotEqual(old.Password, reset.Password);
        Assert.DoesNotContain(_az.Calls.SelectMany(call => call), argument => argument.Contains(reset.Password));

        // The Container App's secret is what the restarted server reads as its password hash.
        await using var overseer = await RunningOverseer.StartAsync(
            await _postgres.CreateEmptyDatabaseAsync(),
            new Dictionary<string, string?> { ["OVERSEER_USER__PASSWORDHASH"] = PasswordHashSecret() });
        await overseer.LogInAsync(old.Username, old.Password);
        Assert.False(await overseer.IsLoggedInAsync());
        await overseer.LogInAsync(reset.Username, reset.Password);
        Assert.True(await overseer.IsLoggedInAsync());
    }

    [Fact]
    public async Task Reset_password_sets_only_the_password_hash_secret_and_restarts_the_active_revision()
    {
        var (exitCode, _) = await ResetPassword();

        Assert.Equal(0, exitCode);
        Assert.Equal(["containerapp secret set", "containerapp revision restart"], _az.Changes.Select(Command));
        var secretSet = _az.CallTo("containerapp", "secret", "set");
        Assert.Equal("overseer-rg", Value(secretSet, "--resource-group"));
        Assert.Equal("overseer", Value(secretSet, "--name"));
        Assert.StartsWith("password-hash=", Value(secretSet, "--secrets"));
        var restart = _az.CallTo("containerapp", "revision", "restart");
        Assert.Equal("overseer", Value(restart, "--name"));
        Assert.Equal(ActiveRevision, Value(restart, "--revision"));
    }

    [Fact]
    public async Task Reset_password_returns_once_a_replica_started_by_the_restart_is_ready_and_the_one_it_replaces_is_gone()
    {
        // Replicas by name and readiness: the new one is ready while the old one still serves with the old hash.
        _az.RespondInTurn(
            ["containerapp", "replica", "list"],
            $"{ActiveRevision}-old\ttrue\n",
            $"{ActiveRevision}-old\ttrue\n{ActiveRevision}-new\ttrue\n",
            $"{ActiveRevision}-new\ttrue\n");

        var (exitCode, _) = await ResetPassword();

        Assert.Equal(0, exitCode);
        var calls = _az.Calls.ToList();
        var restart = calls.FindIndex(call => Command(call) == "containerapp revision restart");
        Assert.Equal(2, calls.Skip(restart).Count(call => Command(call) == "containerapp replica list"));
        Assert.All(calls.Skip(restart), call => Assert.Equal(ActiveRevision, Value(call, "--revision")));
        Assert.Equal("containerapp replica list", Command(calls[^1]));
    }

    [Fact]
    public async Task The_prefix_finds_the_installation_as_it_does_for_install()
    {
        _az.Respond(["group", "list"], "swedencentral\nacme\n");

        var (exitCode, _) = await ResetPassword("--prefix", "acme");

        Assert.Equal(0, exitCode);
        var resourceGroups = _az.Calls.Where(call => call.Contains("--resource-group")).ToList();
        Assert.NotEmpty(resourceGroups);
        Assert.All(resourceGroups, call => Assert.Equal("acme-rg", Value(call, "--resource-group")));
        Assert.All(_az.Changes, call => Assert.Equal("acme", Value(call, "--name")));
    }

    public static TheoryData<string, string> NoInstallation => new()
    {
        // No resource group, so nothing was installed with this prefix in this subscription.
        { "", "There is no Overseer installation with the prefix overseer" },
        // A resource group of that name that Overseer didn't create.
        { "westeurope\n-\n", "There is no Overseer installation with the prefix overseer" },
    };

    [Theory]
    [MemberData(nameof(NoInstallation))]
    public async Task Without_an_installation_for_the_prefix_reset_password_says_so_and_changes_nothing(string resourceGroup, string message)
    {
        _az.Respond(["group", "list"], resourceGroup);

        var (exitCode, output) = await ResetPassword();

        Assert.Equal(1, exitCode);
        var error = Assert.Single(output.Split('\n'), line => line.Contains(message));
        Assert.Contains("Pay-As-You-Go", error);
        Assert.Contains("--prefix", error);
        Assert.DoesNotContain("Password:", output);
        Assert.Empty(_az.Changes);
    }

    [Fact]
    public async Task An_installation_that_stopped_before_its_Container_App_is_completed_by_install_not_reset()
    {
        _az.Respond(["group", "list"], "westeurope\nacme\n")
            .Respond(["containerapp", "list"], "-\n");

        var (exitCode, output) = await ResetPassword("--prefix", "acme");

        Assert.Equal(1, exitCode);
        Assert.Contains("overseer install --prefix acme", output);
        Assert.DoesNotContain("Password:", output);
        Assert.Empty(_az.Changes);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("Acme")]
    [InlineData("thirteenchars")]
    public async Task A_prefix_no_installation_can_have_is_refused_before_anything_happens(string prefix)
    {
        var (exitCode, output) = await ResetPassword("--prefix", prefix);

        Assert.Equal(1, exitCode);
        Assert.Contains("--prefix", output);
        Assert.Empty(_az.Calls);
    }

    [Fact]
    public async Task Reset_password_when_the_Azure_CLI_is_not_logged_in_says_to_log_in_and_changes_nothing()
    {
        _az.Fail(["account", "show"], "ERROR: Please run 'az login' to setup account.");

        var (exitCode, output) = await ResetPassword();

        Assert.Equal(1, exitCode);
        Assert.Contains("az login", output);
        Assert.Equal([["account", "show"]], _az.Calls.Select(call => call.Take(2)));
    }

    [Fact]
    public async Task The_username_is_the_one_the_Container_App_logs_in_with()
    {
        _az.Respond(["containerapp", "show"], "paul\n");

        var (exitCode, output) = await ResetPassword();

        Assert.Equal(0, exitCode);
        Assert.Equal("paul", ResetPasswordOutput.Parse(output).Username);
    }

    [Fact]
    public async Task When_setting_the_secret_fails_reset_password_shows_the_Azure_error_and_no_password()
    {
        const string azureError = "ERROR: (AuthorizationFailed) The client 'user@example.com' does not have authorization to perform action 'Microsoft.App/containerApps/write'.";
        _az.Fail(["containerapp", "secret", "set"], azureError);

        var (exitCode, output) = await ResetPassword();

        Assert.Equal(1, exitCode);
        Assert.Contains(azureError, output);
        Assert.Contains("Contributor", output);
        Assert.DoesNotContain("Password:", output);
        Assert.Empty(_az.CallsTo("containerapp", "revision", "restart"));
    }

    [Fact]
    public async Task When_the_restart_fails_reset_password_still_shows_the_password_it_set()
    {
        const string azureError = "ERROR: (ContainerAppOperationError) Failed to restart revision 'overseer--0000002'.";
        _az.Fail(["containerapp", "revision", "restart"], azureError);

        var (exitCode, output) = await ResetPassword();

        Assert.Equal(1, exitCode);
        Assert.Contains(azureError, output);
        // The hash is set, so the next restart, whenever it comes, makes this the only password that works.
        var credentials = ResetPasswordOutput.Parse(output);
        Assert.Equal("overseer", credentials.Username);
        Assert.True(Passwords.Verify(PasswordHashSecret(), credentials.Password));
        // Running reset-password again would replace the password it just showed.
        Assert.Contains("az containerapp revision restart --resource-group overseer-rg --name overseer", output);
        Assert.DoesNotContain("reset-password again", output);
    }

    /// <summary>The active revision's replica, until the next restart starts a new one in its place.</summary>
    private void TheNextRestartReplacesTheReplica() =>
        _az.RespondInTurn(["containerapp", "replica", "list"], $"{ActiveRevision}-{Guid.NewGuid():N}\ttrue\n", $"{ActiveRevision}-{Guid.NewGuid():N}\ttrue\n");

    /// <summary>The value the last reset gave the Container App's password-hash secret.</summary>
    private string PasswordHashSecret() =>
        Assert.Single(Values(_az.CallsTo("containerapp", "secret", "set").Last(), "--secrets"), secret => secret.StartsWith("password-hash="))
            ["password-hash=".Length..];

    private async Task<(int ExitCode, string Output)> ResetPassword(params string[] args)
    {
        var output = new StringWriter();
        var exitCode = await OverseerCli.InvokeAsync(["reset-password", .. args], _az.Environment(), output, output);
        return (exitCode, output.ToString());
    }

    public void Dispose() => _az.Dispose();
}
