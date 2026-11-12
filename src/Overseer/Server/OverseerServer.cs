using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Overseer.Components;
using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Login;
using Overseer.ModelProviders;
using Overseer.Persistence;
using Overseer.Refinement;
using Overseer.Repositories;
using Overseer.Sandboxes;
using Overseer.Secrets;
using Overseer.Settings;
using Overseer.Specs;

namespace Overseer.Server;

/// <summary>The <c>serve</c> process: the Portal and the orchestration in one host.</summary>
public static class OverseerServer
{
    /// <param name="logs">Also receives everything the server logs, next to the console.</param>
    /// <param name="time">The clock, instead of the system's.</param>
    /// <param name="gitHub">Creates the GitHub Gateway, instead of the one that calls GitHub.</param>
    /// <param name="sandboxes">Creates the Sandbox Provider, instead of the one the configuration says.</param>
    public static WebApplication Build(
        IReadOnlyDictionary<string, string?> environment, ILoggerProvider? logs = null, TimeProvider? time = null,
        Func<IServiceProvider, IGitHubGateway>? gitHub = null, Func<IServiceProvider, ISandboxProvider>? sandboxes = null)
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
        builder.Services.AddSingleton<SettingsStore>();

        builder.Services.AddSingleton(services => UserCredential.FromConfiguration(services.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<AccountLockout>();

        builder.Services.AddSingleton(time ?? TimeProvider.System);
        builder.Services.AddSandboxes(builder.Configuration, environment, sandboxes);
        builder.Services.AddSecrets(builder.Configuration, environment);
        builder.Services.AddSmokeTest(builder.Configuration);
        builder.Services.AddSingleton(gitHub ?? (services => new GitHubGateway(services.GetRequiredService<ISecretReader>())));
        builder.Services.AddSingleton<GitHubTokenStore>();
        builder.Services.AddSingleton<ConfiguredRepositories>();
        builder.Services.AddSingleton<StarterFiles>();
        builder.Services.AddSingleton<RepositorySetups>();
        builder.Services.AddHostedService(services => services.GetRequiredService<RepositorySetups>());
        builder.Services.AddSingleton<ModelProviderTokens>();
        builder.Services.AddSingleton<IIntentFailures, RefinementFailures>();
        builder.Services.AddSingleton<IIntentAttention, RefinementAttention>();
        builder.Services.AddSingleton<IntentLifecycle>();
        builder.Services.AddSingleton<SpecStore>();
        builder.Services.AddSingleton<SpecEdits>();
        builder.Services.AddSingleton<SpecSendBack>();
        builder.Services.AddSingleton<SpecApproval>();
        builder.Services.AddSingleton<Refinements>();
        builder.Services.AddHostedService(services => services.GetRequiredService<Refinements>());

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
        app.MapSmokeTest();
        app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            // Only the Portal's own "Log out" button, so another site can't end the session.
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.BadRequest();
            }
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/login");
        });
        // The Retry button of a failed Repository Setup, on the Settings page.
        app.MapPost("/repository-setups/{id:guid}/retry", async (Guid id, HttpContext context, IAntiforgery antiforgery, RepositorySetups setups) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.BadRequest();
            }
            // Not retried when it isn't failed, as after a second press: the Settings page shows where it is.
            await setups.RetryAsync(id, context.RequestAborted);
            return Results.LocalRedirect("/settings");
        }).RequireAuthorization();
        // The Retry button of an Intent whose Refinement failed, on its detail page.
        app.MapPost("/intents/{id:guid}/retry", async (Guid id, HttpContext context, IAntiforgery antiforgery, Refinements refinements) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.BadRequest();
            }
            // Not retried when it hasn't failed, as after a second press: the detail page shows where it is.
            await refinements.RetryAsync(id, context.RequestAborted);
            return Results.LocalRedirect($"/intents/{id}");
        }).RequireAuthorization();
        // Every page requires the logged-in user; only the login page opts out with [AllowAnonymous].
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .RequireAuthorization();

        return app;
    }

    /// <summary>Builds the server, brings the database schema up to date, and starts listening.</summary>
    /// <inheritdoc cref="Build"/>
    public static async Task<WebApplication> StartAsync(
        IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken, ILoggerProvider? logs = null,
        TimeProvider? time = null, Func<IServiceProvider, IGitHubGateway>? gitHub = null, Func<IServiceProvider, ISandboxProvider>? sandboxes = null)
    {
        var app = Build(environment, logs, time, gitHub, sandboxes);
        try
        {
            // Fail on a missing credential before touching the database: without it nobody can log in.
            app.Services.GetRequiredService<UserCredential>();
            await using (var database = await app.Services.GetRequiredService<IDbContextFactory<OverseerDbContext>>().CreateDbContextAsync(cancellationToken))
            {
                await database.Database.MigrateAsync(cancellationToken);
            }
            await app.Services.GetRequiredService<IntentLifecycle>().RecordStartsBeforeTransitionsAsync(cancellationToken);
            await app.StartAsync(cancellationToken);
            switch (app.Services.GetRequiredService<ISandboxProvider>())
            {
                case FakeSandboxProvider:
                    app.Logger.LogInformation(
                        "Using the fake Sandbox Provider: Sandboxes exist only in memory. Configure {AzureSection} to use Azure.",
                        SandboxProviderConfiguration.AzureSection);
                    break;
                case AzureSandboxProvider { Settings.PrivateNetwork: true } azure:
                    app.Logger.LogInformation(
                        "Sandboxes are Azure VMs in the resource group {ResourceGroup} of the subscription {SubscriptionId}, " +
                        "without a public IP: reached over SSH by private IP, through the Sandbox VNet's peering with Overseer's.",
                        azure.Settings.ResourceGroup, azure.Settings.SubscriptionId);
                    break;
                case AzureSandboxProvider azure:
                    app.Logger.LogInformation(
                        "Sandboxes are Azure VMs in the resource group {ResourceGroup} of the subscription {SubscriptionId}, " +
                        "reached over SSH from this machine's public IP address only.",
                        azure.Settings.ResourceGroup, azure.Settings.SubscriptionId);
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
