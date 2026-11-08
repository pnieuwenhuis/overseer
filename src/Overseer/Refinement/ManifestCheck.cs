using Overseer.GitHub;
using Overseer.Repositories;

namespace Overseer.Refinement;

/// <summary>
/// Which Repositories in scope have no Repository Manifest on their default branch, as the GitHub Gateway says, and which it
/// couldn't tell. Only whether the file is there: what it holds is read once Tasks are implemented.
/// </summary>
/// <param name="Lacking">Those GitHub finds no Repository Manifest in, in scope order.</param>
/// <param name="Unknown">Those GitHub couldn't be asked about, in scope order, each with why.</param>
internal sealed record ManifestCheck(IReadOnlyList<RepositoryName> Lacking, IReadOnlyList<(RepositoryName Repository, string Error)> Unknown)
{
    /// <summary>Asks the GitHub Gateway, for each of <paramref name="repositories"/>, whether it has a Repository Manifest.</summary>
    public static async Task<ManifestCheck> RunAsync(
        IGitHubGateway gitHub, IReadOnlyList<RepositoryName> repositories, CancellationToken cancellationToken)
    {
        var checks = await Task.WhenAll(repositories.Select(repository =>
            gitHub.CheckFileAsync(repository, branch: null, RepositoryManifest.Path, cancellationToken)));
        var lacking = new List<RepositoryName>();
        var unknown = new List<(RepositoryName, string)>();
        foreach (var (repository, check) in repositories.Zip(checks))
        {
            switch (check)
            {
                case GitHubFileCheck.Found:
                    break;
                case GitHubFileCheck.NotFound:
                    lacking.Add(repository);
                    break;
                case GitHubFileCheck.Fails fails:
                    unknown.Add((repository, $"GitHub could not say: {fails.Error}"));
                    break;
                case null:
                    unknown.Add((repository, "Overseer has no GitHub token."));
                    break;
                default:
                    throw new InvalidOperationException("GitHub either finds the file, doesn't, or fails.");
            }
        }
        return new ManifestCheck(lacking, unknown);
    }
}
