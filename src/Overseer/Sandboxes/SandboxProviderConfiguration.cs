using Overseer.Secrets;

namespace Overseer.Sandboxes;

/// <summary>Which Sandbox Provider <c>serve</c> uses: Azure when it is configured, otherwise the fake.</summary>
public static class SandboxProviderConfiguration
{
    public const string AzureSection = "SandboxProvider:Azure";
    public const string SubscriptionIdKey = $"{AzureSection}:SubscriptionId";
    public const string ResourceGroupKey = $"{AzureSection}:ResourceGroup";
    public const string VmSizeKey = $"{AzureSection}:VmSize";

    /// <exception cref="MissingConfigurationException">The Azure Sandbox Provider is configured, but lacks a setting it needs.</exception>
    public static IServiceCollection AddSandboxes(
        this IServiceCollection services, IConfiguration configuration, IReadOnlyDictionary<string, string?> environment)
    {
        if (configuration.GetSection(AzureSection).Exists())
        {
            var settings = AzureSettings(configuration);
            services.AddSingleton<ISandboxProvider>(provider => new AzureSandboxProvider(
                settings,
                environment,
                provider.GetRequiredService<ISecretStore>(),
                provider.GetRequiredService<ISecretReader>(),
                new SshNetSandboxSsh(),
                DeveloperAddress.CurrentAsync,
                provider.GetRequiredService<TimeProvider>()));
        }
        else
        {
            services.AddSingleton<FakeSandboxProvider>();
            services.AddSingleton<ISandboxProvider>(provider => provider.GetRequiredService<FakeSandboxProvider>());
        }
        services.AddSingleton<SandboxRegistry>();
        return services;
    }

    private static AzureSandboxSettings AzureSettings(IConfiguration configuration)
    {
        var subscription = Required(configuration, SubscriptionIdKey, "the ID of the subscription holding the Sandbox resource group");
        if (!Guid.TryParse(subscription, out var subscriptionId))
        {
            throw new MissingConfigurationException(
                $"The configured {SubscriptionIdKey} ({OverseerConfiguration.EnvironmentVariable(SubscriptionIdKey)}) is not a subscription ID. " +
                "`az account show --query id` shows the ID of the subscription your Azure CLI uses.");
        }
        return new AzureSandboxSettings(
            subscriptionId.ToString(),
            Required(configuration, ResourceGroupKey, "the resource group Sandboxes are created in"),
            configuration[VmSizeKey] is { Length: > 0 } size ? size : AzureSandboxSettings.DefaultVmSize);
    }

    private static string Required(IConfiguration configuration, string key, string description) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new MissingConfigurationException(
                $"The Azure Sandbox Provider needs {key} ({OverseerConfiguration.EnvironmentVariable(key)}): {description}. " +
                $"Remove the {AzureSection} settings to use the fake Sandbox Provider instead.");
}
