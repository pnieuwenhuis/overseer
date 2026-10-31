using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Overseer.Sandboxes;

namespace Overseer.Tests;

/// <summary>The agent harness configuration, an <c>opencode.json</c>, as edited on the Settings page and handed to agent sessions.</summary>
public class AgentHarnessConfigurationTests(PostgresServer postgres)
{
    [Fact]
    public async Task Before_the_first_save_the_Settings_page_shows_the_default_which_sets_a_model_for_every_role()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        var page = await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken);

        var configuration = JsonNode.Parse(Editor(page))!;
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var model = configuration["agent"]![role.ToString().ToLowerInvariant()]!["model"]!.GetValue<string>();
            Assert.Matches("^[a-z-]+/[a-z0-9.-]+$", model);
            Assert.Equal(model, Picker(page, role));
        }
    }

    [Fact]
    public async Task Before_the_first_save_an_agent_session_receives_the_default_with_its_roles_model()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        var page = await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken);

        var received = await overseer.StartAgentSessionAsync(AgentRole.Reviewer);

        Assert.Equal((AgentRole.Reviewer, Editor(page), Picker(page, AgentRole.Reviewer)), (received.Role, received.HarnessJson, received.Model));
    }

    [Fact]
    public async Task A_saved_configuration_is_shown_in_the_pickers_and_kept_across_a_restart()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var first = await RunningOverseer.StartAsync(connectionString))
        {
            await first.LogInAsync();

            using var response = await SaveAsync(first, Gateway);

            var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("The agent harness configuration is saved.", page);
            Assert.Equal(Gateway, Editor(page));
            Assert.Equal(GatewayModels, Pickers(page));
        }

        await using var second = await RunningOverseer.StartAsync(connectionString);
        await second.LogInAsync();

        var settings = await second.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken);
        Assert.Equal(Gateway, Editor(settings));
        Assert.Equal(GatewayModels, Pickers(settings));
    }

    [Fact]
    public async Task Each_agent_session_receives_the_configuration_current_at_its_start_with_its_roles_model()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        var before = await overseer.StartAgentSessionAsync(AgentRole.Developer);

        using var response = await SaveAsync(overseer, Gateway);

        var analyst = await overseer.StartAgentSessionAsync(AgentRole.Analyst);
        var developer = await overseer.StartAgentSessionAsync(AgentRole.Developer);
        Assert.Equal((AgentRole.Analyst, Gateway, "gateway/claude-opus-5-5"), (analyst.Role, analyst.HarnessJson, analyst.Model));
        Assert.Equal((AgentRole.Developer, Gateway, "gateway/gpt-5.5-mini"), (developer.Role, developer.HarnessJson, developer.Model));
        Assert.Equal(AgentRole.Developer, before.Role);
        Assert.NotEqual(Gateway, before.HarnessJson);
        Assert.NotEqual("gateway/gpt-5.5-mini", before.Model);
    }

    [Fact]
    public async Task Invalid_JSON_is_rejected_with_the_parsers_message_and_nothing_is_saved()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        var invalid = Gateway.Replace("\"reviewer\": { \"model\": \"gateway/claude-opus-5-5\" }", "\"reviewer\": { \"model\": gateway/claude-opus-5-5 }");
        var parserMessage = Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(invalid)).Message;

        using var response = await SaveAsync(overseer, invalid);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal($"The configuration is not valid JSON: {parserMessage}", Alert(page));
        // The parser counts lines from 0: the reviewer's model is on the 13th.
        Assert.Contains("LineNumber: 12", parserMessage);
        await AssertNothingSavedAsync(overseer, page, invalid);
    }

    [Theory]
    [InlineData("\"reviewer\": { \"model\": \"gateway/claude-opus-5-5\" }", "\"reviewer\": { }", "Reviewer")]
    [InlineData("\"designer\": { \"model\": \"gateway/claude-sonnet-5-5\" },", "\"designer\": { \"model\": \" \" },", "Designer")]
    [InlineData("\"analyst\": { \"model\": \"gateway/claude-opus-5-5\" },", "", "Analyst")]
    public async Task A_configuration_without_a_model_for_a_role_is_rejected_naming_the_role(string replaced, string replacement, string role)
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        var incomplete = Gateway.Replace(replaced, replacement);
        Assert.NotEqual(Gateway, incomplete);

        using var response = await SaveAsync(overseer, incomplete);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith($"The configuration sets no model for the {role}.", Alert(page));
        await AssertNothingSavedAsync(overseer, page, incomplete);
    }

    [Fact]
    public async Task A_configuration_without_agents_is_rejected_naming_every_role()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        const string withoutAgents = """{ "$schema": "https://opencode.ai/config.json", "model": "anthropic/claude-opus-5-5" }""";

        using var response = await SaveAsync(overseer, withoutAgents);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith("The configuration sets no model for the Analyst and the Designer and the Developer and the Reviewer.", Alert(page));
        await AssertNothingSavedAsync(overseer, page, withoutAgents);
    }

    [Fact]
    public async Task The_model_picker_changes_the_roles_model_in_the_JSON_and_keeps_the_rest()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        using var saved = await SaveAsync(overseer, Gateway);

        using var response = await PickAsync(overseer, GatewayModels.With(AgentRole.Developer, "gateway/claude-sonnet-5-5"));

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The models are saved.", page);
        Assert.Equal(GatewayModels.With(AgentRole.Developer, "gateway/claude-sonnet-5-5"), Pickers(page));
        var expected = JsonNode.Parse(Gateway.Replace("\"model\": \"gateway/gpt-5.5-mini\"", "\"model\": \"gateway/claude-sonnet-5-5\""));
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(Editor(page))), Editor(page));
        var developer = await overseer.StartAgentSessionAsync(AgentRole.Developer);
        Assert.Equal((Editor(page), "gateway/claude-sonnet-5-5"), (developer.HarnessJson, developer.Model));
    }

    [Fact]
    public async Task Picking_the_models_in_use_leaves_the_JSON_as_written()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        using var saved = await SaveAsync(overseer, Gateway);

        using var response = await PickAsync(overseer, GatewayModels);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The models are saved.", page);
        Assert.Equal(Gateway, Editor(page));
    }

    [Fact]
    public async Task An_empty_model_in_the_picker_is_rejected_naming_the_role_and_nothing_is_saved()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        using var saved = await SaveAsync(overseer, Gateway);

        using var response = await PickAsync(overseer, GatewayModels.With(AgentRole.Analyst, "gateway/gpt-5.5").With(AgentRole.Designer, " "));

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith("The configuration sets no model for the Designer.", Alert(page));
        Assert.DoesNotContain("The models are saved.", page);
        Assert.Equal(GatewayModels, Pickers(page));
        Assert.Equal(Gateway, Editor(page));
        Assert.Equal("gateway/claude-opus-5-5", (await overseer.StartAgentSessionAsync(AgentRole.Analyst)).Model);
    }

    [Fact]
    public async Task The_user_can_restore_the_default()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        var original = await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken);
        using var saved = await SaveAsync(overseer, Gateway);

        using var response = await overseer.SubmitFormAsync("/settings", "agent-harness-default", new Dictionary<string, string>());

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The default agent harness configuration is restored.", page);
        Assert.Equal(Editor(original), Editor(page));
        Assert.Equal(Pickers(original), Pickers(page));
        var reviewer = await overseer.StartAgentSessionAsync(AgentRole.Reviewer);
        Assert.Equal((Editor(original), Picker(original, AgentRole.Reviewer)), (reviewer.HarnessJson, reviewer.Model));
    }

    /// <summary>
    /// Asserts that the page still shows the rejected configuration for correction, and that the default stays in use:
    /// in the pickers, on a fresh load of the page, and for the next agent session.
    /// </summary>
    private static async Task AssertNothingSavedAsync(RunningOverseer overseer, string page, string rejected)
    {
        var settings = await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("The agent harness configuration is saved.", page);
        Assert.Equal(rejected, Editor(page));
        Assert.Equal(Pickers(settings), Pickers(page));
        Assert.NotEqual(rejected, Editor(settings));
        var session = await overseer.StartAgentSessionAsync(AgentRole.Developer);
        Assert.Equal(Editor(settings), session.HarnessJson);
    }

    /// <summary>A configuration for an AI gateway, with a different model per role and settings Overseer doesn't know.</summary>
    private const string Gateway = """
        {
          "$schema": "https://opencode.ai/config.json",
          "provider": {
            "gateway": {
              "npm": "@ai-sdk/openai-compatible",
              "options": { "baseURL": "https://gateway.example.com/v1" }
            }
          },
          "agent": {
            "analyst": { "model": "gateway/claude-opus-5-5" },
            "designer": { "model": "gateway/claude-sonnet-5-5" },
            "developer": { "model": "gateway/gpt-5.5-mini", "temperature": 0.2 },
            "reviewer": { "model": "gateway/claude-opus-5-5" }
          }
        }
        """;

    private static readonly Dictionary<AgentRole, string> GatewayModels = new()
    {
        [AgentRole.Analyst] = "gateway/claude-opus-5-5",
        [AgentRole.Designer] = "gateway/claude-sonnet-5-5",
        [AgentRole.Developer] = "gateway/gpt-5.5-mini",
        [AgentRole.Reviewer] = "gateway/claude-opus-5-5",
    };

    /// <summary>Writes <paramref name="json"/> in the Settings page's <c>opencode.json</c> editor and saves it.</summary>
    private static Task<HttpResponseMessage> SaveAsync(RunningOverseer overseer, string json) =>
        overseer.SubmitFormAsync("/settings", "agent-harness", new Dictionary<string, string>
        {
            // A browser sends a textarea's line breaks as CR LF.
            ["AgentHarness"] = json.ReplaceLineEndings("\r\n"),
        });

    /// <summary>Picks a model for each role on the Settings page and saves them.</summary>
    private static Task<HttpResponseMessage> PickAsync(RunningOverseer overseer, IReadOnlyDictionary<AgentRole, string> models) =>
        overseer.SubmitFormAsync("/settings", "agent-models", models.ToDictionary(pair => $"{pair.Key}Model", pair => pair.Value));

    /// <summary>The model each role's picker shows.</summary>
    private static Dictionary<AgentRole, string> Pickers(string page) =>
        Enum.GetValues<AgentRole>().ToDictionary(role => role, role => Picker(page, role));

    /// <summary>The text in the <c>opencode.json</c> editor, as a browser would read it.</summary>
    private static string Editor(string page) =>
        Regex.Match(page, "<textarea[^>]*name=\"AgentHarness\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline) is { Success: true } editor
            ? WebUtility.HtmlDecode(editor.Groups[1].Value)
            : throw new InvalidOperationException("The page has no opencode.json editor.");

    /// <summary>The error the agent harness forms show, as a browser would read it.</summary>
    private static string Alert(string page) =>
        Regex.Match(page, "<p[^>]*role=\"alert\"[^>]*>(.*?)</p>", RegexOptions.Singleline) is { Success: true } alert
            ? WebUtility.HtmlDecode(alert.Groups[1].Value)
            : throw new InvalidOperationException("The page shows no error.");

    /// <summary>The model the picker shows for <paramref name="role"/>.</summary>
    private static string Picker(string page, AgentRole role) =>
        Regex.Match(page, $"<input[^>]*name=\"{role}Model\"[^>]*>") is { Success: true } input
            ? WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)
            : throw new InvalidOperationException($"The page has no model picker for the {role}.");
}

internal static class DictionaryCopies
{
    /// <summary>A copy with <paramref name="role"/>'s model replaced.</summary>
    public static Dictionary<AgentRole, string> With(this IReadOnlyDictionary<AgentRole, string> models, AgentRole role, string model) =>
        new(models) { [role] = model };
}
