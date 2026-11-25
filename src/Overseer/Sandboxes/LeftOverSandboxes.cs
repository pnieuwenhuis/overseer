namespace Overseer.Sandboxes;

/// <summary>
/// Destroys, as serve starts, every Sandbox recorded and not recorded destroyed: left over from a stop, a crash or a failed
/// destruction, as no work takes up the Sandbox of an earlier run. The Sandboxes are listed before it has started, so register
/// it ahead of everything that creates one; they are destroyed in the background, since in Azure that takes minutes.
/// </summary>
public sealed class LeftOverSandboxes(SandboxRegistry sandboxes, ILogger<LeftOverSandboxes> logger) : IHostedService
{
    // Cancelled when serve stops: what is left then, the next start destroys.
    private readonly CancellationTokenSource _stopping = new();

    private Task _destroying = Task.CompletedTask;

    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        var leftOver = await sandboxes.ListRecordedAsync(cancellationToken);
        _destroying = Task.Run(() => DestroyAsync(leftOver));
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        try
        {
            await _destroying.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Interrupted, or stopping no longer waits.
        }
    }

    private async Task DestroyAsync(IReadOnlyList<Sandbox> leftOver)
    {
        foreach (var sandbox in leftOver)
        {
            try
            {
                await sandboxes.DestroyAsync(sandbox, _stopping.Token);
            }
            catch (Exception exception) when (!_stopping.IsCancellationRequested)
            {
                // It stays recorded, so the next start tries again.
                logger.LogError(exception, "The left-over Sandbox {Sandbox} of the {OwnerKind} {OwnerId} could not be destroyed.",
                    sandbox.ResourceId, sandbox.Owner.Kind, sandbox.Owner.Id);
            }
        }
    }
}
