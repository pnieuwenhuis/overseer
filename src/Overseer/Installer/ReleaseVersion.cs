using System.Text.RegularExpressions;

namespace Overseer.Installer;

/// <summary>A release's version, <c>X.Y.Z</c> with an optional prerelease suffix, in the order Semantic Versioning gives them.</summary>
internal sealed partial class ReleaseVersion : IComparable<ReleaseVersion>
{
    private readonly int[] _core;
    private readonly string[] _prerelease;

    private ReleaseVersion(int[] core, string[] prerelease) => (_core, _prerelease) = (core, prerelease);

    /// <returns>The version, or null when <paramref name="version"/> isn't one.</returns>
    public static ReleaseVersion? Parse(string version)
    {
        var match = Pattern().Match(version);
        return match.Success
            ? new ReleaseVersion(
                [int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value)],
                match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [])
            : null;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        ArgumentNullException.ThrowIfNull(other);
        for (var i = 0; i < _core.Length; i++)
        {
            if (_core[i] != other._core[i])
            {
                return _core[i].CompareTo(other._core[i]);
            }
        }
        // A prerelease comes before its release.
        if (_prerelease.Length == 0 || other._prerelease.Length == 0)
        {
            return other._prerelease.Length.CompareTo(_prerelease.Length);
        }
        foreach (var (mine, theirs) in _prerelease.Zip(other._prerelease))
        {
            var comparison = (IsNumeric(mine), IsNumeric(theirs)) switch
            {
                // Without leading zeros, the longer number is the larger one.
                (true, true) => mine.Length != theirs.Length ? mine.Length.CompareTo(theirs.Length) : string.CompareOrdinal(mine, theirs),
                (true, false) => -1,
                (false, true) => 1,
                (false, false) => string.CompareOrdinal(mine, theirs),
            };
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static bool IsNumeric(string identifier) => identifier.All(char.IsAsciiDigit);

    // Build metadata (+...) plays no part in the order.
    [GeneratedRegex(@"^(\d{1,9})\.(\d{1,9})\.(\d{1,9})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Pattern();
}
