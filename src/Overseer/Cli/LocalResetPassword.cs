using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Overseer.Login;

namespace Overseer.Cli;

/// <summary>
/// <c>overseer reset-password</c> for a locally run Overseer: a new password whose hash goes into the local
/// config file, where the next <c>serve</c> picks it up. The password itself is only printed, never stored.
/// </summary>
internal static class LocalResetPassword
{
    public static async Task<int> RunAsync(IReadOnlyDictionary<string, string?> environment, TextWriter output, TextWriter error)
    {
        var configFile = OverseerConfiguration.LocalConfigFile(environment);
        string username;
        try
        {
            username = UserCredential.ConfiguredName(new ConfigurationBuilder().AddOverseerSettings(environment).Build());
        }
        catch (Exception exception) when (exception is MissingConfigurationException or InvalidDataException)
        {
            await error.WriteLineAsync(exception.Message);
            return 1;
        }

        var password = Passwords.Generate();
        try
        {
            await WritePasswordHashAsync(configFile, Passwords.Hash(password));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Could not write the new password to {configFile}: {exception.Message}");
            return 1;
        }

        await output.WriteLineAsync($"""
            The password has been reset. Its hash is in {configFile}; restart `overseer serve` to use it.

              Username: {username}
              Password: {password}

            This is the only time the password is shown.
            """);

        var overridingVariable = OverseerConfiguration.EnvironmentVariable(UserCredential.PasswordHashKey);
        if (environment.Keys.Any(variable => string.Equals(variable, overridingVariable, StringComparison.OrdinalIgnoreCase)))
        {
            await error.WriteLineAsync(
                $"Warning: the {overridingVariable} environment variable is set and overrides the config file. " +
                "Unset it, or `overseer serve` keeps using the hash it holds.");
        }
        return 0;
    }

    /// <summary>Sets <c>User:PasswordHash</c> in the config file and keeps everything else in it (except comments).</summary>
    private static async Task WritePasswordHashAsync(string configFile, string passwordHash)
    {
        // Configuration keys are case-insensitive, so an existing "user" section is the one to update.
        var nodeOptions = new JsonNodeOptions { PropertyNameCaseInsensitive = true };
        JsonObject settings;
        if (File.Exists(configFile))
        {
            var existing = JsonNode.Parse(
                await File.ReadAllTextAsync(configFile),
                nodeOptions,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            settings = existing as JsonObject ?? throw new JsonException("The file does not hold a JSON object.");
        }
        else
        {
            settings = new JsonObject(nodeOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            // Only the user may read a new config file; it can hold connection strings as well.
            await using var _ = UserOnlyFile.CreateNew(configFile);
        }

        var section = ConfigurationPath.GetParentPath(UserCredential.PasswordHashKey)!;
        if (settings[section] is not JsonObject user)
        {
            settings[section] = user = new JsonObject(nodeOptions);
        }
        user[ConfigurationPath.GetSectionKey(UserCredential.PasswordHashKey)] = passwordHash;

        // Relaxed escaping keeps the base64 hash readable ('+' instead of \u002B); the file is never served as HTML.
        var json = settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        await File.WriteAllTextAsync(configFile, json + Environment.NewLine);
    }
}
