using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Overseer.Secrets;

namespace Overseer.GitHub;

/// <summary>The GitHub Gateway that calls api.github.com.</summary>
/// <remarks>
/// It uses a plain <see cref="HttpClient"/>, not one from <c>IHttpClientFactory</c>, so no request, and so no token,
/// is ever logged.
/// </remarks>
public sealed class GitHubGateway(ISecretReader secrets) : IGitHubGateway
{
    private static readonly HttpClient Http = CreateClient();

    public async Task<GitHubTokenCheck?> CheckTokenAsync(CancellationToken cancellationToken) =>
        await secrets.ReadAsync(GitHubToken.SecretName, cancellationToken) is { } token
            ? await CheckTokenAsync(token, cancellationToken)
            : null;

    public async Task<GitHubTokenCheck> CheckTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (!GitHubToken.IsWellFormed(token))
        {
            return new GitHubTokenCheck.Fails(GitHubToken.NotWellFormedError);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new GitHubTokenCheck.Fails(await ErrorAsync(response, cancellationToken));
            }
            var user = await response.Content.ReadFromJsonAsync<User>(cancellationToken);
            return user?.Login is { Length: > 0 } login
                ? new GitHubTokenCheck.Works(login)
                : new GitHubTokenCheck.Fails("GitHub accepted the token, but did not say which user it belongs to.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new GitHubTokenCheck.Fails($"GitHub could not be reached: {exception.Message}");
        }
    }

    /// <summary>GitHub's message for a failed request, such as "Bad credentials", with its HTTP status.</summary>
    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = $"HTTP {(int)response.StatusCode}";
        try
        {
            var error = await response.Content.ReadFromJsonAsync<Error>(cancellationToken);
            if (error?.Message is { Length: > 0 } message)
            {
                return $"{message} ({status})";
            }
        }
        catch (JsonException)
        {
            // Not GitHub's JSON error, as from a proxy in between: the status is all there is.
        }
        return $"{response.ReasonPhrase ?? "GitHub refused the request"} ({status})";
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        // GitHub rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("overseer", OverseerVersion.Current));
        return http;
    }

    private sealed record User(string? Login);

    private sealed record Error(string? Message);
}
