namespace Overseer;

/// <summary>How messages to the user put things into words.</summary>
internal static class Prose
{
    /// <summary>The items as a sentence lists them: <c>a</c>, <c>a and b</c>, <c>a, b and c</c>.</summary>
    public static string List(IReadOnlyList<string> items) =>
        items.Count < 2 ? string.Concat(items) : $"{string.Join(", ", items.SkipLast(1))} and {items[^1]}";
}
