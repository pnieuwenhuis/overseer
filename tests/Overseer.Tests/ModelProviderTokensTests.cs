using System.Net;
using System.Text.RegularExpressions;
using Overseer.Sandboxes;

namespace Overseer.Tests;

/// <summary>Model provider tokens, as stored on the Settings page and handed to agent sessions as environment variables.</summary>
public class ModelProviderTokensTests(PostgresServer postgres)
{
    private const string FoundryToken = "foundry-key-0123456789abcdef";
    private const string GatewayToken = "sk-gateway-0123456789";

    [Fact]
    public async Task An_added_token_is_listed_by_name_with_its_environment_variable_never_its_value()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await SaveAsync(overseer, "azure-foundry", FoundryToken);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The provider token azure-foundry is added.", page);
        Assert.Equal([("azure-foundry", "AZURE_FOUNDRY_API_KEY")], Listed(page));
        Assert.DoesNotContain(FoundryToken, page);
        var reloaded = await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken);
        Assert.Equal([("azure-foundry", "AZURE_FOUNDRY_API_KEY")], Listed(reloaded));
        Assert.DoesNotContain(FoundryToken, reloaded);
    }

    [Fact]
    public async Task Every_agent_session_receives_every_stored_token_in_its_environment_variable()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        var before = await overseer.StartAgentSessionAsync(AgentRole.Developer);
        (await SaveAsync(overseer, "azure-foundry", FoundryToken)).Dispose();
        (await SaveAsync(overseer, "gateway", GatewayToken)).Dispose();

        var analyst = await overseer.StartAgentSessionAsync(AgentRole.Analyst);
        var reviewer = await overseer.StartAgentSessionAsync(AgentRole.Reviewer);

        var expected = WithWebSearch(("AZURE_FOUNDRY_API_KEY", FoundryToken), ("GATEWAY_API_KEY", GatewayToken));
        Assert.Equal(WithWebSearch(), before.Environment);
        Assert.Equal(expected, analyst.Environment);
        Assert.Equal(expected, reviewer.Environment);
    }

    [Fact]
    public async Task Saving_under_a_stored_name_replaces_that_token()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        (await SaveAsync(overseer, "gateway", "sk-gateway-old")).Dispose();

        using var response = await SaveAsync(overseer, " Gateway ", GatewayToken);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The provider token gateway is replaced.", page);
        Assert.Equal([("gateway", "GATEWAY_API_KEY")], Listed(page));
        var session = await overseer.StartAgentSessionAsync(AgentRole.Developer);
        Assert.Equal(WithWebSearch(("GATEWAY_API_KEY", GatewayToken)), session.Environment);
    }

    [Fact]
    public async Task A_removed_token_is_no_longer_listed_nor_passed_to_agent_sessions()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        (await SaveAsync(overseer, "azure-foundry", FoundryToken)).Dispose();
        (await SaveAsync(overseer, "gateway", GatewayToken)).Dispose();

        using var response = await RemoveAsync(overseer, "azure-foundry");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The provider token azure-foundry is removed.", page);
        Assert.Equal([("gateway", "GATEWAY_API_KEY")], Listed(page));
        var session = await overseer.StartAgentSessionAsync(AgentRole.Analyst);
        Assert.Equal(WithWebSearch(("GATEWAY_API_KEY", GatewayToken)), session.Environment);
    }

    [Fact]
    public async Task Removing_the_last_token_leaves_none_stored()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        (await SaveAsync(overseer, "gateway", GatewayToken)).Dispose();

        using var response = await RemoveAsync(overseer, "gateway");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("No provider tokens are stored.", page);
        Assert.Empty(Listed(page));
        Assert.Equal(WithWebSearch(), (await overseer.StartAgentSessionAsync(AgentRole.Analyst)).Environment);
    }

    [Theory]
    [InlineData("azure foundry", FoundryToken, "A token's name is 1 to 64 letters, digits and dashes, starting with a letter")]
    [InlineData("azure_foundry", FoundryToken, "A token's name is 1 to 64 letters, digits and dashes, starting with a letter")]
    [InlineData("1st-gateway", FoundryToken, "A token's name is 1 to 64 letters, digits and dashes, starting with a letter")]
    [InlineData(" ", FoundryToken, "Enter a name for the token")]
    [InlineData("azure-foundry", "foundry key", "This is not a token")]
    [InlineData("azure-foundry", "foundry-key\nGATEWAY_API_KEY=x", "This is not a token")]
    [InlineData("azure-foundry", " ", "Enter the token.")]
    public async Task A_name_or_token_that_cannot_be_used_is_refused_and_nothing_is_stored(string name, string token, string error)
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await SaveAsync(overseer, name, token);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(error, WebUtility.HtmlDecode(page));
        Assert.DoesNotContain("The provider token", page);
        Assert.Contains("No provider tokens are stored.", page);
        Assert.Contains($"name=\"ProviderTokenName\" value=\"{WebUtility.HtmlEncode(name)}\"", page);
        Assert.Equal(WithWebSearch(), (await overseer.StartAgentSessionAsync(AgentRole.Analyst)).Environment);
    }

    [Fact]
    public async Task A_configuration_referencing_a_variable_no_token_provides_is_saved_with_a_warning_naming_it()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        (await SaveAsync(overseer, "gateway", GatewayToken)).Dispose();

        using var response = await SaveHarnessAsync(overseer, TwoProviders);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The agent harness configuration is saved.", page);
        Assert.Equal(
            "The configuration references the environment variable AZURE_FOUNDRY_API_KEY, which no provider token provides.",
            Warning(page));
        Assert.Equal(TwoProviders, (await overseer.StartAgentSessionAsync(AgentRole.Analyst)).HarnessJson);
    }

    [Fact]
    public async Task No_warning_shows_while_every_referenced_variable_has_a_token()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        (await SaveAsync(overseer, "gateway", GatewayToken)).Dispose();
        (await SaveAsync(overseer, "azure-foundry", FoundryToken)).Dispose();

        using var response = await SaveHarnessAsync(overseer, TwoProviders);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The agent harness configuration is saved.", page);
        Assert.Null(Warning(page));
    }

    [Fact]
    public async Task Removing_a_token_the_configuration_references_brings_the_warning_naming_every_missing_variable()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        (await SaveAsync(overseer, "gateway", GatewayToken)).Dispose();
        (await SaveHarnessAsync(overseer, TwoProviders)).Dispose();

        using var response = await RemoveAsync(overseer, "gateway");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            "The configuration references the environment variables AZURE_FOUNDRY_API_KEY and GATEWAY_API_KEY, which no provider token provides.",
            Warning(page));
    }

    /// <summary>An <c>opencode.json</c> with two providers, each with its key in an environment variable.</summary>
    private const string TwoProviders = """
        {
          "$schema": "https://opencode.ai/config.json",
          "provider": {
            "gateway": {
              "npm": "@ai-sdk/openai-compatible",
              "options": { "baseURL": "https://gateway.example.com/v1", "apiKey": "{env:GATEWAY_API_KEY}" }
            },
            "azure": {
              "options": { "resourceName": "overseer-foundry", "apiKey": "{env:AZURE_FOUNDRY_API_KEY}" }
            }
          },
          "agent": {
            "analyst": { "model": "azure/claude-opus-5-5" },
            "designer": { "model": "gateway/claude-sonnet-5-5" },
            "developer": { "model": "gateway/claude-sonnet-5-5" },
            "reviewer": { "model": "azure/claude-opus-5-5" }
          }
        }

        """;

    /// <summary>Writes <paramref name="json"/> in the Settings page's <c>opencode.json</c> editor and saves it.</summary>
    private static Task<HttpResponseMessage> SaveHarnessAsync(RunningOverseer overseer, string json) =>
        overseer.SubmitFormAsync("/settings", "agent-harness", new Dictionary<string, string> { ["AgentHarness"] = json });

    /// <summary>The warning the Settings page shows about the agent harness configuration; <c>null</c> when it shows none.</summary>
    private static string? Warning(string page) =>
        Regex.Match(page, "<p[^>]*class=\"settings-warning\"[^>]*>(.*?)</p>", RegexOptions.Singleline) is { Success: true } warning
            ? Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(warning.Groups[1].Value, "<[^>]+>", "")), @"\s+", " ").Trim()
            : null;

    /// <summary>Presses the Remove button of the token listed under <paramref name="name"/>.</summary>
    private static Task<HttpResponseMessage> RemoveAsync(RunningOverseer overseer, string name) =>
        overseer.SubmitFormAsync("/settings", "provider-token-remove", new Dictionary<string, string> { ["RemoveProviderToken"] = name });

    /// <summary>Enters a name and a token in the Settings page's provider token form and saves them.</summary>
    private static Task<HttpResponseMessage> SaveAsync(RunningOverseer overseer, string name, string token) =>
        overseer.SubmitFormAsync("/settings", "provider-token", new Dictionary<string, string>
        {
            ["ProviderTokenName"] = name,
            ["ProviderToken"] = token,
        });

    /// <summary>The provider tokens the Settings page lists: each one's name and environment variable.</summary>
    private static List<(string Name, string Variable)> Listed(string page)
    {
        var list = Regex.Match(page, "<table[^>]*class=\"provider-tokens\"[^>]*>(.*?)</table>", RegexOptions.Singleline);
        return list.Success
            ? [.. Regex.Matches(list.Groups[1].Value, @"<tr[^>]*>\s*<td[^>]*>(.*?)</td>\s*<td[^>]*><code[^>]*>(.*?)</code></td>", RegexOptions.Singleline)
                .Select(row => (WebUtility.HtmlDecode(row.Groups[1].Value.Trim()), WebUtility.HtmlDecode(row.Groups[2].Value)))]
            : [];
    }

    /// <summary>An agent session's environment with these tokens: they come with the switch that gives agents web search.</summary>
    private static Dictionary<string, string> WithWebSearch(params (string Variable, string Token)[] tokens) =>
        new([new("OPENCODE_ENABLE_EXA", "1"), .. tokens.Select(token => new KeyValuePair<string, string>(token.Variable, token.Token))]);
}
