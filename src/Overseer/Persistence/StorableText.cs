using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Overseer.Persistence;

/// <summary>
/// Text from outside Overseer as PostgreSQL can store it. An error can hold whatever a command, a file or a provider said, and
/// saving it must not fail: the failure would then never be recorded, and the work would show as going on for good.
/// </summary>
public static class StorableText
{
    /// <summary>
    /// <paramref name="text"/> without NUL characters, which a text column takes none of, and with U+FFFD for each half of a
    /// surrogate pair that is on its own, which has no UTF-8.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Of(string? text) =>
        // Encoding and decoding again replaces what UTF-8 can't hold.
        text is null ? null : Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text.Replace("\0", "")));
}
