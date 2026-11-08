using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        try
        {
            using var request = Get("user", token);
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
        catch (Exception exception) when (IsUnreachable(exception, cancellationToken))
        {
            return new GitHubTokenCheck.Fails(UnreachableError(exception));
        }
    }

    public async Task<GitHubRepositoryCheck?> CheckRepositoryAsync(RepositoryName repository, CancellationToken cancellationToken)
    {
        if (await secrets.ReadAsync(GitHubToken.SecretName, cancellationToken) is not { } token)
        {
            return null;
        }
        return GitHubToken.IsWellFormed(token)
            ? await CheckRepositoryAsync(repository, token, cancellationToken)
            : new GitHubRepositoryCheck.Fails(GitHubToken.NotWellFormedError);
    }

    /// <summary>Checks that <paramref name="token"/>, well-formed, can reach <paramref name="repository"/>.</summary>
    private static async Task<GitHubRepositoryCheck> CheckRepositoryAsync(RepositoryName repository, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = Get($"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}", token);
            using var response = await Http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new GitHubRepositoryCheck.NotFound();
            }
            if (!response.IsSuccessStatusCode)
            {
                return new GitHubRepositoryCheck.Fails(await ErrorAsync(response, cancellationToken));
            }
            var found = await response.Content.ReadFromJsonAsync<Repository>(cancellationToken);
            return RepositoryName.TryParse(found?.FullName, out var name)
                ? new GitHubRepositoryCheck.Reachable(name)
                : new GitHubRepositoryCheck.Fails("GitHub found the repository, but did not say its name.");
        }
        catch (Exception exception) when (IsUnreachable(exception, cancellationToken))
        {
            return new GitHubRepositoryCheck.Fails(UnreachableError(exception));
        }
    }

    public async Task<GitHubRepositoryCreation?> CreateRepositoryAsync(RepositoryName repository, CancellationToken cancellationToken)
    {
        if (await secrets.ReadAsync(GitHubToken.SecretName, cancellationToken) is not { } token)
        {
            return null;
        }
        // GitHub creates a user's repositories and an organization's at different endpoints, so it first says whose token this is.
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
        var forUser = string.Equals(login, repository.Owner, StringComparison.OrdinalIgnoreCase);

        try
        {
            using var request = Request(HttpMethod.Post, forUser ? "user/repos" : $"orgs/{Uri.EscapeDataString(repository.Owner)}/repos", token);
            request.Content = JsonContent.Create(new NewRepository(repository.Name, Private: true));
            using var response = await Http.SendAsync(request, cancellationToken);
            if (!forUser && response.StatusCode == HttpStatusCode.NotFound)
            {
                // GitHub says only "Not Found" for an owner that is another user, or an organization the token can't see.
                return new GitHubRepositoryCreation.Fails(
                    $"{await ErrorAsync(response, cancellationToken)}: {repository.Owner} is neither the token's user, {login}, " +
                    "nor an organization the token can create repositories in");
            }
            if (!response.IsSuccessStatusCode)
            {
                return new GitHubRepositoryCreation.Fails(await ErrorAsync(response, cancellationToken));
            }
            var created = await response.Content.ReadFromJsonAsync<Repository>(cancellationToken);
            return RepositoryName.TryParse(created?.FullName, out var name)
                ? new GitHubRepositoryCreation.Created(name)
                : new GitHubRepositoryCreation.Fails("GitHub created the repository, but did not say its name.");
        }
        catch (Exception exception) when (IsUnreachable(exception, cancellationToken))
        {
            // GitHub may have created it before the answer was lost.
            return new GitHubRepositoryCreation.Fails(
                $"{UnreachableError(exception)} It may have created the repository anyway: see {repository.WebUrl}");
        }
    }

    public async Task<GitHubFileCheck?> CheckFileAsync(RepositoryName repository, string? branch, string path, CancellationToken cancellationToken)
    {
        if (await secrets.ReadAsync(GitHubToken.SecretName, cancellationToken) is not { } token)
        {
            return null;
        }
        if (!GitHubToken.IsWellFormed(token))
        {
            return new GitHubFileCheck.Fails(GitHubToken.NotWellFormedError);
        }

        try
        {
            var filePath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            // Without a ref, GitHub looks on the repository's default branch.
            using var request = Get(
                $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/contents/{filePath}" +
                (branch is null ? "" : $"?ref={Uri.EscapeDataString(branch)}"),
                token);
            using var response = await Http.SendAsync(request, cancellationToken);
            // Also for a branch that doesn't exist, for an empty repository ("This repository is empty."), and for a repository the
            // token can't reach, which only asking for the repository itself tells apart.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return await CheckRepositoryAsync(repository, token, cancellationToken) switch
                {
                    GitHubRepositoryCheck.Reachable => new GitHubFileCheck.NotFound(),
                    GitHubRepositoryCheck.NotFound => new GitHubFileCheck.Fails(GitHubFileCheck.Unreachable(repository)),
                    GitHubRepositoryCheck.Fails fails => new GitHubFileCheck.Fails(fails.Error),
                    _ => throw new InvalidOperationException("GitHub either reaches the repository, doesn't, or fails."),
                };
            }
            return response.IsSuccessStatusCode
                ? new GitHubFileCheck.Found()
                : new GitHubFileCheck.Fails(await ErrorAsync(response, cancellationToken));
        }
        catch (Exception exception) when (IsUnreachable(exception, cancellationToken))
        {
            return new GitHubFileCheck.Fails(UnreachableError(exception));
        }
    }

    /// <summary>A GET request to the GitHub API, authenticated with <paramref name="token"/>.</summary>
    private static HttpRequestMessage Get(string path, string token) => Request(HttpMethod.Get, path, token);

    /// <summary>A request to the GitHub API, authenticated with <paramref name="token"/>.</summary>
    private static HttpRequestMessage Request(HttpMethod method, string path, string token) =>
        new(method, path) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };

    /// <summary>Whether <paramref name="exception"/> means GitHub could not be asked, or did not answer in JSON.</summary>
    private static bool IsUnreachable(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or JsonException
        || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private static string UnreachableError(Exception exception) => $"GitHub could not be reached: {exception.Message}";

    /// <summary>GitHub's message for a failed request, such as "Bad credentials", with its HTTP status.</summary>
    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = $"HTTP {(int)response.StatusCode}";
        try
        {
            var error = await response.Content.ReadFromJsonAsync<Error>(cancellationToken);
            if (error?.Message is { Length: > 0 } message)
            {
                // A 422 says what is wrong only in its details, such as "name already exists on this account".
                var details = (error.Errors ?? []).Select(Detail).Where(detail => detail.Length > 0).ToList();
                return details.Count == 0
                    ? $"{message} ({status})"
                    : $"{message.TrimEnd('.')}: {string.Join("; ", details)} ({status})";
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

    /// <summary>One of GitHub's error details: a message, or an object with a message or a field and a code.</summary>
    private static string Detail(JsonElement detail) => detail.ValueKind switch
    {
        JsonValueKind.String => detail.GetString() ?? "",
        JsonValueKind.Object when StringProperty(detail, "message") is { Length: > 0 } message => message,
        JsonValueKind.Object => $"{StringProperty(detail, "field")} {StringProperty(detail, "code")}".Trim(),
        _ => "",
    };

    /// <summary>The string value of <paramref name="element"/>'s <paramref name="property"/>; <c>null</c> when it has no such string.</summary>
    private static string? StringProperty(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record Error(string? Message, IReadOnlyList<JsonElement>? Errors);

    private sealed record NewRepository(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("private")] bool Private);

    private sealed record Repository([property: JsonPropertyName("full_name")] string? FullName);
}
