using System.Net;

namespace Overseer.Tests;

public class LoginTests(PostgresServer postgres)
{
    [Theory]
    [InlineData("/")]
    [InlineData("/not-a-page")]
    public async Task Without_logging_in_the_Portal_sends_you_to_the_login_page(string path)
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());

        using var response = await overseer.Http.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/login", response.RequestMessage!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Logging_in_with_the_configured_username_and_password_opens_the_Portal()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());

        using var response = await overseer.LogInAsync(RunningOverseer.Username, RunningOverseer.Password);

        Assert.Equal("/", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.True(await overseer.IsLoggedInAsync());
    }

    [Theory]
    [InlineData(RunningOverseer.Username, "wrong password")]
    [InlineData("someone-else", RunningOverseer.Password)]
    [InlineData(RunningOverseer.Username, "")]
    public async Task Logging_in_with_a_wrong_username_or_password_fails(string username, string password)
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());

        using var response = await overseer.LogInAsync(username, password);

        Assert.Contains("The username or password is incorrect.", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(await overseer.IsLoggedInAsync());
    }

    [Fact]
    public async Task After_logging_in_you_return_to_the_page_you_asked_for()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        using var redirected = await overseer.Http.GetAsync("/?asked=for", TestContext.Current.CancellationToken);
        var loginPage = redirected.RequestMessage!.RequestUri!.PathAndQuery;

        using var response = await overseer.LogInAsync(loginPage: loginPage);

        Assert.Equal("/?asked=for", response.RequestMessage!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Logging_in_never_sends_you_to_another_site()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());

        using var response = await overseer.LogInAsync(loginPage: "/login?ReturnUrl=%2F%2Fexample.com%2F");

        Assert.Equal(overseer.Http.BaseAddress!.Authority, response.RequestMessage!.RequestUri!.Authority);
        Assert.Equal("/", response.RequestMessage.RequestUri.AbsolutePath);
    }

    // ADR 0005 says PostgreSQL holds no credential. No application seam can show what a database does not
    // hold, so this one test reads every table directly.
    [Fact]
    public async Task The_database_holds_no_credential()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        var everything = string.Join("\n", await overseer.DumpDatabaseAsync());

        Assert.Contains("<MigrationId>", everything);
        Assert.DoesNotContain(RunningOverseer.Password, everything);
        Assert.DoesNotContain(RunningOverseer.PasswordHash, everything);
    }
}
