using System.Text.Json.Nodes;
using Overseer.Sandboxes;

namespace Overseer.Tests;

/// <summary>Playing a Designer that hands in screenshots, without which its Task isn't done.</summary>
public static class DesignerHandsIn
{
    /// <summary>Where the Designer writes the list of its screenshots.</summary>
    public const string ListPath = "/home/overseer/.overseer/screenshots.json";

    /// <summary>Where the Designer saves their images.</summary>
    public const string ScreenshotDirectory = "/home/overseer/.overseer/screenshots";

    /// <summary>A PNG image of one pixel.</summary>
    public static byte[] Png { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    /// <summary>The start of a JPEG image.</summary>
    public static byte[] Jpeg { get; } = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0xFF, 0xD9];

    /// <summary>A turn that ends as done having saved these images and listed them.</summary>
    public static FakeAgentTurn HandsIn(params (string File, string Caption, byte[] Content)[] screenshots) =>
        new FakeAgentTurn([], new AgentOutcome.Done()).HandingIn(screenshots);

    /// <summary>The turn, during which the agent also saves these images and lists them.</summary>
    private static FakeAgentTurn HandingIn(this FakeAgentTurn turn, params (string File, string Caption, byte[] Content)[] screenshots) =>
        turn with
        {
            Writes = new Dictionary<string, string>(turn.Writes)
            {
                [ListPath] = ListOf([.. screenshots.Select(screenshot => (screenshot.File, screenshot.Caption))]),
            },
            BinaryWrites = screenshots.ToDictionary(screenshot => $"{ScreenshotDirectory}/{screenshot.File}", screenshot => screenshot.Content),
        };

    /// <summary>The turn, during which the agent also hands in a screenshot, as a Designer's that does its Task.</summary>
    public static FakeAgentTurn WithScreenshot(this FakeAgentTurn turn) => turn.HandingIn(("design.png", "The design", Png));

    /// <summary>The list of these screenshots, as the Designer writes it.</summary>
    public static string ListOf(params (string File, string Caption)[] screenshots) => new JsonObject
    {
        ["screenshots"] = new JsonArray([.. screenshots.Select(screenshot => new JsonObject
        {
            ["file"] = screenshot.File,
            ["caption"] = screenshot.Caption,
        })]),
    }.ToJsonString();

    /// <summary>A session whose every turn ends as done with a screenshot handed in, as a Designer's that did its Task.</summary>
    public static FakeAgentSession DesignsAtOnce() => new(new FakeAgentTurn([], new AgentOutcome.Done()).WithScreenshot());
}
