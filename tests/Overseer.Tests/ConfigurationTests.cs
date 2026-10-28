using Overseer.Server;

namespace Overseer.Tests;

public class ConfigurationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("overseer-config-").FullName;

    private string LocalConfigFile => Path.Combine(_directory, "config.json");

    [Fact]
    public async Task Defaults_apply_when_nothing_is_configured()
    {
        await using var app = OverseerServer.Build(new Dictionary<string, string?>
        {
            ["OVERSEER_CONFIG_FILE"] = LocalConfigFile,
        });

        Assert.Equal("http://localhost:5080", app.Configuration["Urls"]);
    }

    [Fact]
    public async Task The_local_config_file_overrides_defaults()
    {
        File.WriteAllText(LocalConfigFile, """{ "Urls": "http://localhost:6000" }""");

        await using var app = OverseerServer.Build(new Dictionary<string, string?>
        {
            ["OVERSEER_CONFIG_FILE"] = LocalConfigFile,
        });

        Assert.Equal("http://localhost:6000", app.Configuration["Urls"]);
    }

    [Fact]
    public async Task Environment_variables_override_the_local_config_file()
    {
        File.WriteAllText(LocalConfigFile, """{ "Urls": "http://localhost:6000" }""");

        await using var app = OverseerServer.Build(new Dictionary<string, string?>
        {
            ["OVERSEER_CONFIG_FILE"] = LocalConfigFile,
            ["OVERSEER_URLS"] = "http://localhost:7000",
        });

        Assert.Equal("http://localhost:7000", app.Configuration["Urls"]);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
