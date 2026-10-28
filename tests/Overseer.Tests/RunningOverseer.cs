using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Overseer.Persistence;
using Overseer.Server;

namespace Overseer.Tests;

/// <summary>An <c>overseer serve</c> running in-process against a real database, on a free local port.</summary>
public sealed class RunningOverseer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RunningOverseer(WebApplication app)
    {
        _app = app;
        var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        Http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public HttpClient Http { get; }

    /// <summary>A fresh connection to Overseer's database; dispose it when done.</summary>
    public OverseerDbContext OpenDatabase() =>
        _app.Services.GetRequiredService<IDbContextFactory<OverseerDbContext>>().CreateDbContext();

    public static async Task<RunningOverseer> StartAsync(string connectionString)
    {
        var app = await OverseerServer.StartAsync(new Dictionary<string, string?>
        {
            ["OVERSEER_CONFIG_FILE"] = Path.Combine(Path.GetTempPath(), $"overseer-{Guid.NewGuid():N}", "config.json"),
            ["OVERSEER_URLS"] = "http://127.0.0.1:0",
            ["OVERSEER_CONNECTIONSTRINGS__OVERSEER"] = connectionString,
        }, TestContext.Current.CancellationToken);

        return new RunningOverseer(app);
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
