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

    /// <summary>
    /// Makes this GitHub have what <paramref name="before"/> had, as GitHub keeps it all while Overseer restarts: the tokens it
    /// accepts or rejects, its organizations, its repositories with their files and branches, its pull requests, and what it
    /// refuses.
    /// </summary>
    public void CarryOver(FakeGitHubGateway before)
    {
        static void Copy<TKey, TValue>(ConcurrentDictionary<TKey, TValue> from, ConcurrentDictionary<TKey, TValue> to)
            where TKey : notnull
        {
            foreach (var (key, value) in from)
            {
                to[key] = value;
            }
        }

        Copy(before._tokens, _tokens);
        Copy(before._organizations, _organizations);
        Copy(before._repositories, _repositories);
        Copy(before._creationRefusals, _creationRefusals);
        Copy(before._files, _files);
        Copy(before._branches, _branches);
        Copy(before._pullRequestRefusals, _pullRequestRefusals);
        foreach (var created in before._created)
        {
            _created.Enqueue(created);
        }
        foreach (var pullRequest in before._pullRequests)
        {
            _pullRequests.Enqueue(pullRequest);
        }
    }

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

    // By owner/name in lower case, then branch: the branches besides the default one, and whether each has commits of its own.
    private readonly ConcurrentDictionary<(string Repository, string Branch), bool> _branches = new();

    /// <summary>Makes the repository <paramref name="ownerAndName"/> have the branch <paramref name="branch"/>, as pushed to.</summary>
    /// <param name="ahead">Whether it has commits the default branch doesn't, without which GitHub opens no pull request.</param>
    public void AddBranch(string ownerAndName, string branch, bool ahead = true) =>
        _branches[(Key(RepositoryName.Parse(ownerAndName)), branch)] = ahead;

    // By owner/name in lower case: why GitHub refuses to open a pull request in that repository.
    private readonly ConcurrentDictionary<string, string> _pullRequestRefusals = new();

    private readonly ConcurrentQueue<FakePullRequest> _pullRequests = new();

    private volatile Task _pullRequestsHeld = Task.CompletedTask;

    /// <summary>
    /// Makes GitHub answer a request to open a pull request only once <paramref name="until"/> completes, so that a test can act
    /// meanwhile.
    /// </summary>
    public void HoldPullRequests(Task until) => _pullRequestsHeld = until;

    // By owner/name in lower case: what GitHub's answer to opening a pull request in that repository waits for.
    private readonly ConcurrentDictionary<string, Task> _pullRequestAnswersHeld = new();

    /// <summary>
    /// Makes GitHub open a pull request in the repository <paramref name="ownerAndName"/> and say so only once
    /// <paramref name="until"/> completes, so that a test can act while it is open and not yet known to be.
    /// </summary>
    public void HoldPullRequestAnswers(string ownerAndName, Task until) =>
        _pullRequestAnswersHeld[Key(RepositoryName.Parse(ownerAndName))] = until;

    /// <summary>Makes GitHub refuse to open pull requests in the repository <paramref name="ownerAndName"/> with <paramref name="error"/>.</summary>
    public void RefusePullRequests(string ownerAndName, string error) =>
        _pullRequestRefusals[Key(RepositoryName.Parse(ownerAndName))] = error;

    /// <summary>Makes GitHub open pull requests in the repository <paramref name="ownerAndName"/> again.</summary>
    public void AllowPullRequests(string ownerAndName) => _pullRequestRefusals.TryRemove(Key(RepositoryName.Parse(ownerAndName)), out _);

    /// <summary>The pull requests GitHub opened, in that order.</summary>
    public IReadOnlyList<FakePullRequest> PullRequests => [.. _pullRequests];

    public async Task<GitHubPullRequestOpening?> OpenPullRequestAsync(
        RepositoryName repository, string branch, string title, string body, CancellationToken cancellationToken)
    {
        await _pullRequestsHeld.WaitAsync(cancellationToken);
        switch (await CheckRepositoryAsync(repository, cancellationToken))
        {
            case null:
                return null;
            case GitHubRepositoryCheck.Fails fails:
                return new GitHubPullRequestOpening.Fails(fails.Error);
            case GitHubRepositoryCheck.NotFound:
                return new GitHubPullRequestOpening.Fails(GitHubFileCheck.Unreachable(repository));
        }
        if (_pullRequestRefusals.GetValueOrDefault(Key(repository)) is { } refusal)
        {
            return new GitHubPullRequestOpening.Fails(refusal);
        }
        var opening = Open(repository, branch, title, body);
        if (_pullRequestAnswersHeld.TryGetValue(Key(repository), out var held))
        {
            await held.WaitAsync(cancellationToken);
        }
        return opening;
    }

    /// <summary>Opens the pull request, or finds the one that is open for the branch already.</summary>
    private GitHubPullRequestOpening Open(RepositoryName repository, string branch, string title, string body)
    {
        var found = _repositories[Key(repository)];
        lock (_pullRequests)
        {
            if (_pullRequests.FirstOrDefault(open => open.Repository == found.Name && open.Head == branch) is { } existing)
            {
                return new GitHubPullRequestOpening.Opened(existing.Number, existing.Url);
            }
            if (!_branches.TryGetValue((Key(repository), branch), out var ahead))
            {
                return new GitHubPullRequestOpening.Fails("Validation Failed: head invalid (HTTP 422)");
            }
            if (!ahead)
            {
                return new GitHubPullRequestOpening.Fails(
                    $"Validation Failed: No commits between {found.DefaultBranch} and {branch} (HTTP 422)");
            }
            var number = _pullRequests.Count(open => open.Repository == found.Name) + 1;
            var opened = new FakePullRequest(
                found.Name, number, $"{found.Name.WebUrl}/pull/{number}", branch, found.DefaultBranch, title, body);
            _pullRequests.Enqueue(opened);
            return new GitHubPullRequestOpening.Opened(opened.Number, opened.Url);
        }
    }

    private static string Key(RepositoryName repository) => repository.ToString().ToLowerInvariant();

    private sealed record FakeRepository(RepositoryName Name, string? ReachableBy, string DefaultBranch);
}

/// <summary>A pull request the fake GitHub opened.</summary>
/// <param name="Head">The branch it comes from.</param>
/// <param name="Base">The branch it goes to.</param>
public sealed record FakePullRequest(
    RepositoryName Repository, int Number, string Url, string Head, string Base, string Title, string Body);
