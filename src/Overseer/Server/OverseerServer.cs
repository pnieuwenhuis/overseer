using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Overseer.Components;
using Overseer.Login;
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
            EnvironmentName = OverseerConfiguration.EnvironmentName(environment),
        });

        // This replaces the sources CreateBuilder reads from the process, so the environment map is the only environment.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddOverseerSettings(environment);

        builder.Services.AddDbContextFactory<OverseerDbContext>((services, options) =>
            options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("Overseer")
                ?? throw new MissingConfigurationException(
                    "No database is configured. Set the OVERSEER_CONNECTIONSTRINGS__OVERSEER environment variable, " +
                    "or ConnectionStrings:Overseer in the local config file.")));

        builder.Services.AddSingleton(services => UserCredential.FromConfiguration(services.GetRequiredService<IConfiguration>()));

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
    public static async Task<WebApplication> StartAsync(IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        var app = Build(environment);
        try
        {
            // Fail on a missing credential before touching the database: without it nobody can log in.
            app.Services.GetRequiredService<UserCredential>();
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
}
