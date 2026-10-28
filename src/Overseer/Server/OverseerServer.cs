using Microsoft.EntityFrameworkCore;
using Overseer.Components;
using Overseer.Persistence;

namespace Overseer.Server;

/// <summary>The <c>serve</c> process: the Portal and the orchestration in one host.</summary>
public static class OverseerServer
{
    public static WebApplication Build(IReadOnlyDictionary<string, string?> environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(OverseerServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Exactly three layers, later ones winning: defaults, the local config file, OVERSEER_ environment variables.
        // This replaces the sources CreateBuilder reads from the process, so the environment map is the only environment.
        builder.Configuration.Sources.Clear();
        builder.Configuration
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
            .AddJsonFile(LocalConfigFile(environment), optional: true)
            .AddInMemoryCollection(PrefixedEnvironmentVariables(environment));

        builder.Services.AddDbContextFactory<OverseerDbContext>((services, options) =>
            options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("Overseer")
                ?? throw new MissingConfigurationException(
                    "No database is configured. Set the OVERSEER_CONNECTIONSTRINGS__OVERSEER environment variable, " +
                    "or ConnectionStrings:Overseer in the local config file.")));

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
        }
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return app;
    }

    /// <summary>Builds the server, brings the database schema up to date, and starts listening.</summary>
    public static async Task<WebApplication> StartAsync(IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        var app = Build(environment);
        try
        {
            await using (var database = await app.Services.GetRequiredService<IDbContextFactory<OverseerDbContext>>().CreateDbContextAsync(cancellationToken))
            {
                await database.Database.MigrateAsync(cancellationToken);
            }
            await app.StartAsync(cancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

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

    /// <summary>The same place on macOS and Linux: <c>$XDG_CONFIG_HOME/overseer/config.json</c>, falling back to <c>~/.config</c>.</summary>
    private static string LocalConfigFile(IReadOnlyDictionary<string, string?> environment) =>
        environment.GetValueOrDefault("OVERSEER_CONFIG_FILE")
        ?? Path.Combine(
            environment.GetValueOrDefault("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "overseer",
            "config.json");
}
