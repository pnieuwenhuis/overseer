using Microsoft.EntityFrameworkCore;
using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Persistence;
using Overseer.Specs;

namespace Overseer.Implementation;

/// <summary>
/// Each Intent's pull requests, from the Intent branch to the default branch of a repository it changed, opened through the GitHub
/// Gateway and kept in PostgreSQL: at most one per repository, also on GitHub, where one the Intent branch has already is taken
/// for it.
/// </summary>
public sealed class PullRequests(IGitHubGateway gitHub, SpecStore specs, IDbContextFactory<OverseerDbContext> databases)
{
    /// <summary>The longest title and description GitHub takes.</summary>
    private const int TitleLimit = 256;
    private const int BodyLimit = 65536;

    /// <summary>The Intent's pull requests, ordered by repository; empty when none is opened.</summary>
    public async Task<IReadOnlyList<PullRequest>> OfAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var stored = await database.PullRequests.AsNoTracking()
            .Where(pullRequest => pullRequest.IntentId == intentId)
            .ToListAsync(cancellationToken);
        return [.. stored
            .Select(pullRequest => new PullRequest(RepositoryName.Parse(pullRequest.Repository), pullRequest.Number, pullRequest.Url))
            .OrderBy(pullRequest => pullRequest.Repository.ToString(), StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Opens the pull request of each of <paramref name="repositories"/> that the Intent has none in yet, one after the other, titled
    /// by the Intent and described by the summary of its Approved Spec. Where GitHub has one from the Intent branch already, open,
    /// closed or merged, as when serve stopped before it could record it, that one is recorded instead. It stops at the first that
    /// can't be opened; those opened stay.
    /// </summary>
    /// <param name="opened">Called whenever one is opened, or found, once it is recorded.</param>
    /// <returns>Why one couldn't be opened, with GitHub's error; <c>null</c> once each repository has its pull request.</returns>
    public async Task<string?> OpenMissingAsync(
        Intent intent, IReadOnlyList<RepositoryName> repositories, Action opened, CancellationToken cancellationToken)
    {
        var there = (await OfAsync(intent.Id, cancellationToken)).Select(pullRequest => pullRequest.Repository).ToList();
        var missing = repositories.Except(there, RepositoryName.SameRepository).ToList();
        if (missing.Count == 0)
        {
            return null;
        }
        var approved = await specs.ApprovedOfAsync(intent, cancellationToken);
        var title = AtMost(intent.FirstLine, TitleLimit);
        var body = AtMost(approved.Summary, BodyLimit);
        foreach (var repository in missing)
        {
            // Asked first: GitHub would open a second one where the one there is has been closed or merged.
            PullRequest pullRequest;
            switch (await gitHub.FindPullRequestAsync(repository, intent.Branch, cancellationToken))
            {
                case GitHubPullRequestSearch.Found found:
                    pullRequest = new PullRequest(repository, found.Number, found.Url);
                    break;
                case GitHubPullRequestSearch.Fails fails:
                    return CouldNotOpen(repository, fails.Error);
                case null:
                    return CouldNotOpen(repository, NoToken);
                default:
                    switch (await gitHub.OpenPullRequestAsync(repository, intent.Branch, title, body, cancellationToken))
                    {
                        case GitHubPullRequestOpening.Opened opening:
                            pullRequest = new PullRequest(repository, opening.Number, opening.Url);
                            break;
                        case GitHubPullRequestOpening.Fails fails:
                            return CouldNotOpen(repository, fails.Error);
                        default:
                            return CouldNotOpen(repository, NoToken);
                    }
                    break;
            }
            await RecordAsync(intent.Id, pullRequest, cancellationToken);
            opened();
        }
        return null;
    }

    private const string NoToken = "No GitHub token is set: set one in Settings.";

    private static string CouldNotOpen(RepositoryName repository, string error) =>
        $"The pull request of {repository} could not be opened: {error}";

    private async Task RecordAsync(Guid intentId, PullRequest pullRequest, CancellationToken cancellationToken)
    {
        var name = pullRequest.Repository.ToString();
        var url = StorableText.Of(pullRequest.Url);
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await database.UpsertAsync(
            new PullRequestRecord { IntentId = intentId, Repository = name, Number = pullRequest.Number, Url = url },
            record => record.IntentId == intentId && record.Repository == name,
            update => update.SetProperty(record => record.Number, pullRequest.Number).SetProperty(record => record.Url, url),
            cancellationToken);
    }

    /// <summary><paramref name="text"/>, cut short with an ellipsis when it is longer than <paramref name="length"/>.</summary>
    private static string AtMost(string text, int length)
    {
        if (text.Length <= length)
        {
            return text;
        }
        var end = length - 1;
        // Not in the middle of a character that takes two.
        if (char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }
        return $"{text[..end]}…";
    }
}

/// <summary>A pull request of an Intent.</summary>
/// <param name="Url">Its page on GitHub.</param>
public sealed record PullRequest(RepositoryName Repository, int Number, string Url);

/// <summary>A pull request of an Intent, as PostgreSQL holds it.</summary>
public sealed class PullRequestRecord
{
    public required Guid IntentId { get; init; }

    /// <summary><c>owner/name</c> of its repository.</summary>
    public required string Repository { get; init; }

    public required int Number { get; set; }
    public required string Url { get; set; }
}
