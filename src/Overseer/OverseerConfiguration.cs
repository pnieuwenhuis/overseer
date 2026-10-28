namespace Overseer;

/// <summary>
/// Where Overseer's settings come from, shared by <c>serve</c> and the other commands. Exactly three layers,
/// later ones winning: defaults, the local config file, <c>OVERSEER_</c> environment variables.
/// </summary>
public static class OverseerConfiguration
{
    /// <summary>
    /// Adds the three layers. <paramref name="environment"/> is the only environment they read, so
    /// clear any sources that read the process first.
    /// </summary>
    public static IConfigurationBuilder AddOverseerSettings(
        this IConfigurationBuilder builder, IReadOnlyDictionary<string, string?> environment) =>
        builder
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, $"appsettings.{EnvironmentName(environment)}.json"), optional: true)
            .AddJsonFile(LocalConfigFile(environment), optional: true)
            .AddInMemoryCollection(PrefixedEnvironmentVariables(environment));

    /// <summary>Development, Production and so on: <c>ASPNETCORE_ENVIRONMENT</c>, or Production when it is unset.</summary>
    public static string EnvironmentName(IReadOnlyDictionary<string, string?> environment) =>
        environment.GetValueOrDefault("ASPNETCORE_ENVIRONMENT") is { Length: > 0 } name ? name : Environments.Production;

    /// <summary>The same place on macOS and Linux: <c>$XDG_CONFIG_HOME/overseer/config.json</c>, falling back to <c>~/.config</c>.</summary>
    public static string LocalConfigFile(IReadOnlyDictionary<string, string?> environment) =>
        environment.GetValueOrDefault("OVERSEER_CONFIG_FILE")
        ?? Path.Combine(
            environment.GetValueOrDefault("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "overseer",
            "config.json");

    /// <summary>The environment variable that sets <paramref name="key"/>: <c>User:PasswordHash</c> is <c>OVERSEER_USER__PASSWORDHASH</c>.</summary>
    public static string EnvironmentVariable(string key) =>
        EnvironmentPrefix + key.Replace(ConfigurationPath.KeyDelimiter, "__").ToUpperInvariant();

    /// <summary>
    /// <c>OVERSEER_</c> environment variables as configuration keys, using the usual
    /// <c>__</c> section separator: <c>OVERSEER_CONNECTIONSTRINGS__OVERSEER</c> sets
    /// <c>ConnectionStrings:Overseer</c>.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string?>> PrefixedEnvironmentVariables(IReadOnlyDictionary<string, string?> environment) =>
        environment
            .Where(variable => variable.Key.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(variable => KeyValuePair.Create(
                variable.Key[EnvironmentPrefix.Length..].Replace("__", ConfigurationPath.KeyDelimiter),
                variable.Value));

    private const string EnvironmentPrefix = "OVERSEER_";
}
