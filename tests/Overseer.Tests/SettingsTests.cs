using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Overseer.Settings;

namespace Overseer.Tests;

public class SettingsTests(PostgresServer postgres)
{
    [Fact]
    public async Task The_Portal_navigation_links_to_the_Settings_page()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        var home = await overseer.Http.GetStringAsync("/", TestContext.Current.CancellationToken);

        var link = Regex.Match(home, "<nav[^>]*>.*?<a[^>]*href=\"([^\"]*)\"[^>]*>Settings</a>.*?</nav>", RegexOptions.Singleline);
        Assert.True(link.Success, "The navigation has no Settings link.");
        using var response = await overseer.Http.GetAsync(link.Groups[1].Value, TestContext.Current.CancellationToken);
        Assert.Equal("/settings", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Matches("<h1[^>]*>Settings</h1>", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_saved_values_the_operating_limits_are_their_defaults()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());

        var limits = await overseer.Services.GetRequiredService<SettingsStore>().GetOperatingLimitsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, limits.MaxActiveIntents);
        Assert.Equal(TimeSpan.FromMinutes(30), limits.RefinementIdleTimeout);
        Assert.Equal(3, limits.VerificationAttemptsPerTask);
        Assert.Equal(TimeSpan.FromHours(12), limits.MaxSandboxLifetime);
    }

    [Fact]
    public async Task The_Settings_page_shows_each_operating_limit_with_its_default()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        var page = await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken);

        Assert.Contains("Maximum Active Intents", page);
        Assert.Contains("Refinement idle timeout", page);
        Assert.Contains("Verification attempts per Task", page);
        Assert.Contains("Maximum Sandbox lifetime", page);
        Assert.Equal(Defaults, OperatingLimitInputs(page));
    }

    [Fact]
    public async Task Saved_operating_limits_are_kept_across_a_restart()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var first = await RunningOverseer.StartAsync(connectionString))
        {
            await first.LogInAsync();

            using var response = await SaveOperatingLimitsAsync(first, Changed);

            var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("The operating limits are saved.", page);
            Assert.Equal(Changed, OperatingLimitInputs(page));
        }

        await using var second = await RunningOverseer.StartAsync(connectionString);
        await second.LogInAsync();

        Assert.Equal(Changed, OperatingLimitInputs(await second.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Server_side_code_reads_a_saved_change_without_a_restart()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        var settings = overseer.Services.GetRequiredService<SettingsStore>();
        await settings.GetOperatingLimitsAsync(TestContext.Current.CancellationToken);

        using var response = await SaveOperatingLimitsAsync(overseer, Changed);

        Assert.Equal(
            new OperatingLimits(
                MaxActiveIntents: 5,
                RefinementIdleTimeout: TimeSpan.FromMinutes(45),
                VerificationAttemptsPerTask: 4,
                MaxSandboxLifetime: TimeSpan.FromHours(24)),
            await settings.GetOperatingLimitsAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("MaxActiveIntents", "0", "Maximum Active Intents must be a whole number from 1 to 10.")]
    [InlineData("MaxActiveIntents", "11", "Maximum Active Intents must be a whole number from 1 to 10.")]
    [InlineData("RefinementIdleTimeout", "-30", "Refinement idle timeout must be a whole number from 1 to 480.")]
    [InlineData("RefinementIdleTimeout", "481", "Refinement idle timeout must be a whole number from 1 to 480.")]
    [InlineData("VerificationAttemptsPerTask", "three", "Verification attempts per Task must be a whole number from 1 to 10.")]
    [InlineData("VerificationAttemptsPerTask", "2.5", "Verification attempts per Task must be a whole number from 1 to 10.")]
    [InlineData("MaxSandboxLifetime", "", "Maximum Sandbox lifetime must be a whole number from 1 to 48.")]
    [InlineData("MaxSandboxLifetime", "99999999999", "Maximum Sandbox lifetime must be a whole number from 1 to 48.")]
    public async Task An_invalid_operating_limit_is_rejected_and_nothing_is_saved(string field, string value, string message)
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await SaveOperatingLimitsAsync(overseer, new Dictionary<string, string>(Changed) { [field] = value });

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(message, page);
        Assert.DoesNotContain("The operating limits are saved.", page);
        Assert.Equal(new Dictionary<string, string>(Changed) { [field] = value }, OperatingLimitInputs(page));
        Assert.Equal(Defaults, OperatingLimitInputs(await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken)));
        Assert.Equal(
            new OperatingLimits(
                MaxActiveIntents: 2,
                RefinementIdleTimeout: TimeSpan.FromMinutes(30),
                VerificationAttemptsPerTask: 3,
                MaxSandboxLifetime: TimeSpan.FromHours(12)),
            await overseer.Services.GetRequiredService<SettingsStore>().GetOperatingLimitsAsync(TestContext.Current.CancellationToken));
    }

    private static readonly Dictionary<string, string> Changed = new()
    {
        ["MaxActiveIntents"] = "5",
        ["RefinementIdleTimeout"] = "45",
        ["VerificationAttemptsPerTask"] = "4",
        ["MaxSandboxLifetime"] = "24",
    };

    /// <summary>Fills in the operating limits on the Settings page and saves them.</summary>
    private static Task<HttpResponseMessage> SaveOperatingLimitsAsync(RunningOverseer overseer, IReadOnlyDictionary<string, string> values) =>
        overseer.SubmitFormAsync("/settings", "operating-limits", values);

    private static readonly Dictionary<string, string> Defaults = new()
    {
        ["MaxActiveIntents"] = "2",
        ["RefinementIdleTimeout"] = "30",
        ["VerificationAttemptsPerTask"] = "3",
        ["MaxSandboxLifetime"] = "12",
    };

    /// <summary>The value of each operating limit's input on the Settings page.</summary>
    private static Dictionary<string, string> OperatingLimitInputs(string page) =>
        Defaults.Keys.ToDictionary(
            name => name,
            name => Regex.Match(page, $"<input[^>]*name=\"{name}\"[^>]*>") is { Success: true } input
                ? Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value
                : throw new InvalidOperationException($"The page has no input named {name}."));
}
