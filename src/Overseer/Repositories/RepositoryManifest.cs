using System.Text.RegularExpressions;

namespace Overseer.Repositories;

/// <summary>
/// The Repository Manifest: a repository's own <c>.overseer.yml</c>, which says how to set it up, build it, test it and start
/// the services its tests need (ADR 0003). Overseer reads it only once it implements Tasks; until then it only checks
/// whether a repository has one.
/// </summary>
public static partial class RepositoryManifest
{
    /// <summary>Its path from the repository's root.</summary>
    public const string Path = ".overseer.yml";

    /// <summary>
    /// Whether <paramref name="text"/> names the manifest's file: <c>.overseer.yml</c>, in that case, as a file name of its own,
    /// not part of another such as <c>.overseer.yml.example</c>.
    /// </summary>
    public static bool IsNamedIn(string? text) => text is not null && FileName().IsMatch(text);

    [GeneratedRegex(@"(?<![\w.-])\.overseer\.yml(?![\w-]|\.\w)")]
    private static partial Regex FileName();
}
