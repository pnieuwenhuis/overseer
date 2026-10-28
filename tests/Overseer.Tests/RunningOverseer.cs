using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Overseer.Login;
using Overseer.Persistence;
using Overseer.Server;

namespace Overseer.Tests;

/// <summary>An <c>overseer serve</c> running in-process against a real database, on a free local port.</summary>
public sealed class RunningOverseer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly PortalBrowser _browser;

    private RunningOverseer(WebApplication app)
    {
        _app = app;
        var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        _browser = new PortalBrowser(new Uri(address));
    }

    /// <summary>The user Overseer is started with, unless the test configures its own.</summary>
    public const string Username = "overseer";
    public const string Password = "correct horse battery staple";
    public static readonly string PasswordHash = Passwords.Hash(Password);

    /// <summary>A browser-like client: it follows redirects and keeps cookies, so it stays logged in.</summary>
    public HttpClient Http => _browser.Http;

    /// <summary>The client's cookies; copy them to another instance to keep a browser session across a restart.</summary>
    public CookieContainer Cookies => _browser.Cookies;

    /// <summary>The server's services, for what the Portal can't reach yet, such as the secret store.</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>A fresh connection to Overseer's database; dispose it when done.</summary>
    public OverseerDbContext OpenDatabase() =>
        _app.Services.GetRequiredService<IDbContextFactory<OverseerDbContext>>().CreateDbContext();

    /// <summary>The contents of every table in Overseer's database, one XML document per table.</summary>
    public async Task<IReadOnlyList<string>> DumpDatabaseAsync()
    {
        await using var database = OpenDatabase();
        return await database.Database
            .SqlQueryRaw<string>("""
                SELECT query_to_xml(format('SELECT * FROM %I.%I', table_schema, table_name), true, false, '')::text AS "Value"
                FROM information_schema.tables
                WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema')
                """)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <param name="environment">Overrides the test defaults; a <c>null</c> value removes that variable.</param>
    /// <param name="logs">Also receives everything the server logs.</param>
    public static async Task<RunningOverseer> StartAsync(
        string connectionString, IReadOnlyDictionary<string, string?>? environment = null, ILoggerProvider? logs = null)
    {
        var defaults = new Dictionary<string, string?>
        {
            ["OVERSEER_CONFIG_FILE"] = Path.Combine(Path.GetTempPath(), $"overseer-{Guid.NewGuid():N}", "config.json"),
            ["OVERSEER_URLS"] = "http://127.0.0.1:0",
            ["OVERSEER_CONNECTIONSTRINGS__OVERSEER"] = connectionString,
            ["OVERSEER_USER__PASSWORDHASH"] = PasswordHash,
        };
        foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
        {
            defaults[key] = value;
        }

        var app = await OverseerServer.StartAsync(
            defaults.Where(variable => variable.Value is not null).ToDictionary(),
            TestContext.Current.CancellationToken,
            logs);

        return new RunningOverseer(app);
    }

    /// <inheritdoc cref="PortalBrowser.LogInAsync"/>
    public Task<HttpResponseMessage> LogInAsync(string username = Username, string password = Password, string loginPage = "/login") =>
        _browser.LogInAsync(username, password, loginPage);

    /// <inheritdoc cref="PortalBrowser.IsLoggedInAsync"/>
    public Task<bool> IsLoggedInAsync() => _browser.IsLoggedInAsync();

    public async ValueTask DisposeAsync()
    {
        _browser.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
