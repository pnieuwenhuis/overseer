using Microsoft.AspNetCore.Components;
using Overseer.Intents;
using Overseer.Refinement;

namespace Overseer.Components;

/// <summary>
/// A view of Intents that stays current: it reads what it shows when it starts and when its parameters change, and again whenever an
/// Intent or its Refinement changes. Rendered with <c>InteractiveServer</c> it updates over Blazor's SignalR connection.
/// </summary>
/// <typeparam name="TShown">What the view reads from the Intent Lifecycle and Refinement to show.</typeparam>
public abstract class LiveIntentsView<TShown> : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _disposed = new();
    private int _lastReadBegun;

    [Inject]
    protected IntentLifecycle Lifecycle { get; set; } = default!;

    [Inject]
    protected Refinements Refinements { get; set; } = default!;

    /// <summary>
    /// What was last read; <c>null</c> until the first read is done. Persisted from the server's rendering of the page into the live
    /// connection, whose first render would otherwise blank the page until its own read is done.
    /// </summary>
    [PersistentState]
    public TShown? Shown { get; set; }

    /// <summary>Reads what the view shows.</summary>
    protected abstract Task<TShown> ReadAsync(CancellationToken cancellationToken);

    protected override void OnInitialized()
    {
        // Ahead of the first read, so that no change falls between them. The server's rendering of the page ends here and has no one to update.
        if (RendererInfo.IsInteractive)
        {
            Lifecycle.Changed += OnChanged;
            Refinements.Changed += OnChanged;
        }
    }

    protected override Task OnParametersSetAsync() => RefreshAsync();

    private void OnChanged() => _ = InvokeAsync(async () =>
    {
        try
        {
            await RefreshAsync();
            StateHasChanged();
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // The page was closed meanwhile.
        }
    });

    private async Task RefreshAsync()
    {
        // Reads overlap when changes follow each other closely, and may end in any order: only the one begun last shows what is current.
        var read = ++_lastReadBegun;
        var shown = await ReadAsync(_disposed.Token);
        if (read == _lastReadBegun)
        {
            Shown = shown;
        }
    }

    public void Dispose()
    {
        Lifecycle.Changed -= OnChanged;
        Refinements.Changed -= OnChanged;
        _disposed.Cancel();
        _disposed.Dispose();
        GC.SuppressFinalize(this);
    }
}
