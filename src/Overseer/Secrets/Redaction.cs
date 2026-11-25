namespace Overseer.Secrets;

/// <summary>Taking secrets out of text that is shown, stored or put in an error, such as what a command printed.</summary>
public static class Redaction
{
    /// <summary><paramref name="text"/> with <c>***</c> in place of each of <paramref name="secrets"/> it holds.</summary>
    /// <param name="secrets">The secret values; an empty one is nothing to take out.</param>
    public static string Without(string text, params IEnumerable<string> secrets) =>
        secrets.Where(secret => secret.Length > 0)
            // The longest first, so that one holding another goes whole.
            .OrderByDescending(secret => secret.Length)
            .Aggregate(text, (redacted, secret) => redacted.Replace(secret, "***", StringComparison.Ordinal));

    /// <summary>
    /// As <see cref="Without"/>, for text that is still arriving: also without an end of it that one of <paramref name="secrets"/>
    /// begins with, which may be that secret with its rest still to come.
    /// </summary>
    public static string WithoutSoFar(string text, IReadOnlyCollection<string> secrets)
    {
        var redacted = Without(text, secrets);
        var held = secrets.Select(secret => LengthOfBeginningAtEnd(redacted, secret)).DefaultIfEmpty().Max();
        return redacted[..^held];
    }

    /// <summary>The length of the longest beginning of <paramref name="secret"/>, short of the whole, that the text ends with.</summary>
    private static int LengthOfBeginningAtEnd(string text, string secret)
    {
        for (var length = Math.Min(text.Length, secret.Length - 1); length > 0; length--)
        {
            if (text.AsSpan().EndsWith(secret.AsSpan(0, length), StringComparison.Ordinal))
            {
                return length;
            }
        }
        return 0;
    }
}
