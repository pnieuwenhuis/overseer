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

    /// <summary>Checks that the GitHub token Overseer uses can reach <paramref name="repository"/>.</summary>
    /// <returns>What GitHub says about the repository; <c>null</c> when no GitHub token is set.</returns>
    Task<GitHubRepositoryCheck?> CheckRepositoryAsync(RepositoryName repository, CancellationToken cancellationToken);
}

/// <summary>What GitHub says about a repository, asked with the GitHub token Overseer uses.</summary>
public abstract record GitHubRepositoryCheck
{
    private GitHubRepositoryCheck()
    {
    }

    /// <summary>The token can reach the repository, which GitHub names <paramref name="Repository"/>, in GitHub's case.</summary>
    public sealed record Reachable(RepositoryName Repository) : GitHubRepositoryCheck;

    /// <summary>
    /// GitHub knows no such repository that the token can reach: it doesn't exist, or it is private and the token has no
    /// access to it. GitHub doesn't say which.
    /// </summary>
    public sealed record NotFound : GitHubRepositoryCheck;

    /// <summary>GitHub refused the request, as for a token it rejects, or could not be asked; <paramref name="Error"/> says why.</summary>
    public sealed record Fails(string Error) : GitHubRepositoryCheck;
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
