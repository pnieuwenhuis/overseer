using System.Text.Json.Nodes;
using Overseer.Cli;
using Overseer.Login;

namespace Overseer.Tests;

public class CliTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("overseer-cli-").FullName;

    private string LocalConfigFile => Path.Combine(_directory, "config.json");

    [Fact]
    public async Task Without_a_command_overseer_lists_its_commands()
    {
        var (_, output) = await Run();

        Assert.Contains("serve", output);
        Assert.Contains("install", output);
        Assert.Contains("uninstall", output);
        Assert.Contains("reset-password", output);
    }

    [Fact]
    public async Task An_unknown_command_prints_usage_and_fails()
    {
        var (exitCode, output) = await Run("frobnicate");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Usage:", output);
        Assert.Contains("reset-password", output);
    }

    [Fact]
    public async Task Version_prints_the_version_stamped_into_the_executable()
    {
        var (exitCode, output) = await Run("--version");

        Assert.Equal(0, exitCode);
        Assert.Equal(OverseerVersion.Current, output.Trim());
    }

    [Fact]
    public async Task A_local_build_reports_the_development_version()
    {
        var (_, output) = await Run("--version");

        Assert.Equal("0.0.0-dev", output.Trim());
    }

    [Fact]
    public async Task Serve_without_a_password_hash_fails_and_points_at_reset_password()
    {
        var (exitCode, output) = await Run("serve");

        Assert.Equal(1, exitCode);
        Assert.Contains("overseer reset-password --local", output);
    }

    [Fact]
    public async Task Serve_with_something_other_than_a_password_hash_fails_and_points_at_reset_password()
    {
        // The likeliest mistake: the password itself instead of its hash.
        var (exitCode, output) = await Run(["serve"], new Dictionary<string, string?> { ["OVERSEER_USER__PASSWORDHASH"] = Passwords.Generate() });

        Assert.Equal(1, exitCode);
        Assert.Contains("not a password hash", output);
        Assert.Contains("overseer reset-password --local", output);
    }

    [Fact]
    public async Task Local_reset_password_prints_a_new_password_and_writes_its_hash_to_the_local_config_file()
    {
        var (exitCode, output) = await Run("reset-password", "--local");

        Assert.Equal(0, exitCode);
        var credentials = ResetPasswordOutput.Parse(output);
        Assert.Equal("overseer", credentials.Username);
        Assert.True(credentials.Password.Length >= 20, $"Password {credentials.Password} is too short.");
        var configFile = File.ReadAllText(LocalConfigFile);
        Assert.DoesNotContain(credentials.Password, configFile);
        Assert.True(Passwords.Verify(ConfiguredPasswordHash(configFile), credentials.Password));
    }

    [Fact]
    public async Task Local_reset_password_generates_a_different_password_each_time()
    {
        var first = ResetPasswordOutput.Parse((await Run("reset-password", "--local")).Output);
        var second = ResetPasswordOutput.Parse((await Run("reset-password", "--local")).Output);

        Assert.NotEqual(first.Password, second.Password);
        Assert.False(Passwords.Verify(ConfiguredPasswordHash(File.ReadAllText(LocalConfigFile)), first.Password));
    }

    [Fact]
    public async Task Local_reset_password_keeps_the_other_settings_in_the_local_config_file()
    {
        File.WriteAllText(LocalConfigFile, """
            {
              // Comments are allowed in the config file.
              "Urls": "http://localhost:6000",
              "user": { "name": "paul", "passwordHash": "old" },
            }
            """);

        var credentials = ResetPasswordOutput.Parse((await Run("reset-password", "--local")).Output);

        Assert.Equal("paul", credentials.Username);
        var configFile = JsonNode.Parse(File.ReadAllText(LocalConfigFile))!;
        Assert.Equal("http://localhost:6000", (string?)configFile["Urls"]);
        Assert.Equal("paul", (string?)configFile["user"]!["name"]);
        Assert.True(Passwords.Verify(ConfiguredPasswordHash(configFile.ToJsonString()), credentials.Password));
    }

    [Fact]
    public async Task Local_reset_password_warns_when_an_environment_variable_overrides_the_new_hash()
    {
        var (exitCode, output) = await Run(["reset-password", "--local"], new Dictionary<string, string?> { ["OVERSEER_USER__PASSWORDHASH"] = "set-elsewhere" });

        Assert.Equal(0, exitCode);
        Assert.Contains("OVERSEER_USER__PASSWORDHASH", output);
    }

    [Fact]
    public async Task Reset_password_refuses_a_prefix_with_local_because_it_names_an_installation_in_Azure()
    {
        var (exitCode, output) = await Run("reset-password", "--local", "--prefix", "acme");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("--local", output);
        Assert.False(File.Exists(LocalConfigFile));
    }

    private static string ConfiguredPasswordHash(string configFile)
    {
        var user = JsonNode.Parse(configFile, new JsonNodeOptions { PropertyNameCaseInsensitive = true })!["User"]!;
        return (string)user["PasswordHash"]!;
    }

    private Task<(int ExitCode, string Output)> Run(params string[] args) => Run(args, new Dictionary<string, string?>());

    private async Task<(int ExitCode, string Output)> Run(string[] args, IReadOnlyDictionary<string, string?> environment)
    {
        var output = new StringWriter();
        var defaults = new Dictionary<string, string?>
        {
            ["OVERSEER_CONFIG_FILE"] = LocalConfigFile,
            ["OVERSEER_URLS"] = "http://127.0.0.1:0",
            // Never reached: serve checks the credential before it touches the database.
            ["OVERSEER_CONNECTIONSTRINGS__OVERSEER"] = "Host=127.0.0.1;Port=1",
        };
        foreach (var (key, value) in environment)
        {
            defaults[key] = value;
        }
        var exitCode = await OverseerCli.InvokeAsync(args, defaults, output, output);
        return (exitCode, output.ToString());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
