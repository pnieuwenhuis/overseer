using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Overseer.Components;
using Overseer.Login;
using Overseer.Persistence;
using Overseer.Sandboxes;
using Overseer.Secrets;

namespace Overseer.Server;

/// <summary>The <c>serve</c> process: the Portal and the orchestration in one host.</summary>
public static class OverseerServer
{
    /// <param name="logs">Also receives everything the server logs, next to the console.</param>
    public static WebApplication Build(IReadOnlyDictionary<string, string?> environment, ILoggerProvider? logs = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(OverseerServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = OverseerConfiguration.EnvironmentName(environment),
        });

        // This replaces the sources CreateBuilder reads from the process, so the environment map is the only environment.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddOverseerSettings(environment);
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        builder.Services.AddDatabase(builder.Configuration);

        builder.Services.AddSingleton(services => UserCredential.FromConfiguration(services.GetRequiredService<IConfiguration>()));

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSandboxes(builder.Configuration, environment);
        builder.Services.AddSecrets(builder.Configuration, environment);

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.Events.OnValidatePrincipal = async context =>
                {
                    if (!context.HttpContext.RequestServices.GetRequiredService<UserCredential>().IsCurrent(context.Principal))
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
            });
        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
        }
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapStaticAssets();
        // Every page requires the logged-in user; only the login page opts out with [AllowAnonymous].
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .RequireAuthorization();

        return app;
    }

    /// <summary>Builds the server, brings the database schema up to date, and starts listening.</summary>
    /// <inheritdoc cref="Build"/>
    public static async Task<WebApplication> StartAsync(
        IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken, ILoggerProvider? logs = null)
    {
        var app = Build(environment, logs);
        try
        {
            // Fail on a missing credential before touching the database: without it nobody can log in.
            app.Services.GetRequiredService<UserCredential>();
            await using (var database = await app.Services.GetRequiredService<IDbContextFactory<OverseerDbContext>>().CreateDbContextAsync(cancellationToken))
            {
                await database.Database.MigrateAsync(cancellationToken);
            }
            await app.StartAsync(cancellationToken);
            switch (app.Services.GetRequiredService<ISandboxProvider>())
            {
                case FakeSandboxProvider:
                    app.Logger.LogInformation(
                        "Using the fake Sandbox Provider: Sandboxes exist only in memory. Configure {AzureSection} to use Azure.",
                        SandboxProviderConfiguration.AzureSection);
                    break;
                case AzureSandboxProvider azure:
                    app.Logger.LogInformation(
                        "Sandboxes are Azure VMs of size {VmSize} in the resource group {ResourceGroup} of the subscription {SubscriptionId}, " +
                        "reached over SSH from this machine's public IP address only.",
                        azure.Settings.VmSize, azure.Settings.ResourceGroup, azure.Settings.SubscriptionId);
                    break;
            }
            switch (app.Services.GetRequiredService<ISecretReader>())
            {
                case FileSecretStore file:
                    app.Logger.LogInformation(
                        "Secrets are kept in the local secrets file {SecretsFile}. Configure {KeyVaultUriKey} to use Key Vault.",
                        file.Path, SecretStoreConfiguration.KeyVaultUriKey);
                    break;
                case KeyVaultSecretStore vault:
                    app.Logger.LogInformation("Secrets are kept in Key Vault {VaultUri}.", vault.VaultUri);
                    break;
            }
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }
}
