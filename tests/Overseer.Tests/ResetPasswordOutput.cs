using System.Text.RegularExpressions;

namespace Overseer.Tests;

/// <summary>The username and password <c>overseer reset-password</c> printed, read the way a person would.</summary>
public sealed record ResetPasswordOutput(string Username, string Password)
{
    public static ResetPasswordOutput Parse(string output) => new(
        Regex.Match(output, @"^\s*Username:\s*(\S+)\s*$", RegexOptions.Multiline).Groups[1].Value,
        Regex.Match(output, @"^\s*Password:\s*(\S+)\s*$", RegexOptions.Multiline).Groups[1].Value);
}
