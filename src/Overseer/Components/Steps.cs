namespace Overseer.Components;

/// <summary>A list of steps, each shown done, under way, still to come, or stopped (the <c>steps</c> and <c>step-*</c> classes in app.css).</summary>
internal static class Steps
{
    /// <summary>
    /// The class of the step <paramref name="step"/> while the work is in <paramref name="phase"/>: its phases come in the order of
    /// their steps, so the steps before the current phase's are done and those after it still to come, and every phase after the
    /// last step's, such as being done, leaves them all done. Every step stopped once the work is <paramref name="failed"/>.
    /// </summary>
    public static string ClassOf<TPhase>(TPhase phase, TPhase step, TPhase failed) where TPhase : struct, Enum =>
        EqualityComparer<TPhase>.Default.Equals(phase, failed) ? "step-stopped"
        : Comparer<TPhase>.Default.Compare(phase, step) switch
        {
            0 => "step-current",
            > 0 => "step-done",
            _ => "step-pending",
        };
}
