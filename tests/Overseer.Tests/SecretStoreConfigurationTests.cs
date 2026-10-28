using Microsoft.Extensions.DependencyInjection;
using Overseer.Secrets;
using Overseer.Server;

namespace Overseer.Tests;

/// <summary>Which secret store backend <c>serve</c> uses, and where the local secrets file lives.</summary>
public class SecretStoreConfigurationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("overseer-config-").FullName;

    private string LocalConfigFile => Path.Combine(_directory, "config.json");

    [Fact]
    public async Task By_default_secrets_go_to_a_file_next_to_the_local_config_file_that_only_the_user_can_read()
    {
        await using var app = OverseerServer.Build(new Dictionary<string, string?>
        {
            ["OVERSEER_CONFIG_FILE"] = LocalConfigFile,
        });

        await app.Services.GetRequiredService<ISecretStore>().SetAsync("github-token", "github_pat_11ABC", TestContext.Current.CancellationToken);

        var secretsFile = Path.Combine(_directory, "secrets.json");
        Assert.Contains("github_pat_11ABC", await File.ReadAllTextAsync(secretsFile, TestContext.Current.CancellationToken));
        Assert.Equal("github_pat_11ABC", await app.Services.GetRequiredService<ISecretReader>().ReadAsync("github-token", TestContext.Current.CancellationToken));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(secretsFile));
        }
    }

    [Fact]
    public async Task The_secrets_file_can_be_moved()
    {
        var secretsFile = Path.Combine(_directory, "elsewhere", "my-secrets.json");
        await using var app = OverseerServer.Build(new Dictionary<string, string?>
        {
            ["OVERSEER_CONFIG_FILE"] = LocalConfigFile,
            ["OVERSEER_SECRETS__FILE"] = secretsFile,
        });

        await app.Services.GetRequiredService<ISecretStore>().SetAsync("github-token", "github_pat_11ABC", TestContext.Current.CancellationToken);

        Assert.Contains("github_pat_11ABC", await File.ReadAllTextAsync(secretsFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_configured_Key_Vault_holds_the_secrets()
    {
        File.WriteAllText(LocalConfigFile, """{ "Secrets": { "KeyVaultUri": "https://overseer-test.vault.azure.net/" } }""");

        await using var app = OverseerServer.Build(new Dictionary<string, string?>
        {
            ["OVERSEER_CONFIG_FILE"] = LocalConfigFile,
        });

        var vault = Assert.IsType<KeyVaultSecretStore>(app.Services.GetRequiredService<ISecretReader>());
        Assert.Equal(new Uri("https://overseer-test.vault.azure.net/"), vault.VaultUri);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
