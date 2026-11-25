namespace Overseer.Secrets;

/// <summary>Taking secrets out of text that is shown, stored or put in an error, such as what a command printed.</summary>
public static class Redaction
{
    /// <summary><paramref name="text"/> with <c>***</c> in place of each of <paramref name="secrets"/> it holds.</summary>
    /// <param name="secrets">The secret values; an empty one is nothing to take out.</param>
    public static string Without(string text, params IEnumerable<string> secrets) =>
        secrets.Where(secret => secret.Length > 0).Aggregate(text, (redacted, secret) => redacted.Replace(secret, "***", StringComparison.Ordinal));
}
