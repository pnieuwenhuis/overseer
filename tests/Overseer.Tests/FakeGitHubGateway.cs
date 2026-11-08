using System.Collections.Concurrent;
using Overseer.GitHub;
using Overseer.Secrets;

namespace Overseer.Tests;

/// <summary>
/// A GitHub that exists only in memory, scripted by the test. Like the real gateway, it reads the stored token through
/// the secret reader. GitHub rejects a token it was not told to accept with "Bad credentials", and knows only the
/// repositories it was told about or created.
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
    /// repository. Its default branch is <paramref name="defaultBranch"/>.
    /// </summary>
    public void AddRepository(string ownerAndName, string? reachableBy = null, string defaultBranch = DefaultBranch)
    {
        var repository = RepositoryName.Parse(ownerAndName);
        _repositories[Key(repository)] = new FakeRepository(repository, reachableBy, defaultBranch);
    }

    /// <summary>The default branch of a repository unless the test says otherwise, as GitHub's own default for new repositories.</summary>
    public const string DefaultBranch = "main";

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

    // By owner in lower case: why GitHub refuses to create repositories for that owner.
    private readonly ConcurrentDictionary<string, string> _creationRefusals = new();

    private readonly ConcurrentQueue<RepositoryName> _created = new();

    // In lower case: the organizations GitHub has, in which every token it accepts may create repositories.
    private readonly ConcurrentDictionary<string, bool> _organizations = new();

    /// <summary>
    /// Makes GitHub have the organization <paramref name="owner"/>. A token creates repositories for its own user and in
    /// organizations; for any other owner GitHub says "Not Found".
    /// </summary>
    public void AddOrganization(string owner) => _organizations[owner.ToLowerInvariant()] = true;

    /// <summary>The repositories GitHub was asked to create and created, in that order.</summary>
    public IReadOnlyList<RepositoryName> CreatedRepositories => [.. _created];

    /// <summary>
    /// Makes GitHub refuse to create repositories for <paramref name="owner"/> with <paramref name="error"/>, as for a
    /// token without the rights to.
    /// </summary>
    public void RefuseRepositoryCreation(string owner, string error) => _creationRefusals[owner.ToLowerInvariant()] = error;

    public async Task<GitHubRepositoryCreation?> CreateRepositoryAsync(RepositoryName repository, CancellationToken cancellationToken)
    {
        if (await secrets.ReadAsync(GitHubToken.SecretName, cancellationToken) is not { } token)
        {
            return null;
        }
        string login;
        switch (await CheckTokenAsync(token, cancellationToken))
        {
            case GitHubTokenCheck.Works works:
                login = works.Login;
                break;
            case GitHubTokenCheck.Fails fails:
                return new GitHubRepositoryCreation.Fails(fails.Error);
            default:
                throw new InvalidOperationException("A token check either works or fails.");
        }
        if (!string.Equals(repository.Owner, login, StringComparison.OrdinalIgnoreCase)
            && !_organizations.ContainsKey(repository.Owner.ToLowerInvariant()))
        {
            return new GitHubRepositoryCreation.Fails("Not Found (HTTP 404)");
        }
        if (_creationRefusals.GetValueOrDefault(repository.Owner.ToLowerInvariant()) is { } refusal)
        {
            return new GitHubRepositoryCreation.Fails(refusal);
        }
        if (!_repositories.TryAdd(Key(repository), new FakeRepository(repository, ReachableBy: null, DefaultBranch)))
        {
            return new GitHubRepositoryCreation.Fails("Repository creation failed: name already exists on this account (HTTP 422)");
        }
        _created.Enqueue(repository);
        return new GitHubRepositoryCreation.Created(repository);
    }

    // By owner/name in lower case, then branch and path: the files on the repositories' branches.
    private readonly ConcurrentDictionary<(string Repository, string Branch, string Path), bool> _files = new();

    /// <summary>Makes <paramref name="path"/> be on <paramref name="branch"/> of the repository <paramref name="ownerAndName"/>.</summary>
    public void AddFile(string ownerAndName, string branch, string path) =>
        _files[(Key(RepositoryName.Parse(ownerAndName)), branch, path)] = true;

    /// <summary>Takes <paramref name="path"/> off <paramref name="branch"/> of the repository <paramref name="ownerAndName"/>.</summary>
    public void RemoveFile(string ownerAndName, string branch, string path) =>
        _files.TryRemove((Key(RepositoryName.Parse(ownerAndName)), branch, path), out _);

    public async Task<GitHubFileCheck?> CheckFileAsync(RepositoryName repository, string? branch, string path, CancellationToken cancellationToken) =>
        await CheckRepositoryAsync(repository, cancellationToken) switch
        {
            null => null,
            GitHubRepositoryCheck.Fails fails => new GitHubFileCheck.Fails(fails.Error),
            GitHubRepositoryCheck.Reachable when _repositories[Key(repository)] is var found
                && _files.ContainsKey((Key(repository), branch ?? found.DefaultBranch, path)) => new GitHubFileCheck.Found(),
            GitHubRepositoryCheck.Reachable => new GitHubFileCheck.NotFound(),
            _ => new GitHubFileCheck.Fails(GitHubFileCheck.Unreachable(repository)),
        };

    private static string Key(RepositoryName repository) => repository.ToString().ToLowerInvariant();

    private sealed record FakeRepository(RepositoryName Name, string? ReachableBy, string DefaultBranch);
}
