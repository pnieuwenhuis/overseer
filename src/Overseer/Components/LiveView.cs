using Microsoft.AspNetCore.Components;

namespace Overseer.Components;

/// <summary>
/// A view that stays current: it reads what it shows when it starts and when its parameters change, and again whenever something it
/// shows changes. Rendered with <c>InteractiveServer</c> it updates over Blazor's SignalR connection.
/// </summary>
/// <typeparam name="TShown">
/// What the view keeps from the server's rendering of the page for the live connection: all it reads, or a part of it when the whole
/// can be larger than the connection takes back.
/// </typeparam>
public abstract class LiveView<TShown> : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _disposed = new();
    private bool _reading;
    private bool _stale;

    /// <summary>
    /// What was last read; <c>null</c> until the first read is done. Persisted from the server's rendering of the page into the live
    /// connection, whose first render would otherwise blank what shows it until its own read is done.
    /// </summary>
    [PersistentState]
    public TShown? Shown { get; set; }

    /// <summary>Reads what the view shows. No other read of the view goes on meanwhile, so it may set fields it renders.</summary>
    protected abstract Task<TShown> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Has <see cref="OnChanged"/> called whenever something the view shows changes.</summary>
    protected abstract void Subscribe();

    /// <summary>Undoes <see cref="Subscribe"/>, also where that was never called.</summary>
    protected abstract void Unsubscribe();

    protected override void OnInitialized()
    {
        // Ahead of the first read, so that no change falls between them. The server's rendering of the page ends here and has no one to update.
        if (RendererInfo.IsInteractive)
        {
            Subscribe();
        }
    }

    protected override Task OnParametersSetAsync() => RefreshAsync();

    /// <summary>Reads what the view shows again, on whichever thread it changed.</summary>
    protected void OnChanged() => _ = InvokeAsync(async () =>
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // The page was closed meanwhile.
        }
    });

    /// <summary>
    /// Reads and renders what the view shows, one read at a time: changes that come while one goes on, as they do in quick succession
    /// while an agent talks, are read together once it ends.
    /// </summary>
    private async Task RefreshAsync()
    {
        _stale = true;
        if (_reading)
        {
            return;
        }
        _reading = true;
        try
        {
            while (_stale)
            {
                _stale = false;
                Shown = await ReadAsync(_disposed.Token);
                StateHasChanged();
            }
        }
        finally
        {
            _reading = false;
        }
    }

    public void Dispose()
    {
        Unsubscribe();
        _disposed.Cancel();
        _disposed.Dispose();
        GC.SuppressFinalize(this);
    }
}
