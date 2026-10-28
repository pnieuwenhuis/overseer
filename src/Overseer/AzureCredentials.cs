using Azure.Core;
using Azure.Identity;

namespace Overseer;

/// <summary>How Overseer signs in to Azure services such as Key Vault and PostgreSQL.</summary>
public static class AzureCredentials
{
    /// <summary>The Managed Identity when Overseer runs in Azure, and the developer's Azure CLI login when it runs locally.</summary>
    /// <param name="managedIdentityClientId">The client ID of a user-assigned Managed Identity; <c>null</c> for the system-assigned one.</param>
    public static TokenCredential ManagedIdentityOrAzureCli(string? managedIdentityClientId) =>
        new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            // Only the Managed Identity and the Azure CLI login: settings come from configuration, not from
            // AZURE_* environment variables, and the CLI is how Overseer reaches Azure everywhere else.
            ManagedIdentityClientId = managedIdentityClientId,
            ExcludeEnvironmentCredential = true,
            ExcludeWorkloadIdentityCredential = true,
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeAzurePowerShellCredential = true,
            ExcludeAzureDeveloperCliCredential = true,
            ExcludeInteractiveBrowserCredential = true,
        });
}
