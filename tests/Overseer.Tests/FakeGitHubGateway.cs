using System.Collections.Concurrent;
using Overseer.GitHub;
using Overseer.Secrets;

namespace Overseer.Tests;

/// <summary>
/// A GitHub that exists only in memory, scripted by the test. Like the real gateway, it reads the stored token through
/// the secret reader. GitHub rejects a token it was not told to accept with "Bad credentials", and knows only the
/// repositories it was told about.
/// </summary>
public sealed class FakeGitHubGateway(ISecretReader secrets) : IGitHubGateway
{
    private readonly ConcurrentDictionary<string, GitHubTokenCheck> _tokens = new();

    // By owner/name in lower case, since GitHub matches names regardless of case.
    private readonly ConcurrentDictionary<string, FakeRepository> _repositories = new();

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

    /// <summary>
    /// Makes GitHub have the repository <paramref name="ownerAndName"/>, in that case. Every token GitHub accepts can reach
    /// it, or only <paramref name="reachableBy"/>: to any other, GitHub says it doesn't exist, as it does for a private
    /// repository.
    /// </summary>
    public void AddRepository(string ownerAndName, string? reachableBy = null)
    {
        var repository = RepositoryName.Parse(ownerAndName);
        _repositories[Key(repository)] = new FakeRepository(repository, reachableBy);
    }

    public async Task<GitHubRepositoryCheck?> CheckRepositoryAsync(RepositoryName repository, CancellationToken cancellationToken)
    {
        if (await secrets.ReadAsync(GitHubToken.SecretName, cancellationToken) is not { } token)
        {
            return null;
        }
        if (await CheckTokenAsync(token, cancellationToken) is GitHubTokenCheck.Fails fails)
        {
            return new GitHubRepositoryCheck.Fails(fails.Error);
        }
        return _repositories.GetValueOrDefault(Key(repository)) is { } found && (found.ReachableBy ?? token) == token
            ? new GitHubRepositoryCheck.Reachable(found.Name)
            : new GitHubRepositoryCheck.NotFound();
    }

    private static string Key(RepositoryName repository) => repository.ToString().ToLowerInvariant();

    private sealed record FakeRepository(RepositoryName Name, string? ReachableBy);
}
