using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace Overseer.Tests;

public class LoginTests(PostgresServer postgres)
{
    [Theory]
    [InlineData("/")]
    [InlineData("/settings")]
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

    [Fact]
    public async Task Logging_out_returns_to_the_login_page_and_the_Portal_needs_logging_in_again()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await overseer.LogOutAsync();

        Assert.Equal("/login", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.False(await overseer.IsLoggedInAsync());
    }

    [Fact]
    public async Task Another_site_cannot_log_you_out()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await overseer.Http.PostAsync("/logout", new FormUrlEncodedContent([]), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await overseer.IsLoggedInAsync());
    }

    private static async Task FailLoginsAsync(
        RunningOverseer overseer, int times, string username = RunningOverseer.Username, string password = "wrong password")
    {
        for (var attempt = 0; attempt < times; attempt++)
        {
            (await overseer.LogInAsync(username, password)).Dispose();
        }
    }

    // 12:00 UTC, so the time the lockout ends is known.
    private static FakeTimeProvider Noon() => new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(RunningOverseer.Username, "wrong password")]
    [InlineData("someone-else", RunningOverseer.Password)]
    public async Task After_5_failed_logins_even_the_correct_password_is_refused_for_15_minutes(string username, string password)
    {
        var time = Noon();
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync(), time: time);
        await FailLoginsAsync(overseer, 5, username, password);
        time.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(59));

        using var response = await overseer.LogInAsync();

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The account is temporarily locked after too many failed logins. Try again after 12:15 UTC.", page);
        Assert.DoesNotContain("incorrect", page);
        Assert.False(await overseer.IsLoggedInAsync());
    }

    [Fact]
    public async Task The_lockout_ends_by_itself_after_15_minutes()
    {
        var time = Noon();
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync(), time: time);
        await FailLoginsAsync(overseer, 5);
        time.Advance(TimeSpan.FromMinutes(15));

        (await overseer.LogInAsync()).Dispose();

        Assert.True(await overseer.IsLoggedInAsync());
    }

    [Fact]
    public async Task Logging_in_resets_the_count_of_failed_logins()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync(), time: Noon());
        await FailLoginsAsync(overseer, 4);
        (await overseer.LogInAsync()).Dispose();
        (await overseer.LogOutAsync()).Dispose();
        await FailLoginsAsync(overseer, 4);

        (await overseer.LogInAsync()).Dispose();

        Assert.True(await overseer.IsLoggedInAsync());
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
