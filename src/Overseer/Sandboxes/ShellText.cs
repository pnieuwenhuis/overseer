namespace Overseer.Sandboxes;

/// <summary>Text in the shell commands run in Sandboxes.</summary>
public static class ShellText
{
    /// <summary><paramref name="text"/> as one word the shell takes literally, whatever it holds.</summary>
    public static string Quoted(string text) => $"'{text.Replace("'", "'\\''")}'";

    /// <summary>
    /// A script that prints what <paramref name="script"/> writes to standard output and nothing else when it succeeds, as for
    /// output that is read: what it writes to standard error, such as warnings, is printed only when it fails.
    /// </summary>
    public static string OutputAlone(string script) =>
        $$"""
        errors=$(mktemp) || exit
        {
        {{script}}
        } 2> "$errors"
        status=$?
        [ "$status" -eq 0 ] || cat "$errors"
        rm -f "$errors"
        exit "$status"
        """;
}
