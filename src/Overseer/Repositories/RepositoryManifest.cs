namespace Overseer.Repositories;

/// <summary>
/// The Repository Manifest: a repository's own <c>.overseer.yml</c>, which says how to set it up, build it, test it and start
/// the services its tests need (ADR 0003). Overseer reads it only once it implements Tasks; until then it only checks
/// whether a repository has one.
/// </summary>
public static class RepositoryManifest
{
    /// <summary>Its path from the repository's root.</summary>
    public const string Path = ".overseer.yml";
}
