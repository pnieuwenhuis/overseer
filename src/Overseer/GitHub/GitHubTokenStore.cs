using Overseer.Secrets;

namespace Overseer.GitHub;

/// <summary>
/// The GitHub token as the user sets, replaces and removes it, write-only like every secret. A new token is checked
/// through the GitHub Gateway before it is stored. One that fails the check replaces the token in use only once the
/// user confirms it; until then it is kept in memory only, never in the secret store.
/// </summary>
public sealed class GitHubTokenStore(ISecretStore secrets, IGitHubGateway gitHub)
{
    // The last token that failed the check, waiting for the user to store it anyway. Overseer has one user, so there is
    // one such token, whichever browser or session entered it. Every change swaps it atomically.
    private FailedToken? _failed;

    /// <summary>Why the token waiting for the user's confirmation failed the check; <c>null</c> when none is waiting.</summary>
    public string? FailedCheck => _failed?.Error;

    /// <summary>Whether a GitHub token is in use, without asking GitHub.</summary>
    public Task<bool> IsSetAsync(CancellationToken cancellationToken) => secrets.IsSetAsync(GitHubToken.SecretName, cancellationToken);

    /// <summary>Asks GitHub about the token in use, which the GitHub Gateway reads itself.</summary>
    /// <inheritdoc cref="IGitHubGateway.CheckTokenAsync(CancellationToken)"/>
    public Task<GitHubTokenCheck?> CheckAsync(CancellationToken cancellationToken) => gitHub.CheckTokenAsync(cancellationToken);

    /// <summary>Checks the token the user entered, and stores it in place of the token in use if it works.</summary>
    public async Task<GitHubTokenSaving> SaveAsync(string? token, CancellationToken cancellationToken)
    {
        token = token?.Trim() ?? "";
        if (!GitHubToken.IsWellFormed(token))
        {
            return new GitHubTokenSaving.Refused(token == "" ? "Enter a GitHub token." : GitHubToken.NotWellFormedError);
        }

        switch (await gitHub.CheckTokenAsync(token, cancellationToken))
        {
            case GitHubTokenCheck.Works works:
                await secrets.SetAsync(GitHubToken.SecretName, token, cancellationToken);
                Interlocked.Exchange(ref _failed, null);
                return new GitHubTokenSaving.Stored(works.Login);
            case GitHubTokenCheck.Fails fails:
                Interlocked.Exchange(ref _failed, new FailedToken(token, fails.Error));
                return new GitHubTokenSaving.AwaitsConfirmation(fails.Error);
            default:
                throw new InvalidOperationException("A token check either works or fails.");
        }
    }

    /// <summary>Stores the token that last failed the check, in place of the token in use, as the user confirmed.</summary>
    /// <returns>
    /// What it failed the check with, which is now the stored token's; <c>null</c> when no token was waiting, because the
    /// user discarded or replaced it since.
    /// </returns>
    public async Task<GitHubTokenCheck.Fails?> StoreFailedAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _failed, null) is not { } failed)
        {
            return null;
        }
        await secrets.SetAsync(GitHubToken.SecretName, failed.Token, cancellationToken);
        return new GitHubTokenCheck.Fails(failed.Error);
    }

    /// <summary>Forgets the token that last failed the check; the token in use stays.</summary>
    public void DiscardFailed() => Interlocked.Exchange(ref _failed, null);

    /// <summary>Removes the token in use, and forgets any token waiting for confirmation.</summary>
    public async Task RemoveAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _failed, null);
        await secrets.RemoveAsync(GitHubToken.SecretName, cancellationToken);
    }

    private sealed record FailedToken(string Token, string Error);
}

/// <summary>What saving a GitHub token did.</summary>
public abstract record GitHubTokenSaving
{
    private GitHubTokenSaving()
    {
    }

    /// <summary>GitHub accepted the token as <paramref name="Login"/>, and it is now the token in use.</summary>
    public sealed record Stored(string Login) : GitHubTokenSaving;

    /// <summary>
    /// The token failed the check with <paramref name="Error"/>. The token in use stays, unless the user confirms this one
    /// (<see cref="GitHubTokenStore.StoreFailedAsync"/>); until then <see cref="GitHubTokenStore.FailedCheck"/> says so.
    /// </summary>
    public sealed record AwaitsConfirmation(string Error) : GitHubTokenSaving;

    /// <summary>What the user entered can't be a GitHub token, so it was neither checked nor kept.</summary>
    public sealed record Refused(string Error) : GitHubTokenSaving;
}
