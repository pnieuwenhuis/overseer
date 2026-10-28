namespace Overseer.Sandboxes;

/// <summary>Which Sandbox Provider <c>serve</c> uses: Azure when it is configured, otherwise the fake.</summary>
public static class SandboxProviderConfiguration
{
    public const string AzureSection = "SandboxProvider:Azure";

    /// <exception cref="MissingConfigurationException">The Azure Sandbox Provider is configured; it is not built yet.</exception>
    public static IServiceCollection AddSandboxes(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration.GetSection(AzureSection).Exists())
        {
            throw new MissingConfigurationException(
                "The Azure Sandbox Provider is not implemented yet. Remove the " +
                $"{AzureSection} settings ({OverseerConfiguration.EnvironmentVariable(AzureSection)}__*) to use the fake Sandbox Provider.");
        }

        services.AddSingleton<FakeSandboxProvider>();
        services.AddSingleton<ISandboxProvider>(provider => provider.GetRequiredService<FakeSandboxProvider>());
        services.AddSingleton<SandboxRegistry>();
        return services;
    }
}
