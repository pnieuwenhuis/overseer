using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Overseer.Tests;

/// <summary>
/// What OpenCode, at the version Sandboxes install, does when an agent in an <c>opencode.json</c> uses a permission:
/// allow, ask or deny. Written from OpenCode 1.19.4's permission docs and source
/// (https://raw.githubusercontent.com/anomalyco/opencode/v1.19.4/packages/web/src/content/docs/permissions.mdx,
/// packages/opencode/src/agent/agent.ts and src/permission/index.ts at the same tag), as an independent check on the
/// configuration Overseer ships: a custom agent's rules are OpenCode's defaults, then the global <c>permission</c>, then
/// the agent's own, and the last rule whose permission and pattern both match wins.
/// </summary>
public sealed class OpenCodePermissions
{
    // OpenCode's defaults for an agent it doesn't define itself: everything allowed, except these.
    private static readonly IReadOnlyList<Rule> Defaults =
    [
        new("*", "*", "allow"),
        new("doom_loop", "*", "ask"),
        new("external_directory", "*", "ask"),
        new("question", "*", "deny"),
        new("plan_enter", "*", "deny"),
        new("plan_exit", "*", "deny"),
        new("read", "*", "allow"),
        new("read", "*.env", "ask"),
        new("read", "*.env.*", "ask"),
        new("read", "*.env.example", "allow"),
    ];

    private readonly IReadOnlyList<Rule> _rules;

    private OpenCodePermissions(IReadOnlyList<Rule> rules) => _rules = rules;

    /// <summary>The permissions of <paramref name="agent"/> in the <c>opencode.json</c> <paramref name="json"/>.</summary>
    public static OpenCodePermissions Of(string json, string agent)
    {
        var configuration = JsonNode.Parse(json)!;
        return new([.. Defaults, .. Rules(configuration["permission"]), .. Rules(configuration["agent"]?[agent]?["permission"])]);
    }

    /// <summary>
    /// What OpenCode does when the agent uses <paramref name="permission"/> on <paramref name="pattern"/>, such as
    /// <c>bash</c> on a command or <c>read</c> on a path: <c>allow</c>, <c>ask</c> or <c>deny</c>.
    /// </summary>
    public string Evaluate(string permission, string pattern) =>
        _rules.LastOrDefault(rule => Matches(permission, rule.Permission) && Matches(pattern, rule.Pattern))?.Action ?? "ask";

    // "allow" for everything, { "bash": "allow" } for every use of one, or { "bash": { "git *": "deny" } } per pattern.
    private static IEnumerable<Rule> Rules(JsonNode? permission) => permission switch
    {
        null => [],
        JsonValue action => [new("*", "*", action.GetValue<string>())],
        JsonObject permissions => permissions.SelectMany(entry => entry.Value is JsonObject patterns
            ? patterns.Select(rule => new Rule(entry.Key, rule.Key, rule.Value!.GetValue<string>()))
            : [new Rule(entry.Key, "*", entry.Value!.GetValue<string>())]),
        _ => throw new ArgumentException($"OpenCode reads no permission like {permission.ToJsonString()}."),
    };

    // * is anything and ? one character; a trailing " *" also matches nothing, so "git push *" matches "git push".
    private static bool Matches(string value, string pattern)
    {
        var expression = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        if (expression.EndsWith(@"\ .*", StringComparison.Ordinal))
        {
            expression = expression[..^4] + "( .*)?";
        }
        return Regex.IsMatch(value, $"^{expression}$", RegexOptions.Singleline);
    }

    private sealed record Rule(string Permission, string Pattern, string Action);
}
