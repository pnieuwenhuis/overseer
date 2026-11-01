using System.Net.Http.Headers;
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
    public async Task In_a_new_and_empty_repository_no_file_is_found()
    {
        await Secrets.SetAsync(GitHubToken.SecretName, WorkingToken.Token, TestContext.Current.CancellationToken);
        var repository = NewRepositoryName();
        Assert.IsType<GitHubRepositoryCreation.Created>(await CreateAsync(repository));

        var check = await Gateway.CheckFileAsync(repository, "main", ".overseer.yml", TestContext.Current.CancellationToken);

        Assert.IsType<GitHubFileCheck.NotFound>(check);
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

    /// <summary>A repository of the working token's user that doesn't exist yet.</summary>
    private RepositoryName NewRepositoryName() => new(WorkingToken.Login, $"overseer-contract-{Guid.NewGuid():N}");

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
        _gateway.AddRepository(PublicRepository.ToString());
        _gateway.AddFile(PublicRepository.ToString(), "master", "README");
    }

    protected override IGitHubGateway Gateway => _gateway;

    protected override (string Token, string Login) WorkingToken => ("github_pat_11WORKING", "monalisa");
}

/// <summary>
/// Opt-in: runs against GitHub when <c>TEST_GITHUB_TOKEN</c> holds a token GitHub accepts, and <c>TEST_GITHUB_LOGIN</c>
/// the login it works as. Creating repositories needs a fine-grained token with "Administration: Read and write" on all
/// of the user's repositories; the tests create private repositories named <c>overseer-contract-...</c> and delete
/// them again, with the same permission. Checking for files needs "Contents: Read" as well.
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

    protected override async Task DeleteRepositoryAsync(RepositoryName repository)
    {
        using var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}")
        {
            Headers =
            {
                Authorization = new AuthenticationHeaderValue("Bearer", WorkingToken.Token),
                UserAgent = { new ProductInfoHeaderValue("overseer-tests", "1") },
            },
        };
        using var response = await http.SendAsync(request, CancellationToken.None);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Delete {repository.WebUrl} by hand: GitHub did not delete it ({(int)response.StatusCode}).");
    }
}
