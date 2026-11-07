using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Overseer.Sandboxes;

/// <summary>
/// The agent harness configuration: the <c>opencode.json</c> agents run with. It defines an OpenCode agent per role,
/// named after the role in lower case, and sets the model of each.
/// </summary>
public sealed partial class AgentHarnessConfiguration
{
    // Declared before Default, which is parsed with them.
    // Strict JSON, and no property twice, so what Overseer reads is what OpenCode reads.
    private static readonly JsonDocumentOptions ParserOptions = new() { AllowDuplicateProperties = false };

    // Like Overseer's default, and non-ASCII and HTML-sensitive characters kept as they are, as the user would write them.
    private static readonly JsonSerializerOptions WriterOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IReadOnlyDictionary<AgentRole, string> _models;

    private AgentHarnessConfiguration(string json, IReadOnlyDictionary<AgentRole, string> models)
    {
        Json = json;
        _models = models;
    }

    /// <summary>The <c>opencode.json</c>, as the user wrote it.</summary>
    public string Json { get; }

    /// <summary>The model the agent in <paramref name="role"/> uses, as OpenCode names it: <c>provider/model</c>.</summary>
    public string ModelOf(AgentRole role) => _models[role];

    /// <summary>
    /// The environment variables the configuration references as <c>{env:NAME}</c>, which OpenCode replaces with their
    /// values, in order of first reference.
    /// </summary>
    public IReadOnlyList<string> EnvironmentVariables =>
        [.. EnvironmentReference().Matches(Json).Select(reference => reference.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    // As OpenCode finds them: anywhere in the text, before it parses the JSON.
    [GeneratedRegex(@"\{env:([^}]+)\}")]
    private static partial Regex EnvironmentReference();

    /// <summary>The name of the OpenCode agent for <paramref name="role"/>.</summary>
    public static string AgentName(AgentRole role) => role.ToString().ToLowerInvariant();

    /// <summary>
    /// The configuration Overseer ships: the strongest model where judgement matters most, a cheaper one elsewhere. Every tool is
    /// allowed without asking, as no one is there to answer, except that the Analyst may not <c>git push</c>: OpenCode applies the
    /// last bash rule that matches. That is no guarantee, since the user can edit it: the guarantee has to come from the Analyst's
    /// Sandbox itself.
    /// </summary>
    public static AgentHarnessConfiguration Default { get; } = Parse(
        """
        {
          "$schema": "https://opencode.ai/config.json",
          "autoupdate": false,
          "permission": "allow",
          "agent": {
            "analyst": {
              "description": "Questions the user about an Intent and writes its Spec.",
              "mode": "primary",
              "model": "anthropic/claude-opus-5-5",
              "permission": {
                "bash": {
                  "*": "allow",
                  "git push": "deny",
                  "git push *": "deny"
                }
              }
            },
            "designer": {
              "description": "Works out the UI and UX for a Spec with UI changes.",
              "mode": "primary",
              "model": "anthropic/claude-sonnet-5-5"
            },
            "developer": {
              "description": "Implements a Task, including the tests for its own work.",
              "mode": "primary",
              "model": "anthropic/claude-sonnet-5-5"
            },
            "reviewer": {
              "description": "Reviews an Intent's pull requests against the Approved Spec and the Review Guidelines.",
              "mode": "primary",
              "model": "anthropic/claude-opus-5-5"
            }
          }
        }

        """);

    /// <summary>Reads an <c>opencode.json</c> as the user wrote it, which must set a model for every role.</summary>
    /// <param name="error">Why <paramref name="json"/> can't be used, with the JSON parser's own message when it isn't JSON.</param>
    public static bool TryParse(string? json, [NotNullWhen(true)] out AgentHarnessConfiguration? configuration, [NotNullWhen(false)] out string? error)
    {
        configuration = null;
        json = (json ?? "").ReplaceLineEndings("\n");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, ParserOptions);
        }
        catch (JsonException exception)
        {
            error = $"The configuration is not valid JSON: {exception.Message}";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "The configuration must be a JSON object.";
                return false;
            }

            var models = new Dictionary<AgentRole, string>();
            foreach (var role in Enum.GetValues<AgentRole>())
            {
                if (document.RootElement.TryGetProperty("agent", out var agents) && agents.ValueKind == JsonValueKind.Object
                    && agents.TryGetProperty(AgentName(role), out var agent) && agent.ValueKind == JsonValueKind.Object
                    && agent.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(model.GetString()))
                {
                    models[role] = model.GetString()!;
                }
            }

            var missing = Enum.GetValues<AgentRole>().Where(role => !models.ContainsKey(role)).ToList();
            if (missing.Count > 0)
            {
                error = $"The configuration sets no model for the {string.Join(" and the ", missing)}. "
                    + $"Each role needs one, such as \"agent\": {{ \"{AgentName(missing[0])}\": {{ \"model\": \"provider/model\" }} }}.";
                return false;
            }

            configuration = new AgentHarnessConfiguration(json, models);
            error = null;
            return true;
        }
    }

    /// <summary>
    /// This configuration's JSON with the given model for each role, and everything else as it is. Unchanged when every
    /// model is already the one given; otherwise rewritten, indented by two spaces.
    /// </summary>
    /// <remarks>The result isn't checked: read it with <see cref="TryParse"/>, which rejects an empty model.</remarks>
    public string WithModels(IReadOnlyDictionary<AgentRole, string?> models)
    {
        var changed = models.Where(pair => (pair.Value?.Trim() ?? "") != ModelOf(pair.Key)).ToList();
        if (changed.Count == 0)
        {
            return Json;
        }

        // Every role has an agent object with a model, or this configuration wouldn't have parsed.
        var root = JsonNode.Parse(Json, documentOptions: ParserOptions)!;
        foreach (var (role, model) in changed)
        {
            root["agent"]![AgentName(role)]!["model"] = model?.Trim() ?? "";
        }
        return root.ToJsonString(WriterOptions) + (Json.EndsWith('\n') ? "\n" : "");
    }

    private static AgentHarnessConfiguration Parse(string json) =>
        TryParse(json, out var configuration, out var error) ? configuration : throw new InvalidOperationException(error);
}
