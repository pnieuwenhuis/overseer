using System.Text.Json.Nodes;
using Overseer.Sandboxes;
using static Overseer.Tests.AgentTurns;

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

    /// <summary>A JPEG image of 4 by 3 pixels, as <c>cjpeg</c> writes it.</summary>
    public static byte[] Jpeg { get; } = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PT" +
        "gyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAAD" +
        "AAQDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhBy" +
        "JxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKT" +
        "lJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAA" +
        "AAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRom" +
        "JygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExc" +
        "bHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDBooor5w/QD//Z");

    /// <summary>As <see cref="Jpeg"/>, progressive: in several scans.</summary>
    public static byte[] ProgressiveJpeg { get; } = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PT" +
        "gyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wgARCAAD" +
        "AAQDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAT/xAAVAQEBAAAAAAAAAAAAAAAAAAAFBv/aAAwDAQACEAMQAAABgBtB/8QAFBABAA" +
        "AAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/a" +
        "AAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyF//9oADAMBAA" +
        "IAAwAAABAL/8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPxB//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPxB//8QAFBABAAAA" +
        "AAAAAAAAAAAAAAAAAP/aAAgBAQABPxB//9k=");

    /// <summary>As <see cref="Jpeg"/>, as <c>sips</c> writes it, with Exif and Photoshop data.</summary>
    public static byte[] JpegWithMetadata { get; } = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAASABIAAD/4QBMRXhpZgAATU0AKgAAAAgAAYdpAAQAAAABAAAAGgAAAAAAA6ABAAMAAAABAAEAAKACAAQAAAABAAAABK" +
        "ADAAQAAAABAAAAAwAAAAD/7QA4UGhvdG9zaG9wIDMuMAA4QklNBAQAAAAAAAA4QklNBCUAAAAAABDUHYzZjwCyBOmACZjs+EJ+/8AAEQgAAwAE" +
        "AwEiAAIRAQMRAf/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMCBAMFBQQEAAABfQECAwAEEQUSITFBBhNRYQcicR" +
        "QygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SV" +
        "lpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/EAB8BAAMBAQEBAQEBAQEAAAAAAA" +
        "ABAgMEBQYHCAkKC//EALURAAIBAgQEAwQHBQQEAAECdwABAgMRBAUhMQYSQVEHYXETIjKBCBRCkaGxwQkjM1LwFWJy0QoWJDThJfEXGBkaJico" +
        "KSo1Njc4OTpDREVGR0hJSlNUVVZXWFlaY2RlZmdoaWpzdHV2d3h5eoKDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8" +
        "jJytLT1NXW19jZ2uLj5OXm5+jp6vLz9PX29/j5+v/bAEMAAgICAgICAwICAwUDAwMFBgUFBQUGCAYGBgYGCAoICAgICAgKCgoKCgoKCgwMDAwM" +
        "DA4ODg4ODw8PDw8PDw8PD//bAEMBAgICBAQEBwQEBxALCQsQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEB" +
        "AQEP/dAAQAAf/aAAwDAQACEQMRAD8A8Hooor+Yz/SA/9k=");

    /// <summary>A GIF image of 4 by 3 pixels, in the older GIF87a version.</summary>
    public static byte[] Gif { get; } = Convert.FromBase64String("R0lGODdhBAADAJEAAAAAAMg8Hv///wAAACH5BAkAAAMALAAAAAAEAAMAAAIDjI9WADs=");

    /// <summary>An animated GIF image, as <c>ffmpeg</c> writes it.</summary>
    public static byte[] AnimatedGif { get; } = Convert.FromBase64String(
        "R0lGODlhBAAEAPcfMQAAACQAAEgAAGwAAJAAALQAANgAAPwAAAAkACQkAEgkAGwkAJAkALQkANgkAPwkAABIACRIAEhIAGxIAJBIALRIANhIAP" +
        "xIAABsACRsAEhsAGxsAJBsALRsANhsAPxsAACQACSQAEiQAGyQAJCQALSQANiQAPyQAAC0ACS0AEi0AGy0AJC0ALS0ANi0APy0AADYACTYAEjY" +
        "AGzYAJDYALTYANjYAPzYAAD8ACT8AEj8AGz8AJD8ALT8ANj8APz8AAAAVSQAVUgAVWwAVZAAVbQAVdgAVfwAVQAkVSQkVUgkVWwkVZAkVbQkVd" +
        "gkVfwkVQBIVSRIVUhIVWxIVZBIVbRIVdhIVfxIVQBsVSRsVUhsVWxsVZBsVbRsVdhsVfxsVQCQVSSQVUiQVWyQVZCQVbSQVdiQVfyQVQC0VSS0" +
        "VUi0VWy0VZC0VbS0Vdi0Vfy0VQDYVSTYVUjYVWzYVZDYVbTYVdjYVfzYVQD8VST8VUj8VWz8VZD8VbT8Vdj8Vfz8VQAAqiQAqkgAqmwAqpAAqr" +
        "QAqtgAqvwAqgAkqiQkqkgkqmwkqpAkqrQkqtgkqvwkqgBIqiRIqkhIqmxIqpBIqrRIqthIqvxIqgBsqiRsqkhsqmxsqpBsqrRsqthsqvxsqgCQ" +
        "qiSQqkiQqmyQqpCQqrSQqtiQqvyQqgC0qiS0qki0qmy0qpC0qrS0qti0qvy0qgDYqiTYqkjYqmzYqpDYqrTYqtjYqvzYqgD8qiT8qkj8qmz8qp" +
        "D8qrT8qtj8qvz8qgAA/yQA/0gA/2wA/5AA/7QA/9gA//wA/wAk/yQk/0gk/2wk/5Ak/7Qk/9gk//wk/wBI/yRI/0hI/2xI/5BI/7RI/9hI//xI" +
        "/wBs/yRs/0hs/2xs/5Bs/7Rs/9hs//xs/wCQ/ySQ/0iQ/2yQ/5CQ/7SQ/9iQ//yQ/wC0/yS0/0i0/2y0/5C0/7S0/9i0//y0/wDY/yTY/0jY/2" +
        "zY/5DY/7TY/9jY//zY/wD8/yT8/0j8/2z8/5D8/7T8/9j8//z8/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQEMgAfACwAAAAABAAEAAAIEwABHMDx" +
        "AwC+Y8AMIjzAA9+wgAAAIfkEBTIAAAAsAAADAAQAAQAACAcAT+AAdywgADs=");

    /// <summary>A lossy WebP image of 4 by 3 pixels, as <c>cwebp</c> writes it.</summary>
    public static byte[] WebP { get; } = Convert.FromBase64String("UklGRjoAAABXRUJQVlA4IC4AAADQAQCdASoEAAMAAgA0JaACdLoB+AADsAD+7L2P/FnIxHZ5v/q0PlofLQ/bsAAA");

    /// <summary>As <see cref="WebP"/>, lossless.</summary>
    public static byte[] LosslessWebP { get; } = Convert.FromBase64String("UklGRh4AAABXRUJQVlA4TBEAAAAvA4AAAAdQniLXo/+BiOh/AAA=");

    /// <summary>As <see cref="WebP"/>, with transparency, which takes the extended format.</summary>
    public static byte[] TransparentWebP { get; } = Convert.FromBase64String(
        "UklGRmAAAABXRUJQVlA4WAoAAAAQAAAAAwAAAgAAQUxQSAsAAAABH1CUiAiUPKL/AQBWUDggLgAAANABAJ0BKgQAAwACADQloAJ0ugH4AAOwAP" +
        "7svY/8WcjEdnm/+rQ+Wh8tD9uwAAA=");

    /// <summary>An animated WebP image, as <c>gif2webp</c> writes it.</summary>
    public static byte[] AnimatedWebP { get; } = Convert.FromBase64String(
        "UklGRsoAAABXRUJQVlA4WAoAAAASAAAAAwAAAwAAQU5JTQYAAAAAbPz/AABBTk1GUAAAAAAAAAAAAAMAAAMAAPQBAAJWUDhMOAAAAC8DwAAAR2" +
        "AgbZtsv3/FE0ImbRMxFTj/7BAQFPk/2vwHsoE4D2asgCzANGqxey4JxJJDEf2PRuQDQU5NRkYAAAAAAAABAAADAAABAAD0AQAAVlA4TC4AAAAv" +
        "A0AAECegIG0Dpjv+meAJIRugKPx/sUFAUOT/aPMfwLHfCBsEAinOYYUj+h80");

    /// <summary>A turn that ends as done having saved these images and listed them.</summary>
    public static FakeAgentTurn HandsIn(params (string File, string Caption, byte[] Content)[] screenshots) =>
        Done().HandingIn(screenshots);

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
    public static FakeAgentSession DesignsAtOnce() => new(Done().WithScreenshot());
}
