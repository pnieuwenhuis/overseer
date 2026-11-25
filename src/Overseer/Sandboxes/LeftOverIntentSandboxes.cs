namespace Overseer.Sandboxes;

/// <summary>
/// Destroys, as serve starts, every Sandbox an Intent owns from before: Refinement and implementation both own Sandboxes for an
/// Intent, and neither takes up the Sandbox of an earlier run. Register it ahead of both, so that it has listed what is left over
/// before either creates a new one.
/// </summary>
public sealed class LeftOverIntentSandboxes(SandboxRegistry sandboxes, ILogger<LeftOverIntentSandboxes> logger) : IHostedService
{
    private readonly SandboxedWork _work = new(sandboxes, logger);

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => _work.DestroyLeftOverAsync(SandboxOwnerKind.Intent, cancellationToken);

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => _work.StopAsync(cancellationToken);
}
