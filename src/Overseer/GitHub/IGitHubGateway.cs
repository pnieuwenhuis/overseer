namespace Overseer.GitHub;

/// <summary>
/// The only component that calls the GitHub API. It reads the GitHub token itself, through the secret reader, so no
/// caller ever holds the stored token.
/// </summary>
public interface IGitHubGateway
{
    /// <summary>Checks the GitHub token Overseer uses, as stored in the secret store.</summary>
    /// <returns>The login it works as, or GitHub's error; <c>null</c> when no GitHub token is set.</returns>
    Task<GitHubTokenCheck?> CheckTokenAsync(CancellationToken cancellationToken);

    /// <summary>Checks a token the user entered, before it is stored.</summary>
    /// <returns>The login it works as, or GitHub's error.</returns>
    Task<GitHubTokenCheck> CheckTokenAsync(string token, CancellationToken cancellationToken);
}

/// <summary>What GitHub says about a token.</summary>
public abstract record GitHubTokenCheck
{
    private GitHubTokenCheck()
    {
    }

    /// <summary>GitHub accepts the token, as the user <paramref name="Login"/>.</summary>
    public sealed record Works(string Login) : GitHubTokenCheck;

    /// <summary>GitHub rejected the token, or could not be asked; <paramref name="Error"/> says why.</summary>
    public sealed record Fails(string Error) : GitHubTokenCheck;
}
