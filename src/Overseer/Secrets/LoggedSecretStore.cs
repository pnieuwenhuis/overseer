namespace Overseer.Secrets;

/// <summary>Logs which secrets are set and removed, by name only, whichever backend holds them.</summary>
internal sealed class LoggedSecretStore(ISecretStore backend, ILogger<LoggedSecretStore> logger) : ISecretStore
{
    public async Task SetAsync(string name, string value, CancellationToken cancellationToken)
    {
        await backend.SetAsync(name, value, cancellationToken);
        logger.LogInformation("Secret {SecretName} set", name);
    }

    public async Task RemoveAsync(string name, CancellationToken cancellationToken)
    {
        await backend.RemoveAsync(name, cancellationToken);
        logger.LogInformation("Secret {SecretName} removed", name);
    }

    public Task<bool> IsSetAsync(string name, CancellationToken cancellationToken) => backend.IsSetAsync(name, cancellationToken);
}
