using System.Collections.Concurrent;

namespace Overseer.Sandboxes;

/// <summary>
/// Work done in the background in Sandboxes, such as Refinements, implementations or Repository Setups, shaped the same way for
/// each: it runs until serve stops, which interrupts it and waits for it. The module it belongs to keeps where each piece of work
/// stands, and takes up the work a stop interrupted when serve starts: Repository Setups and implementations mark it failed, and
/// Refinements continue it. The Sandboxes it leaves behind are destroyed then (<see cref="LeftOverSandboxes"/>).
/// </summary>
public sealed class SandboxedWork(ILogger logger)
{
    // Cancelled when serve stops: the work running then is interrupted, and the next start takes it up.
    private readonly CancellationTokenSource _stopping = new();

    // What runs in the background, which stopping waits for.
    private readonly ConcurrentDictionary<Task, bool> _running = new();

    /// <summary>
    /// Runs <paramref name="work"/> in the background, then records how it ended with <paramref name="finish"/>, unless serve
    /// stopped meanwhile: an error then, such as an agent session the stop broke off, is the stop's doing, so the work's phase is
    /// left as it is for the next start to take up.
    /// </summary>
    /// <param name="what">The work, for the log, such as "the Refinement of the Intent …".</param>
    /// <param name="work">Does the work, until serve stops; returns why it failed, or <c>null</c>.</param>
    /// <param name="finish">
    /// Records what <paramref name="work"/> returned, or the message of what it threw. Not cancelled: once the work has ended,
    /// stopping waits for this. Should it fail, the next start takes the work up as interrupted.
    /// </param>
    public void Run(string what, Func<CancellationToken, Task<string?>> work, Func<string?, Task> finish) => Track(Task.Run(async () =>
    {
        string? error;
        try
        {
            error = await work(_stopping.Token);
        }
        catch (Exception exception) when (!_stopping.IsCancellationRequested)
        {
            logger.LogError(exception, "Failed: {Work}.", what);
            error = exception.Message;
        }
        catch (Exception)
        {
            error = "";
        }
        if (error is not null && _stopping.IsCancellationRequested)
        {
            logger.LogWarning("Overseer stopped during {Work}.", what);
            return;
        }
        try
        {
            await finish(error);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "How it ended could not be recorded: {Work}.", what);
        }
    }));

    /// <summary>Interrupts the work running now and waits for it to end, until <paramref name="cancellationToken"/> gives up.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        try
        {
            await Task.WhenAll(_running.Keys).WaitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or AggregateException)
        {
            // Stopping no longer waits; the next start takes up what was running and destroys its Sandboxes.
        }
    }

    private void Track(Task task)
    {
        _running[task] = true;
        task.ContinueWith(done => _running.TryRemove(done, out _), TaskScheduler.Default);
    }
}
