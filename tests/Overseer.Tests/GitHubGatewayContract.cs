using Overseer.GitHub;
using Overseer.Secrets;

namespace Overseer.Tests;

/// <summary>What every GitHub Gateway must do. Derive from this once per implementation.</summary>
public abstract class GitHubGatewayContract : IDisposable
{
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

    public void Dispose()
    {
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
    }

    protected override IGitHubGateway Gateway => _gateway;

    protected override (string Token, string Login) WorkingToken => ("github_pat_11WORKING", "octocat");
}

/// <summary>
/// Opt-in: runs against GitHub when <c>TEST_GITHUB_TOKEN</c> holds a token GitHub accepts, and <c>TEST_GITHUB_LOGIN</c>
/// the login it works as.
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
}
