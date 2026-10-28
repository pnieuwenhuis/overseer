using System.Net;
using Overseer.Cli;

namespace Overseer.Tests;

/// <summary>A local <c>overseer reset-password</c> followed by a restart of <c>overseer serve</c>, as a developer would do it.</summary>
public class ResetPasswordTests(PostgresServer postgres) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("overseer-reset-").FullName;

    private Dictionary<string, string?> CliEnvironment => new()
    {
        ["OVERSEER_CONFIG_FILE"] = Path.Combine(_directory, "config.json"),
    };

    /// <summary>Only the config file holds the hash, as when serve runs locally.</summary>
    private Dictionary<string, string?> ServeEnvironment => new(CliEnvironment)
    {
        ["OVERSEER_USER__PASSWORDHASH"] = null,
    };

    [Fact]
    public async Task After_a_reset_and_a_restart_the_new_password_works_and_the_old_one_is_rejected()
    {
        var database = await postgres.CreateEmptyDatabaseAsync();
        var old = await ResetPasswordAsync();
        await using (var overseer = await RunningOverseer.StartAsync(database, ServeEnvironment))
        {
            await overseer.LogInAsync(old.Username, old.Password);
            Assert.True(await overseer.IsLoggedInAsync());
        }

        var reset = await ResetPasswordAsync();

        await using (var restarted = await RunningOverseer.StartAsync(database, ServeEnvironment))
        {
            await restarted.LogInAsync(old.Username, old.Password);
            Assert.False(await restarted.IsLoggedInAsync());

            await restarted.LogInAsync(reset.Username, reset.Password);
            Assert.True(await restarted.IsLoggedInAsync());
        }
    }

    [Fact]
    public async Task A_reset_ends_the_sessions_logged_in_with_the_old_password()
    {
        var database = await postgres.CreateEmptyDatabaseAsync();
        var old = await ResetPasswordAsync();
        CookieCollection session;
        await using (var overseer = await RunningOverseer.StartAsync(database, ServeEnvironment))
        {
            await overseer.LogInAsync(old.Username, old.Password);
            session = overseer.Cookies.GetAllCookies();
        }

        await ResetPasswordAsync();

        await using var restarted = await RunningOverseer.StartAsync(database, ServeEnvironment);
        restarted.Cookies.Add(session);
        Assert.False(await restarted.IsLoggedInAsync());
    }

    [Fact]
    public async Task A_restart_without_a_reset_keeps_you_logged_in()
    {
        var database = await postgres.CreateEmptyDatabaseAsync();
        var credentials = await ResetPasswordAsync();
        CookieCollection session;
        await using (var overseer = await RunningOverseer.StartAsync(database, ServeEnvironment))
        {
            await overseer.LogInAsync(credentials.Username, credentials.Password);
            session = overseer.Cookies.GetAllCookies();
        }

        await using var restarted = await RunningOverseer.StartAsync(database, ServeEnvironment);
        restarted.Cookies.Add(session);
        Assert.True(await restarted.IsLoggedInAsync());
    }

    private async Task<ResetPasswordOutput> ResetPasswordAsync()
    {
        var output = new StringWriter();
        Assert.Equal(0, await OverseerCli.InvokeAsync(["reset-password"], CliEnvironment, output, output));
        return ResetPasswordOutput.Parse(output.ToString());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
