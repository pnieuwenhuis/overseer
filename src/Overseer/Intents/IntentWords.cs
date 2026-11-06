namespace Overseer.Intents;

internal static class IntentWords
{
    /// <summary>The state as the user reads it, in the glossary's words.</summary>
    public static string InWords(this IntentState state) => state switch
    {
        IntentState.AwaitingSpecApproval => "Awaiting Spec Approval",
        IntentState.InReview => "In Review",
        // One word, the same in the code and the glossary.
        _ => state.ToString(),
    };

    /// <summary>What the light says in words, so that it doesn't rely on its colour.</summary>
    public static string InWords(this IntentLight light) => light switch
    {
        IntentLight.Green => "Working",
        _ => throw new ArgumentOutOfRangeException(nameof(light), light, null),
    };

    /// <summary>A Working Time as the user reads it, in hours and minutes, such as "2 h 15 min".</summary>
    /// <param name="shown">The Working Time as <see cref="WorkingTime.ShownAt"/> gives it, in whole minutes.</param>
    public static string WorkingTimeInWords(TimeSpan shown)
    {
        var (hours, minutes) = Math.DivRem((long)shown.TotalMinutes, 60);
        return (hours, minutes) switch
        {
            (0, _) => $"{minutes} min",
            (_, 0) => $"{hours} h",
            _ => $"{hours} h {minutes} min",
        };
    }
}
