namespace Overseer.Sandboxes;

/// <summary>Text in the shell commands run in Sandboxes.</summary>
public static class ShellText
{
    /// <summary><paramref name="text"/> as one word the shell takes literally, whatever it holds.</summary>
    public static string Quoted(string text) => $"'{text.Replace("'", "'\\''")}'";
}
