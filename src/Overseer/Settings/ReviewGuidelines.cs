namespace Overseer.Settings;

/// <summary>The human's global rules for what a Reviewer must check, as Markdown.</summary>
public static class ReviewGuidelines
{
    /// <summary>
    /// The Review Guidelines as Overseer keeps them, from what the user wrote: LF line breaks whatever the browser sent, and
    /// empty when there is nothing but whitespace.
    /// </summary>
    public static string FromEditor(string? text) => string.IsNullOrWhiteSpace(text) ? "" : text.ReplaceLineEndings("\n");
}
