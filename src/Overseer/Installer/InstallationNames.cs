using System.Security.Cryptography;
using System.Text;

namespace Overseer.Installer;

/// <summary>
/// The names of everything install creates, derived from the prefix, so the same prefix always finds the same
/// installation. Key Vault and PostgreSQL names must be unique across Azure, so they also carry a suffix derived
/// from the subscription.
/// </summary>
internal sealed record InstallationNames(string Prefix, string SubscriptionId)
{
    public string ResourceGroup => $"{Prefix}-rg";
    public string SandboxResourceGroup => $"{Prefix}-sandboxes-rg";
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
}
