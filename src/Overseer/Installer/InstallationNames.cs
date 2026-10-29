using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Overseer.Installer;

/// <summary>
/// The names of everything install creates, derived from the prefix, so the same prefix always finds the same
/// installation. Key Vault and PostgreSQL names must be unique across Azure, so they also carry a suffix derived
/// from the subscription.
/// </summary>
internal sealed partial record InstallationNames(string Prefix, string SubscriptionId)
{
    public string ResourceGroup => $"{Prefix}-rg";
    public string SandboxResourceGroup => $"{Prefix}-sandboxes-rg";
    /// <summary>In the Sandbox resource group, where the Azure Sandbox Provider puts Sandboxes (<c>Sandboxes/Scripts</c>).</summary>
    public string SandboxVirtualNetwork => "sandboxes";
    public string NatGateway => "sandboxes-nat";
    public string Identity => $"{Prefix}-identity";
    public string KeyVault => $"{Prefix}-kv-{UniqueSuffix}";
    public string VirtualNetwork => $"{Prefix}-vnet";
    public string DatabaseServer => $"{Prefix}-db-{UniqueSuffix}";
    public string Database => "overseer";
    public string LogAnalyticsWorkspace => $"{Prefix}-logs";
    public string ContainerAppsEnvironment => $"{Prefix}-env";
    public string ContainerApp => Prefix;

    private string UniqueSuffix { get; } =
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SubscriptionId.ToLowerInvariant())))[..8];

    /// <summary>The prefix of an installation when <c>--prefix</c> is left out.</summary>
    public const string DefaultPrefix = "overseer";

    /// <summary>The <c>--prefix</c> option that names this prefix's installation in a command line; none for the default.</summary>
    public static string PrefixOption(string prefix) => prefix == DefaultPrefix ? "" : $" --prefix {prefix}";

    /// <summary>Why <paramref name="prefix"/> can't start the names of an installation's resources, or null when it can.</summary>
    public static string? PrefixProblem(string prefix) => ValidPrefix().IsMatch(prefix)
        ? null
        : $"--prefix {prefix} can't start Azure resource names. Use 3 to 12 lowercase letters, digits and single hyphens, " +
          "starting with a letter and not ending with a hyphen, such as `overseer` or `acme-dev`.";

    // The Key Vault name is the tightest: at most 24 characters, of which the prefix gets 12.
    [GeneratedRegex("^(?=.{3,12}$)[a-z][a-z0-9]*(-[a-z0-9]+)*$")]
    private static partial Regex ValidPrefix();
}
