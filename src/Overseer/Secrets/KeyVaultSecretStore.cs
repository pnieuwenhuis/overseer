using Azure;
using Azure.Security.KeyVault.Secrets;

namespace Overseer.Secrets;

/// <summary>
/// Secrets in Azure Key Vault: through the Managed Identity when Overseer runs in Azure, and through the
/// developer's Azure CLI login when it runs locally.
/// </summary>
public sealed class KeyVaultSecretStore(SecretClient client) : ISecretStore, ISecretReader
{
    public Uri VaultUri => client.VaultUri;

    /// <param name="managedIdentityClientId">The client ID of a user-assigned Managed Identity; <c>null</c> for the system-assigned one.</param>
    public static KeyVaultSecretStore Connect(Uri vaultUri, string? managedIdentityClientId) =>
        new(new SecretClient(vaultUri, AzureCredentials.ManagedIdentityOrAzureCli(managedIdentityClientId)));

    public async Task SetAsync(string name, string value, CancellationToken cancellationToken)
    {
        SecretName.Validate(name);
        try
        {
            await client.SetSecretAsync(name, value, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            // A removed secret stays deleted but recoverable, and keeps its name taken, so bring it back and replace its value.
            var recovery = await client.StartRecoverDeletedSecretAsync(name, cancellationToken);
            await recovery.WaitForCompletionAsync(cancellationToken);
            await client.SetSecretAsync(name, value, cancellationToken);
        }
    }

    public async Task RemoveAsync(string name, CancellationToken cancellationToken)
    {
        SecretName.Validate(name);
        DeleteSecretOperation deletion;
        try
        {
            deletion = await client.StartDeleteSecretAsync(name, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return;
        }
        // Not purged: purging takes a while to finish and is refused when the vault has purge protection.
        await deletion.WaitForCompletionAsync(cancellationToken);
    }

    public async Task<bool> IsSetAsync(string name, CancellationToken cancellationToken) =>
        await ReadAsync(name, cancellationToken) is not null;

    public async Task<string?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        SecretName.Validate(name);
        try
        {
            return (await client.GetSecretAsync(name, cancellationToken: cancellationToken)).Value.Value;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }
}
