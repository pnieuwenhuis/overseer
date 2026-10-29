using System.Collections.Concurrent;
using Overseer.Sandboxes;

namespace Overseer.Server;

/// <summary>
/// Lets the opt-in smoke test have a deployed Overseer create a Sandbox, run commands in it and destroy it, before
/// anything in the Portal does. Only with <see cref="EnabledKey"/>, which install never sets, and only for the
/// logged-in user. Creating and destroying a Sandbox take minutes, longer than Container Apps lets a request last, so
/// they run in the background while the test polls the Sandbox's state.
/// </summary>
public static class SmokeTestEndpoints
{
    public const string EnabledKey = "SmokeTest:Enabled";

    public static IServiceCollection AddSmokeTest(this IServiceCollection services, IConfiguration configuration) =>
        configuration.GetValue<bool>(EnabledKey) ? services.AddSingleton<SmokeTestSandboxes>() : services;

    public static void MapSmokeTest(this WebApplication app)
    {
        if (app.Services.GetService<SmokeTestSandboxes>() is null)
        {
            return;
        }
        var sandboxes = app.MapGroup("/smoke-test/sandboxes").RequireAuthorization();
        sandboxes.MapPost("/", (SmokeTestSandboxes smokeTest) => Results.Accepted($"/smoke-test/sandboxes/{smokeTest.Create()}"));
        sandboxes.MapGet("/{id:guid}", (Guid id, SmokeTestSandboxes smokeTest) =>
            smokeTest.Find(id) is { } sandbox ? Results.Ok(sandbox) : Results.NotFound());
        sandboxes.MapPost("/{id:guid}/commands", async (Guid id, SmokeTestCommand command, SmokeTestSandboxes smokeTest, CancellationToken cancellationToken) =>
            await smokeTest.RunCommandAsync(id, command.Command, cancellationToken) is { } result ? Results.Ok(result) : Results.Conflict());
        sandboxes.MapDelete("/{id:guid}", (Guid id, SmokeTestSandboxes smokeTest) =>
            smokeTest.Destroy(id) ? Results.Accepted($"/smoke-test/sandboxes/{id}") : Results.Conflict());
    }
}

public sealed record SmokeTestCommand(string Command);

/// <summary>A smoke test's Sandbox: Creating, Ready, Destroying, Destroyed, or Failed with the error.</summary>
public sealed record SmokeTestSandbox(string State, string? ResourceId = null, string? Error = null);

/// <summary>The smoke test's Sandboxes, by ID, which is also their owner's: an Intent that exists nowhere else.</summary>
internal sealed class SmokeTestSandboxes(
    SandboxRegistry registry, ISandboxProvider provider, IHostApplicationLifetime lifetime, ILogger<SmokeTestSandboxes> logger)
{
    private readonly ConcurrentDictionary<Guid, SmokeTestSandbox> _sandboxes = new();
    private readonly ConcurrentDictionary<Guid, Sandbox> _created = new();

    public Guid Create()
    {
        var id = Guid.NewGuid();
        _sandboxes[id] = new SmokeTestSandbox("Creating");
        InBackground(id, async cancellationToken =>
        {
            var sandbox = await registry.CreateAsync(new SandboxOwner(SandboxOwnerKind.Intent, id), [], cancellationToken);
            _created[id] = sandbox;
            return new SmokeTestSandbox("Ready", sandbox.ResourceId);
        });
        return id;
    }

    public SmokeTestSandbox? Find(Guid id) => _sandboxes.GetValueOrDefault(id);

    /// <summary>Runs the command in the Sandbox, or returns null when the Sandbox isn't ready.</summary>
    public async Task<CommandResult?> RunCommandAsync(Guid id, string command, CancellationToken cancellationToken) =>
        Find(id) is { State: "Ready" } && _created.TryGetValue(id, out var sandbox)
            ? await provider.RunCommandAsync(sandbox, command, cancellationToken)
            : null;

    /// <summary>Starts destroying the Sandbox, unless it isn't ready.</summary>
    public bool Destroy(Guid id)
    {
        if (Find(id) is not { State: "Ready" } ready || !_created.TryGetValue(id, out var sandbox)
            || !_sandboxes.TryUpdate(id, ready with { State = "Destroying" }, ready))
        {
            return false;
        }
        InBackground(id, async cancellationToken =>
        {
            await registry.DestroyAsync(sandbox, cancellationToken);
            _created.TryRemove(id, out _);
            return ready with { State = "Destroyed" };
        });
        return true;
    }

    private void InBackground(Guid id, Func<CancellationToken, Task<SmokeTestSandbox>> work) => _ = Task.Run(async () =>
    {
        try
        {
            _sandboxes[id] = await work(lifetime.ApplicationStopping);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The smoke test's Sandbox {Id} failed.", id);
            _sandboxes[id] = _sandboxes[id] with { State = "Failed", Error = exception.Message };
        }
    });
}
