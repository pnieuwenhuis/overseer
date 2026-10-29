using Microsoft.Extensions.DependencyInjection;
using Overseer.Sandboxes;
using Overseer.Server;

namespace Overseer.Tests;

/// <summary>Which Sandbox Provider <c>serve</c> uses.</summary>
public class SandboxProviderConfigurationTests : IDisposable
{
    private const string Subscription = "0b0c0d0e-1f2a-4b3c-8d4e-5f6a7b8c9d0e";

    private readonly string _directory = Directory.CreateTempSubdirectory("overseer-config-").FullName;

    private string LocalConfigFile => Path.Combine(_directory, "config.json");

    [Fact]
    public async Task Without_Azure_settings_Sandboxes_come_from_the_fake_provider()
    {
        await using var app = Build();

        Assert.IsType<FakeSandboxProvider>(app.Services.GetRequiredService<ISandboxProvider>());
    }

    [Fact]
    public async Task With_a_subscription_and_a_Sandbox_resource_group_Sandboxes_are_Azure_VMs_of_the_default_size()
    {
        File.WriteAllText(LocalConfigFile, $$"""
            { "SandboxProvider": { "Azure": { "SubscriptionId": "{{Subscription}}", "ResourceGroup": "overseer-sandboxes-rg" } } }
            """);

        await using var app = Build();

        var azure = Assert.IsType<AzureSandboxProvider>(app.Services.GetRequiredService<ISandboxProvider>());
        Assert.Equal(new AzureSandboxSettings(Subscription, "overseer-sandboxes-rg", "Standard_D4s_v5"), azure.Settings);
    }

    [Fact]
    public async Task The_VM_size_can_be_configured()
    {
        await using var app = Build(new()
        {
            ["OVERSEER_SANDBOXPROVIDER__AZURE__SUBSCRIPTIONID"] = Subscription,
            ["OVERSEER_SANDBOXPROVIDER__AZURE__RESOURCEGROUP"] = "overseer-sandboxes-rg",
            ["OVERSEER_SANDBOXPROVIDER__AZURE__VMSIZE"] = "Standard_D8s_v5",
        });

        var azure = Assert.IsType<AzureSandboxProvider>(app.Services.GetRequiredService<ISandboxProvider>());
        Assert.Equal("Standard_D8s_v5", azure.Settings.VmSize);
    }

    [Theory]
    [InlineData("OVERSEER_SANDBOXPROVIDER__AZURE__SUBSCRIPTIONID", "OVERSEER_SANDBOXPROVIDER__AZURE__RESOURCEGROUP")]
    [InlineData("OVERSEER_SANDBOXPROVIDER__AZURE__RESOURCEGROUP", "OVERSEER_SANDBOXPROVIDER__AZURE__SUBSCRIPTIONID")]
    public void Azure_settings_without_a_subscription_or_resource_group_stop_serve_saying_which_is_missing(string given, string missing)
    {
        var refused = Assert.Throws<MissingConfigurationException>(() => Build(new()
        {
            [given] = given.EndsWith("SUBSCRIPTIONID") ? Subscription : "overseer-sandboxes-rg",
        }));

        Assert.Contains(missing, refused.Message);
    }

    [Fact]
    public void A_subscription_name_instead_of_its_ID_stops_serve()
    {
        var refused = Assert.Throws<MissingConfigurationException>(() => Build(new()
        {
            ["OVERSEER_SANDBOXPROVIDER__AZURE__SUBSCRIPTIONID"] = "Visual Studio Enterprise",
            ["OVERSEER_SANDBOXPROVIDER__AZURE__RESOURCEGROUP"] = "overseer-sandboxes-rg",
        }));

        Assert.Contains("OVERSEER_SANDBOXPROVIDER__AZURE__SUBSCRIPTIONID", refused.Message);
        Assert.Contains("az account show --query id", refused.Message);
    }

    private Microsoft.AspNetCore.Builder.WebApplication Build(Dictionary<string, string?>? environment = null)
    {
        var all = new Dictionary<string, string?>(environment ?? []) { ["OVERSEER_CONFIG_FILE"] = LocalConfigFile };
        return OverseerServer.Build(all);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
