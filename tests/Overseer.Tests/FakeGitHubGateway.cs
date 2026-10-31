using System.Collections.Concurrent;
using Overseer.GitHub;
using Overseer.Secrets;

namespace Overseer.Tests;

/// <summary>
/// A GitHub that exists only in memory, scripted by the test. Like the real gateway, it reads the stored token through
/// the secret reader. GitHub rejects a token it was not told to accept with "Bad credentials".
/// </summary>
public sealed class FakeGitHubGateway(ISecretReader secrets) : IGitHubGateway
{
    private readonly ConcurrentDictionary<string, GitHubTokenCheck> _tokens = new();

    /// <summary>Makes GitHub accept <paramref name="token"/> as the user <paramref name="login"/>.</summary>
    public void AcceptToken(string token, string login) => _tokens[token] = new GitHubTokenCheck.Works(login);

    /// <summary>Makes GitHub reject <paramref name="token"/> with <paramref name="error"/>.</summary>
    public void RejectToken(string token, string error) => _tokens[token] = new GitHubTokenCheck.Fails(error);

    public async Task<GitHubTokenCheck?> CheckTokenAsync(CancellationToken cancellationToken) =>
        await secrets.ReadAsync(GitHubToken.SecretName, cancellationToken) is { } token
            ? await CheckTokenAsync(token, cancellationToken)
            : null;

    public Task<GitHubTokenCheck> CheckTokenAsync(string token, CancellationToken cancellationToken) =>
        Task.FromResult(_tokens.GetValueOrDefault(token) ?? new GitHubTokenCheck.Fails("Bad credentials (HTTP 401)"));
}
