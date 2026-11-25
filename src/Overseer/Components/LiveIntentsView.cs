using Microsoft.AspNetCore.Components;
using Overseer.Implementation;
using Overseer.Intents;
using Overseer.Refinement;

namespace Overseer.Components;

/// <summary>A view of Intents that stays current as an Intent, its Refinement or its implementation changes.</summary>
/// <typeparam name="TShown">What the view reads from the Intent Lifecycle, Refinement and the Task Runner to show.</typeparam>
public abstract class LiveIntentsView<TShown> : LiveView<TShown>
{
    [Inject]
    protected IntentLifecycle Lifecycle { get; set; } = default!;

    [Inject]
    protected Refinements Refinements { get; set; } = default!;

    [Inject]
    protected TaskRunner TaskRunner { get; set; } = default!;

    protected override void Subscribe()
    {
        Lifecycle.Changed += OnChanged;
        Refinements.Changed += OnChanged;
        TaskRunner.Changed += OnChanged;
    }

    protected override void Unsubscribe()
    {
        Lifecycle.Changed -= OnChanged;
        Refinements.Changed -= OnChanged;
        TaskRunner.Changed -= OnChanged;
    }
}
