using Overseer.Cli;
using static Overseer.Tests.FakeAzureCli;

namespace Overseer.Tests;

/// <summary><c>overseer uninstall</c> against a fake Azure CLI.</summary>
public class UninstallTests : IDisposable
{
    private const string SubscriptionId = "6f0c1a2b-3c4d-4e5f-8a9b-0c1d2e3f4a5b";
    private const string IdentityPrincipalId = "0a1b2c3d-1111-4222-8333-444455556666";
    private const string KeyVault = "overseer-kv-b11b83c1";
    private const string SandboxRoleAssignment =
        "/subscriptions/6f0c1a2b-3c4d-4e5f-8a9b-0c1d2e3f4a5b/resourceGroups/overseer-sandboxes-rg/providers/Microsoft.Authorization/roleAssignments/5d6e7f80-0000-4000-8000-000000000001";

    private readonly FakeAzureCli _az = new();

    /// <summary>Azure as a logged-in user sees it, before anything is installed.</summary>
    public UninstallTests()
    {
        _az.Respond(["account", "show"], $"{SubscriptionId}\nPay-As-You-Go\nuser@example.com\n");
    }

    [Fact]
    public async Task Uninstall_when_nothing_is_installed_says_there_is_nothing_to_remove_and_succeeds()
    {
        var (exitCode, output) = await Uninstall();

        Assert.Equal(0, exitCode);
        var nothing = Assert.Single(output.Split('\n'), line => line.Contains("nothing to remove"));
        Assert.Contains("Pay-As-You-Go", nothing);
        Assert.Empty(_az.Changes);
    }

    [Fact]
    public async Task Typing_the_prefix_removes_the_role_assignments_then_the_Sandbox_and_Overseer_resource_groups_then_purges_the_Key_Vault()
    {
        Installed();

        var (exitCode, output) = await Uninstall(input: "overseer\n");

        Assert.True(exitCode == 0, output);
        Assert.Equal(
            ["role assignment delete", "group delete", "monitor log-analytics workspace delete", "group delete", "keyvault purge"],
            _az.Changes.Select(Command));
        Assert.Equal([SandboxRoleAssignment], Values(_az.CallTo("role", "assignment", "delete"), "--ids"));
        Assert.Equal(
            ["overseer-sandboxes-rg", "overseer-rg"],
            _az.CallsTo("group", "delete").Select(call => Value(call, "--name")));
        Assert.Equal(KeyVault, Value(_az.CallTo("keyvault", "purge"), "--name"));
        Assert.Contains("uninstalled", output);
    }

    [Fact]
    public async Task The_Log_Analytics_workspace_is_deleted_for_good_so_that_a_later_install_does_not_recover_its_logs()
    {
        Installed();

        var (exitCode, output) = await Uninstall(["--yes"]);

        Assert.True(exitCode == 0, output);
        var delete = _az.CallTo("monitor", "log-analytics", "workspace", "delete");
        Assert.Equal("overseer-rg", Value(delete, "--resource-group"));
        Assert.Equal("overseer-logs", Value(delete, "--workspace-name"));
        Assert.Equal("true", Value(delete, "--force"));
    }

    [Theory]
    [InlineData("yes\n")]
    [InlineData("y\n")]
    [InlineData("acme\n")]
    // No input at all, such as a script that didn't pass --yes.
    [InlineData("")]
    public async Task Uninstall_lists_what_it_removes_and_removes_nothing_unless_the_prefix_is_typed(string input)
    {
        Installed();

        var (exitCode, output) = await Uninstall(input: input);

        Assert.Equal(1, exitCode);
        var listing = output[output.IndexOf("Uninstall removes", StringComparison.Ordinal)..output.IndexOf("Type the prefix", StringComparison.Ordinal)];
        Assert.Contains("overseer-rg", listing);
        Assert.Contains("overseer-sandboxes-rg", listing);
        Assert.Contains("nothing was removed", output);
        Assert.Empty(_az.Changes);
    }

    [Fact]
    public async Task Yes_removes_without_asking_for_the_prefix()
    {
        Installed();

        var (exitCode, output) = await Uninstall(["--yes"]);

        Assert.True(exitCode == 0, output);
        Assert.DoesNotContain("Type the prefix", output);
        Assert.Contains("overseer-sandboxes-rg", output);
        Assert.Equal(2, _az.CallsTo("group", "delete").Count);
    }

    [Fact]
    public async Task A_resource_group_of_the_same_name_that_Overseer_did_not_create_is_left_alone_and_said_so()
    {
        Installed();
        _az.Respond(["group", "show", "--name", "overseer-sandboxes-rg"], "-\n");

        var (exitCode, output) = await Uninstall(["--yes"]);

        Assert.True(exitCode == 0, output);
        Assert.Equal(["overseer-rg"], _az.CallsTo("group", "delete").Select(call => Value(call, "--name")));
        var leftAlone = Assert.Single(output.Split('\n'), line => line.Contains("overseer-sandboxes-rg"));
        Assert.Contains("overseer-prefix=overseer", leftAlone);
        Assert.Contains("leaves it alone", leftAlone);
    }

    [Fact]
    public async Task Without_its_Overseer_resource_group_the_Managed_Identity_and_Key_Vault_are_not_Overseers_to_remove()
    {
        Installed();
        _az.Respond(["group", "show", "--name", "overseer-rg"], "-\n");

        var (exitCode, output) = await Uninstall(["--yes"]);

        Assert.True(exitCode == 0, output);
        Assert.Equal(["group delete"], _az.Changes.Select(Command));
        Assert.Equal("overseer-sandboxes-rg", Value(_az.CallTo("group", "delete"), "--name"));
        Assert.DoesNotContain(KeyVault, output);
    }

    [Fact]
    public async Task Resource_groups_that_Overseer_did_not_create_leave_nothing_to_remove()
    {
        Installed();
        _az.Respond(["group", "show"], "-\n");

        var (exitCode, output) = await Uninstall();

        Assert.Equal(0, exitCode);
        Assert.Contains("nothing to remove", output);
        Assert.Contains("leaves it alone", output);
        Assert.Empty(_az.Changes);
    }

    [Fact]
    public async Task Re_running_after_an_uninstall_that_stopped_before_the_purge_purges_the_Key_Vault()
    {
        _az.Respond(["keyvault", "list-deleted"], KeyVault + "\n");

        var (exitCode, output) = await Uninstall(["--yes"]);

        Assert.True(exitCode == 0, output);
        Assert.Equal(["keyvault purge"], _az.Changes.Select(Command));
        Assert.Equal(KeyVault, Value(_az.CallTo("keyvault", "purge"), "--name"));
    }

    [Fact]
    public async Task When_a_step_fails_uninstall_stops_with_the_Azure_error_and_says_a_re_run_removes_the_rest()
    {
        const string azureError = "ERROR: (AuthorizationFailed) The client 'user@example.com' does not have authorization to perform action 'Microsoft.Resources/subscriptions/resourceGroups/delete'.";
        Installed();
        _az.Fail(["group", "delete", "--name", "overseer-sandboxes-rg"], azureError);

        var (exitCode, output) = await Uninstall(["--yes", "--prefix", "overseer"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("overseer-sandboxes-rg", output[output.IndexOf("stopped at", StringComparison.Ordinal)..]);
        Assert.Contains(azureError, output);
        Assert.Contains("run `overseer uninstall` again", output);
        // Deleting a role assignment takes more than Contributor.
        Assert.Contains("User Access Administrator", output);
        Assert.Equal(["role assignment delete", "group delete"], _az.Changes.Select(Command));
    }

    [Fact]
    public async Task The_prefix_names_the_installation_to_remove()
    {
        Installed();
        _az.Respond(["group", "show"], "acme\n");

        var (exitCode, output) = await Uninstall(["--prefix", "acme"], input: "acme\n");

        Assert.True(exitCode == 0, output);
        Assert.Equal(
            ["group exists --name acme-rg", "group exists --name acme-sandboxes-rg"],
            _az.CallsTo("group", "exists").Select(call => string.Join(' ', call)).Distinct());
        Assert.Equal("acme-rg", Value(_az.CallTo("identity", "list"), "--resource-group"));
        Assert.Equal(["acme-sandboxes-rg", "acme-rg"], _az.CallsTo("group", "delete").Select(call => Value(call, "--name")));
        Assert.Equal("acme-kv-b11b83c1", Value(_az.CallTo("keyvault", "purge"), "--name"));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("Acme")]
    [InlineData("thirteenchars")]
    public async Task A_prefix_no_installation_can_have_is_refused_before_anything_happens(string prefix)
    {
        var (exitCode, output) = await Uninstall(["--prefix", prefix]);

        Assert.Equal(1, exitCode);
        Assert.Contains("--prefix", output);
        Assert.Empty(_az.Calls);
    }

    [Fact]
    public async Task Uninstall_when_the_Azure_CLI_is_not_logged_in_says_to_log_in_and_removes_nothing()
    {
        _az.Fail(["account", "show"], "ERROR: Please run 'az login' to setup account.");

        var (exitCode, output) = await Uninstall(["--yes"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("az login", output);
        Assert.Equal([["account", "show"]], _az.Calls.Select(call => call.Take(2)));
    }

    /// <summary>
    /// Azure as an install with the default prefix left it. The fake ignores <c>--query</c>, so a listing answers every
    /// name it is filtered for; the Key Vault is soft-deleted once its resource group is.
    /// </summary>
    private void Installed()
    {
        _az.Respond(["group", "exists"], "true\n")
            .Respond(["group", "show"], "overseer\n")
            .Respond(["identity", "list"], IdentityPrincipalId + "\n")
            .Respond(["role", "assignment", "list"], SandboxRoleAssignment + "\n")
            .Respond(["monitor", "log-analytics", "workspace", "list"], "overseer-logs\n");
        _az.RespondInTurn(["keyvault", "list-deleted"], "", KeyVault + "\n");
    }

    private async Task<(int ExitCode, string Output)> Uninstall(string[]? args = null, string input = "")
    {
        var output = new StringWriter();
        var exitCode = await OverseerCli.InvokeAsync(["uninstall", .. args ?? []], _az.Environment(), new StringReader(input), output, output);
        return (exitCode, output.ToString());
    }

    public void Dispose() => _az.Dispose();
}
