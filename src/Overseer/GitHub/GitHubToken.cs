namespace Overseer.GitHub;

/// <summary>The user's GitHub fine-grained token, which Overseer and its agents work with GitHub as.</summary>
public static class GitHubToken
{
    /// <summary>The name of the secret that holds it.</summary>
    public const string SecretName = "github-token";

    /// <summary>
    /// Whether <paramref name="token"/> can be a GitHub token at all: GitHub's tokens are printable ASCII without spaces.
    /// This also keeps line breaks out of the HTTP header the token goes into.
    /// </summary>
    public static bool IsWellFormed(string token) => token.Length > 0 && token.All(character => character is > ' ' and < '\u007f');

    /// <summary>Why a token that is not <see cref="IsWellFormed"/> is refused.</summary>
    public const string NotWellFormedError = "This is not a GitHub token: a token has no spaces, line breaks or other special characters.";
}
