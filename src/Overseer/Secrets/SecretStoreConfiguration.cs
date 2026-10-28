namespace Overseer.Secrets;

/// <summary>Where <c>serve</c> keeps secrets: in Key Vault when one is configured, otherwise in the local secrets file.</summary>
public static class SecretStoreConfiguration
{
    public const string KeyVaultUriKey = "Secrets:KeyVaultUri";
    public const string ManagedIdentityClientIdKey = "Secrets:ManagedIdentityClientId";
    public const string FileKey = "Secrets:File";

    /// <exception cref="MissingConfigurationException">The configured Key Vault URI is not a URI.</exception>
    public static IServiceCollection AddSecrets(
        this IServiceCollection services, IConfiguration configuration, IReadOnlyDictionary<string, string?> environment)
    {
        if (configuration[KeyVaultUriKey] is { Length: > 0 } keyVaultUri)
        {
            if (!Uri.TryCreate(keyVaultUri, UriKind.Absolute, out var vault) || vault.Scheme != Uri.UriSchemeHttps)
            {
                throw new MissingConfigurationException(
                    $"The configured {KeyVaultUriKey} ({OverseerConfiguration.EnvironmentVariable(KeyVaultUriKey)}) is not a Key Vault URI " +
                    "such as https://my-vault.vault.azure.net/.");
            }
            return services.AddBackend(KeyVaultSecretStore.Connect(vault, configuration[ManagedIdentityClientIdKey] is { Length: > 0 } id ? id : null));
        }

        var file = configuration[FileKey] is { Length: > 0 } configured ? Path.GetFullPath(configured) : LocalSecretsFile(environment);
        return services.AddBackend(new FileSecretStore(file));
    }

    private static IServiceCollection AddBackend<TBackend>(this IServiceCollection services, TBackend backend)
        where TBackend : class, ISecretStore, ISecretReader =>
        services
            .AddSingleton<ISecretReader>(backend)
            .AddSingleton<ISecretStore>(provider => new LoggedSecretStore(backend, provider.GetRequiredService<ILogger<LoggedSecretStore>>()));

    /// <summary>
    /// <c>secrets.json</c> next to the local config file: <c>~/.config/overseer/secrets.json</c> by default,
    /// outside any repository.
    /// </summary>
    private static string LocalSecretsFile(IReadOnlyDictionary<string, string?> environment) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(OverseerConfiguration.LocalConfigFile(environment)))!, "secrets.json");
}
