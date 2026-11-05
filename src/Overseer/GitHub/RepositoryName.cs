using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Overseer.GitHub;

/// <summary>A GitHub repository's <c>owner/name</c>. GitHub matches both parts regardless of case.</summary>
public sealed partial record RepositoryName(string Owner, string Name)
{
    /// <summary>Why what the user entered is not a repository.</summary>
    public const string NotARepositoryError = "Enter a repository as owner/name, such as octocat/Hello-World, or paste its GitHub URL.";

    /// <summary>
    /// Tells whether two names are the same repository, as GitHub does: regardless of case. A record's own equality does not, for it
    /// also tells a name as the user typed it from how GitHub spells it.
    /// </summary>
    public static IEqualityComparer<RepositoryName> SameRepository { get; } = EqualityComparer<RepositoryName>.Create(
        (first, second) => StringComparer.OrdinalIgnoreCase.Equals(first?.ToString(), second?.ToString()),
        repository => StringComparer.OrdinalIgnoreCase.GetHashCode(repository.ToString()));

    public override string ToString() => $"{Owner}/{Name}";

    /// <summary>The repository's page on GitHub.</summary>
    public Uri WebUrl => new($"https://github.com/{Owner}/{Name}");

    /// <summary>
    /// Reads a repository as the user entered it: <c>owner/name</c>, or a GitHub URL as a browser or <c>git clone</c> shows
    /// it, such as <c>https://github.com/owner/name/tree/main</c> or <c>git@github.com:owner/name.git</c>.
    /// </summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out RepositoryName? repository)
    {
        repository = null;
        var text = input?.Trim() ?? "";
        var match = Url().Match(text) is { Success: true } url ? url : OwnerAndName().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var owner = match.Groups["owner"].Value;
        var name = match.Groups["name"].Value;
        // git clone URLs end in .git, which is not part of the name.
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".git".Length];
        }
        if (!IsValidOwner(owner) || !IsValidName(name))
        {
            return false;
        }
        repository = new RepositoryName(owner, name);
        return true;
    }

    /// <summary>Reads a repository as <see cref="TryParse"/> does, for one known to be valid, such as a stored one.</summary>
    /// <exception cref="FormatException">It is not a repository.</exception>
    public static RepositoryName Parse(string ownerAndName) =>
        TryParse(ownerAndName, out var repository) ? repository : throw new FormatException($"'{ownerAndName}' is not owner/name.");

    /// <summary>Why an owner that is not <see cref="IsValidOwner"/> is refused.</summary>
    public const string InvalidOwnerError = "An owner is up to 39 letters, digits and hyphens, not starting with a hyphen.";

    /// <summary>Why a name that is not <see cref="IsValidName"/> is refused.</summary>
    public const string InvalidNameError = "A name is up to 100 letters, digits, hyphens, underscores and dots, but not \".\" or \"..\".";

    /// <summary>Whether GitHub accepts <paramref name="owner"/> as a user's or organization's login.</summary>
    public static bool IsValidOwner(string owner) => ValidOwner().IsMatch(owner);

    /// <summary>Whether GitHub accepts <paramref name="name"/> as a repository's name.</summary>
    public static bool IsValidName(string name) => ValidName().IsMatch(name) && name is not ("." or "..");

    [GeneratedRegex(@"^(?<owner>[^/\s]+)/(?<name>[^/\s]+)$")]
    private static partial Regex OwnerAndName();

    // Anything after owner/name, such as /tree/main, a query or a fragment, is a page of the same repository.
    [GeneratedRegex(@"^(?:(?:https?://)?(?:www\.)?github\.com/|git@github\.com:|ssh://git@github\.com/)(?<owner>[^/\s?#]+)/(?<name>[^/\s?#]+)(?:[/?#]\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    // GitHub's rules: an owner is up to 39 letters, digits and hyphens, not starting with a hyphen; a name is up to 100
    // letters, digits, hyphens, underscores and dots.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,38}$")]
    private static partial Regex ValidOwner();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex ValidName();
}
