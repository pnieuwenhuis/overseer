using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Overseer.GitHub;
using Overseer.Secrets;

namespace Overseer.Tests;

/// <summary>What every GitHub Gateway must do. Derive from this once per implementation.</summary>
public abstract class GitHubGatewayContract : IAsyncDisposable
{
    // The repositories a test created, which are deleted again after it.
    private readonly List<RepositoryName> _created = [];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"overseer-{Guid.NewGuid():N}");

    protected GitHubGatewayContract()
    {
        Secrets = new FileSecretStore(Path.Combine(_directory, "secrets.json"));
    }

    /// <summary>The secret store the gateway reads the GitHub token from.</summary>
    protected FileSecretStore Secrets { get; }

    /// <summary>The gateway under test, reading the GitHub token from <see cref="Secrets"/>.</summary>
    protected abstract IGitHubGateway Gateway { get; }

    /// <summary>A token GitHub accepts, and the login it works as.</summary>
    protected abstract (string Token, string Login) WorkingToken { get; }

    /// <summary>A well-formed token GitHub has never issued.</summary>
    private const string UnknownToken = "github_pat_11AAAAAAA0000000000000_unknownunknownunknownunknownunknownunknownunknownun";

    [Fact]
    public async Task A_working_token_works_as_its_login()
    {
        var (token, login) = WorkingToken;

        var check = await Gateway.CheckTokenAsync(token, TestContext.Current.CancellationToken);

        Assert.Equal(new GitHubTokenCheck.Works(login), check);
    }

    [Fact]
    public async Task An_unknown_token_fails_with_GitHubs_error()
    {
        var check = await Gateway.CheckTokenAsync(UnknownToken, TestContext.Current.CancellationToken);

        var fails = Assert.IsType<GitHubTokenCheck.Fails>(check);
        Assert.Contains("Bad credentials", fails.Error);
    }

    [Fact]
    public async Task The_stored_token_is_read_from_the_secret_store()
    {
        var (token, login) = WorkingToken;
        Assert.Null(await Gateway.CheckTokenAsync(TestContext.Current.CancellationToken));

        await Secrets.SetAsync(GitHubToken.SecretName, token, TestContext.Current.CancellationToken);
        Assert.Equal(new GitHubTokenCheck.Works(login), await Gateway.CheckTokenAsync(TestContext.Current.CancellationToken));

        await Secrets.SetAsync(GitHubToken.SecretName, UnknownToken, TestContext.Current.CancellationToken);
        Assert.IsType<GitHubTokenCheck.Fails>(await Gateway.CheckTokenAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A public repository on GitHub, which every token GitHub accepts can reach.</summary>
    protected static readonly RepositoryName PublicRepository = new("octocat", "Hello-World");

    [Fact]
    public async Task A_repository_the_token_can_reach_is_reachable_by_GitHubs_name_for_it()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);

        var check = await Gateway.CheckRepositoryAsync(new RepositoryName("OCTOCAT", "hello-world"), TestContext.Current.CancellationToken);

        Assert.Equal(new GitHubRepositoryCheck.Reachable(PublicRepository), check);
    }

    [Fact]
    public async Task A_repository_that_does_not_exist_is_not_found()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);

        var check = await Gateway.CheckRepositoryAsync(
            new RepositoryName("octocat", $"no-such-repository-{Guid.NewGuid():N}"), TestContext.Current.CancellationToken);

        Assert.IsType<GitHubRepositoryCheck.NotFound>(check);
    }

    [Fact]
    public async Task Without_a_stored_token_a_repository_is_not_checked()
    {
        Assert.Null(await Gateway.CheckRepositoryAsync(PublicRepository, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task With_an_unknown_stored_token_checking_a_repository_fails_with_GitHubs_error()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, UnknownToken, TestContext.Current.CancellationToken);

        var check = await Gateway.CheckRepositoryAsync(PublicRepository, TestContext.Current.CancellationToken);

        var fails = Assert.IsType<GitHubRepositoryCheck.Fails>(check);
        Assert.Contains("Bad credentials", fails.Error);
    }

    [Fact]
    public async Task A_repository_is_created_for_the_tokens_user_and_is_then_reachable()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);
        var repository = NewRepositoryName();

        var creation = await CreateAsync(repository);

        Assert.Equal(new GitHubRepositoryCreation.Created(repository), creation);
        Assert.Equal(
            new GitHubRepositoryCheck.Reachable(repository),
            await Gateway.CheckRepositoryAsync(repository, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Creating_a_repository_that_exists_fails_with_GitHubs_error()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);
        var repository = NewRepositoryName();
        Assert.IsType<GitHubRepositoryCreation.Created>(await CreateAsync(repository));

        var creation = await CreateAsync(repository);

        var fails = Assert.IsType<GitHubRepositoryCreation.Fails>(creation);
        Assert.Contains("name already exists on this account", fails.Error);
    }

    [Fact]
    public async Task Creating_a_repository_for_another_user_fails_with_GitHubs_error()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);

        // octocat is a user, not an organization, so nobody else can create repositories for it.
        var creation = await CreateAsync(new RepositoryName(PublicRepository.Owner, $"overseer-contract-{Guid.NewGuid():N}"));

        var fails = Assert.IsType<GitHubRepositoryCreation.Fails>(creation);
        Assert.Contains("Not Found", fails.Error);
    }

    [Fact]
    public async Task Without_a_stored_token_no_repository_is_created()
    {
        Assert.Null(await CreateAsync(NewRepositoryName()));
    }

    [Fact]
    public async Task With_an_unknown_stored_token_creating_a_repository_fails_with_GitHubs_error()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, UnknownToken, TestContext.Current.CancellationToken);

        var creation = await CreateAsync(NewRepositoryName());

        var fails = Assert.IsType<GitHubRepositoryCreation.Fails>(creation);
        Assert.Contains("Bad credentials", fails.Error);
    }

    [Fact]
    public async Task A_file_on_a_branch_is_found()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);

        var check = await Gateway.CheckFileAsync(PublicRepository, "master", "README", TestContext.Current.CancellationToken);

        Assert.IsType<GitHubFileCheck.Found>(check);
    }

    [Fact]
    public async Task A_file_that_is_not_on_the_branch_is_not_found()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);

        var check = await Gateway.CheckFileAsync(PublicRepository, "master", ".overseer.yml", TestContext.Current.CancellationToken);

        Assert.IsType<GitHubFileCheck.NotFound>(check);
    }

    [Fact]
    public async Task A_file_on_the_default_branch_is_found_without_naming_the_branch()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);

        // Hello-World's default branch is master.
        var check = await Gateway.CheckFileAsync(PublicRepository, branch: null, "README", TestContext.Current.CancellationToken);

        Assert.IsType<GitHubFileCheck.Found>(check);
    }

    [Fact]
    public async Task A_file_that_is_not_on_the_default_branch_is_not_found()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);

        var check = await Gateway.CheckFileAsync(PublicRepository, branch: null, ".overseer.yml", TestContext.Current.CancellationToken);

        Assert.IsType<GitHubFileCheck.NotFound>(check);
    }

    [Fact]
    public async Task In_a_new_and_empty_repository_no_file_is_found_on_its_default_branch()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);
        var repository = NewRepositoryName();
        Assert.IsType<GitHubRepositoryCreation.Created>(await CreateAsync(repository));

        var check = await Gateway.CheckFileAsync(repository, branch: null, ".overseer.yml", TestContext.Current.CancellationToken);

        Assert.IsType<GitHubFileCheck.NotFound>(check);
    }

    [Fact]
    public async Task In_a_new_and_empty_repository_no_file_is_found()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);
        var repository = NewRepositoryName();
        Assert.IsType<GitHubRepositoryCreation.Created>(await CreateAsync(repository));

        var check = await Gateway.CheckFileAsync(repository, "main", ".overseer.yml", TestContext.Current.CancellationToken);

        Assert.IsType<GitHubFileCheck.NotFound>(check);
    }

    [Fact]
    public async Task Checking_a_file_in_a_repository_the_token_cannot_reach_fails_naming_the_repository()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);
        // GitHub says "Not Found" for its files too, as for a file that isn't there.
        var repository = new RepositoryName("octocat", $"no-such-repository-{Guid.NewGuid():N}");

        var check = await Gateway.CheckFileAsync(repository, branch: null, ".overseer.yml", TestContext.Current.CancellationToken);

        var fails = Assert.IsType<GitHubFileCheck.Fails>(check);
        Assert.Contains(repository.ToString(), fails.Error);
    }

    [Fact]
    public async Task Without_a_stored_token_no_file_is_checked()
    {
        Assert.Null(await Gateway.CheckFileAsync(PublicRepository, "master", "README", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task With_an_unknown_stored_token_checking_a_file_fails_with_GitHubs_error()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, UnknownToken, TestContext.Current.CancellationToken);

        var check = await Gateway.CheckFileAsync(PublicRepository, "master", "README", TestContext.Current.CancellationToken);

        var fails = Assert.IsType<GitHubFileCheck.Fails>(check);
        Assert.Contains("Bad credentials", fails.Error);
    }

    private const string Branch = "overseer/contract";

    [Fact]
    public async Task A_pull_request_is_opened_from_the_branch_to_the_default_branch_with_its_title_and_description()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);
        var (repository, defaultBranch) = await RepositoryWithBranchAsync(Branch, ahead: true);

        var opening = await OpenPullRequestAsync(repository, Branch);

        var opened = Assert.IsType<GitHubPullRequestOpening.Opened>(opening);
        Assert.Equal($"https://github.com/{repository}/pull/{opened.Number}", opened.Url);
        Assert.Equal(
            new OpenPullRequest("Export data", "Orders can be exported as **CSV**.", Branch, defaultBranch),
            await ReadPullRequestAsync(repository, opened.Number));
    }

    [Fact]
    public async Task A_pull_request_that_is_open_for_the_branch_already_is_the_one_opened_and_stays_as_it_is()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);
        var (repository, defaultBranch) = await RepositoryWithBranchAsync(Branch, ahead: true);
        var first = Assert.IsType<GitHubPullRequestOpening.Opened>(await OpenPullRequestAsync(repository, Branch));

        var again = await Gateway.OpenPullRequestAsync(
            repository, Branch, "Another title", "Another description.", TestContext.Current.CancellationToken);

        Assert.Equal(first, again);
        Assert.Equal(
            new OpenPullRequest("Export data", "Orders can be exported as **CSV**.", Branch, defaultBranch),
            await ReadPullRequestAsync(repository, first.Number));
    }

    [Fact]
    public async Task Opening_a_pull_request_for_a_branch_with_nothing_the_default_branch_lacks_fails_with_GitHubs_error()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);
        var (repository, _) = await RepositoryWithBranchAsync(Branch, ahead: false);

        var opening = await OpenPullRequestAsync(repository, Branch);

        var fails = Assert.IsType<GitHubPullRequestOpening.Fails>(opening);
        Assert.Contains("No commits between", fails.Error);
    }

    [Fact]
    public async Task Opening_a_pull_request_for_a_branch_that_does_not_exist_fails_with_GitHubs_error()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);
        var (repository, _) = await RepositoryWithBranchAsync(Branch, ahead: true);

        var opening = await OpenPullRequestAsync(repository, "no-such-branch");

        var fails = Assert.IsType<GitHubPullRequestOpening.Fails>(opening);
        Assert.Contains("head", fails.Error);
        Assert.Contains("422", fails.Error);
    }

    [Fact]
    public async Task Opening_a_pull_request_in_a_repository_the_token_cannot_reach_fails_naming_the_repository()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);
        var repository = new RepositoryName("octocat", $"no-such-repository-{Guid.NewGuid():N}");

        var opening = await OpenPullRequestAsync(repository, Branch);

        var fails = Assert.IsType<GitHubPullRequestOpening.Fails>(opening);
        Assert.Contains(repository.ToString(), fails.Error);
    }

    [Fact]
    public async Task Without_a_stored_token_no_pull_request_is_opened()
    {
        Assert.Null(await OpenPullRequestAsync(PublicRepository, Branch));
    }

    [Fact]
    public async Task With_an_unknown_stored_token_opening_a_pull_request_fails_with_GitHubs_error()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, UnknownToken, TestContext.Current.CancellationToken);

        var opening = await OpenPullRequestAsync(PublicRepository, Branch);

        var fails = Assert.IsType<GitHubPullRequestOpening.Fails>(opening);
        Assert.Contains("Bad credentials", fails.Error);
    }

    private Task<GitHubPullRequestOpening?> OpenPullRequestAsync(RepositoryName repository, string branch) =>
        Gateway.OpenPullRequestAsync(
            repository, branch, "Export data", "Orders can be exported as **CSV**.", TestContext.Current.CancellationToken);

    /// <summary>
    /// A new repository of the working token's user, to be deleted after the test, with a commit on its default branch and the
    /// branch <paramref name="branch"/> from it, which has a commit of its own when <paramref name="ahead"/>.
    /// </summary>
    protected abstract Task<(RepositoryName Repository, string DefaultBranch)> RepositoryWithBranchAsync(string branch, bool ahead);

    /// <summary>The pull request as GitHub has it.</summary>
    protected abstract Task<OpenPullRequest> ReadPullRequestAsync(RepositoryName repository, int number);

    /// <summary>A pull request as GitHub has it.</summary>
    /// <param name="Head">The branch it comes from.</param>
    /// <param name="Base">The branch it goes to.</param>
    protected sealed record OpenPullRequest(string Title, string Body, string Head, string Base);

    /// <summary>A repository of the working token's user that doesn't exist yet.</summary>
    private RepositoryName NewRepositoryName() => new(WorkingToken.Login, $"overseer-contract-{Guid.NewGuid():N}");

    /// <summary>A repository of the working token's user, created now and deleted after the test.</summary>
    protected async Task<RepositoryName> CreatedRepositoryAsync()
    {
        var repository = NewRepositoryName();
        Assert.IsType<GitHubRepositoryCreation.Created>(await CreateAsync(repository));
        return repository;
    }

    /// <summary>Creates <paramref name="repository"/>, to be deleted after the test if GitHub created it.</summary>
    private async Task<GitHubRepositoryCreation?> CreateAsync(RepositoryName repository)
    {
        var creation = await Gateway.CreateRepositoryAsync(repository, TestContext.Current.CancellationToken);
        if (creation is GitHubRepositoryCreation.Created created)
        {
            _created.Add(created.Repository);
        }
        return creation;
    }

    /// <summary>Deletes a repository a test created, so none are left behind on GitHub.</summary>
    protected virtual Task DeleteRepositoryAsync(RepositoryName repository) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var repository in _created)
        {
            await DeleteRepositoryAsync(repository);
        }
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
        GC.SuppressFinalize(this);
    }
}

public sealed class FakeGitHubGatewayContract : GitHubGatewayContract
{
    private readonly FakeGitHubGateway _gateway;

    public FakeGitHubGatewayContract()
    {
        _gateway = new FakeGitHubGateway(Secrets);
        _gateway.AcceptToken(WorkingToken.Token, WorkingToken.Login);
        _gateway.AddRepository(PublicRepository.ToString(), defaultBranch: "master");
        _gateway.AddFile(PublicRepository.ToString(), "master", "README");
    }

    protected override IGitHubGateway Gateway => _gateway;

    protected override (string Token, string Login) WorkingToken => ("github_pat_11WORKING", "monalisa");

    protected override async Task<(RepositoryName Repository, string DefaultBranch)> RepositoryWithBranchAsync(string branch, bool ahead)
    {
        var repository = await CreatedRepositoryAsync();
        _gateway.AddBranch(repository.ToString(), branch, ahead);
        return (repository, FakeGitHubGateway.DefaultBranch);
    }

    protected override Task<OpenPullRequest> ReadPullRequestAsync(RepositoryName repository, int number)
    {
        var found = Assert.Single(_gateway.PullRequests, open => open.Repository == repository && open.Number == number);
        return Task.FromResult(new OpenPullRequest(found.Title, found.Body, found.Head, found.Base));
    }
}

/// <summary>
/// Opt-in: runs against GitHub when <c>TEST_GITHUB_TOKEN</c> holds a token GitHub accepts, and <c>TEST_GITHUB_LOGIN</c>
/// the login it works as. Creating repositories needs a fine-grained token with "Administration: Read and write" on all
/// of the user's repositories; the tests create private repositories named <c>overseer-contract-...</c> and delete
/// them again, with the same permission. Checking for files and opening pull requests need "Contents: Read and write" and
/// "Pull requests: Read and write" as well: the tests commit and branch in the repositories they create.
/// </summary>
public sealed class GitHubGatewayContractAgainstGitHub : GitHubGatewayContract
{
    private const string TokenVariable = "TEST_GITHUB_TOKEN";
    private const string LoginVariable = "TEST_GITHUB_LOGIN";

    private readonly GitHubGateway _gateway;

    public GitHubGatewayContractAgainstGitHub()
    {
        var token = Environment.GetEnvironmentVariable(TokenVariable);
        var login = Environment.GetEnvironmentVariable(LoginVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(token) || string.IsNullOrEmpty(login),
            $"Set {TokenVariable} to a GitHub token and {LoginVariable} to its login to run the GitHub Gateway contract against GitHub.");
        WorkingToken = (token!, login!);
        _gateway = new GitHubGateway(Secrets);
    }

    protected override IGitHubGateway Gateway => _gateway;

    protected override (string Token, string Login) WorkingToken { get; }

    protected override async Task<(RepositoryName Repository, string DefaultBranch)> RepositoryWithBranchAsync(string branch, bool ahead)
    {
        var repository = await CreatedRepositoryAsync();
        // The first commit of an empty repository makes its default branch.
        await CommitAsync(repository, "README.md", branch: null);
        var defaultBranch = (await SendAsync(HttpMethod.Get, PathOf(repository)))["default_branch"]!.GetValue<string>();
        var head = (await SendAsync(HttpMethod.Get, $"{PathOf(repository)}/git/ref/heads/{defaultBranch}"))["object"]!["sha"]!
            .GetValue<string>();
        await SendAsync(HttpMethod.Post, $"{PathOf(repository)}/git/refs", new { @ref = $"refs/heads/{branch}", sha = head });
        if (ahead)
        {
            await CommitAsync(repository, "change.txt", branch);
        }
        return (repository, defaultBranch);
    }

    protected override async Task<OpenPullRequest> ReadPullRequestAsync(RepositoryName repository, int number)
    {
        var found = await SendAsync(HttpMethod.Get, $"{PathOf(repository)}/pulls/{number}");
        return new OpenPullRequest(
            found["title"]!.GetValue<string>(), found["body"]!.GetValue<string>(),
            found["head"]!["ref"]!.GetValue<string>(), found["base"]!["ref"]!.GetValue<string>());
    }

    /// <summary>Commits a new file, to the default branch when <paramref name="branch"/> is <c>null</c>.</summary>
    private Task<JsonNode> CommitAsync(RepositoryName repository, string path, string? branch)
    {
        var content = new Dictionary<string, string>
        {
            ["message"] = $"Add {path}",
            ["content"] = Convert.ToBase64String("contract"u8),
        };
        if (branch is not null)
        {
            content["branch"] = branch;
        }
        return SendAsync(HttpMethod.Put, $"{PathOf(repository)}/contents/{path}", content);
    }

    private static string PathOf(RepositoryName repository) =>
        $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}";

    /// <summary>Sends the test's own request to GitHub, which must succeed, and gives its answer.</summary>
    private async Task<JsonNode> SendAsync(HttpMethod method, string path, object? content = null)
    {
        using var response = await RequestAsync(method, path, content);
        var answer = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path} failed ({(int)response.StatusCode}): {answer}");
        return JsonNode.Parse(answer)!;
    }

    private async Task<HttpResponseMessage> RequestAsync(HttpMethod method, string path, object? content = null)
    {
        using var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        using var request = new HttpRequestMessage(method, path)
        {
            Headers =
            {
                Authorization = new AuthenticationHeaderValue("Bearer", WorkingToken.Token),
                UserAgent = { new ProductInfoHeaderValue("overseer-tests", "1") },
            },
            Content = content is null ? null : JsonContent.Create(content),
        };
        return await http.SendAsync(request, CancellationToken.None);
    }

    protected override async Task DeleteRepositoryAsync(RepositoryName repository)
    {
        using var response = await RequestAsync(HttpMethod.Delete, PathOf(repository));
        Assert.True(
            response.IsSuccessStatusCode,
            $"Delete {repository.WebUrl} by hand: GitHub did not delete it ({(int)response.StatusCode}).");
    }
}
