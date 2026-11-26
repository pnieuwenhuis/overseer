using System.Text.Json;
using Overseer.Sandboxes;

namespace Overseer.Implementation;

/// <summary>
/// The screenshots of a design as the Designer hands them in: images in a directory of their own, and a JSON file that lists
/// them in order, each with a caption saying what it shows. The Designer's instructions describe the format. The images
/// themselves are taken once the list is valid (<see cref="TaskScreenshots.Session.TakeAsync"/>).
/// </summary>
internal sealed class ScreenshotHandIn : HandIn<IReadOnlyList<ListedScreenshot>>
{
    public static ScreenshotHandIn Instance { get; } = new();

    /// <summary>Where the images go: in the home of the Sandbox's user, outside the repositories.</summary>
    public const string Directory = $"{Sandbox.HomeDirectory}/.overseer/screenshots";

    /// <summary>The list of them, next to the directory.</summary>
    public const string FilePath = $"{Directory}.json";

    public override string Path => FilePath;

    /// <summary>The most screenshots one hand-in may list.</summary>
    public const int MostScreenshots = 20;

    /// <summary>The most megabytes one image may hold.</summary>
    public const int LargestImageMegabytes = 5;

    /// <summary>An example of the list, for the Designer's instructions.</summary>
    public const string Example = """
        {
          "screenshots": [
            { "file": "orders-page.png", "caption": "The orders page, with the Export button above the table." }
          ]
        }
        """;

    /// <summary>How the Designer hands in its screenshots, in Markdown.</summary>
    public static string HowToHandIn { get; } =
        $"""
        Save each screenshot as a PNG, JPEG, GIF or WebP image in `{Directory}/`, outside the repositories: screenshots don't
        belong in a repository, so don't commit them. Then list them in `{FilePath}`, as JSON such as:

        ```json
        {Example}
        ```

        "file" is the name of an image in that directory, and "caption" says what it shows. List at most {MostScreenshots}, each at
        most {LargestImageMegabytes} MB. When you end your turn, Overseer takes the list and the images out of the Sandbox, so to hand
        in screenshots again, save the images and write the list anew. A new list replaces what you handed in before, so list every
        screenshot the human should see.
        """;

    protected override HandedIn<IReadOnlyList<ListedScreenshot>> Validate(string content) => FromJson(content, root =>
    {
        if (ListOf(root, "screenshots") is not { } screenshots)
        {
            return Invalid($"{FilePath} must hold a JSON object whose \"screenshots\" is a list of screenshots.");
        }
        if (screenshots.Count is 0 or > MostScreenshots)
        {
            return Invalid($"{FilePath} must list at least 1 and at most {MostScreenshots} screenshots, not {screenshots.Count}.");
        }

        var errors = new List<string>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var read = ReadEach(screenshots, "Screenshot", ["file", "caption"], errors, (screenshot, number) =>
        {
            var file = TextOf(screenshot, "file");
            var caption = TextOf(screenshot, "caption");
            if (file is null)
            {
                errors.Add($"Screenshot {number} has no \"file\": it must be the name of an image in {Directory}.");
            }
            else if (file is "." or ".." || file.Contains('/') || file.Any(char.IsControl))
            {
                errors.Add($"Screenshot {number}'s \"file\" must be the name of an image in {Directory}, without a path: {file}");
                file = null;
            }
            else if (!listed.Add(file))
            {
                errors.Add($"Screenshot {number} lists {file} again: list each image once.");
                file = null;
            }
            if (caption is null)
            {
                errors.Add($"Screenshot {number} has no \"caption\": it must be text that isn't empty.");
            }
            return file is not null && caption is not null ? new ListedScreenshot(number, file, caption) : null;
        });
        return errors.Count == 0
            ? new HandedIn<IReadOnlyList<ListedScreenshot>>.Valid(read)
            : new HandedIn<IReadOnlyList<ListedScreenshot>>.Invalid(errors);
    });

    private static HandedIn<IReadOnlyList<ListedScreenshot>>.Invalid Invalid(string error) => new([error]);
}

/// <summary>A screenshot as the Designer lists it.</summary>
/// <param name="Number">Its place in the list, from 1.</param>
/// <param name="File">The name of its image in <see cref="ScreenshotHandIn.Directory"/>.</param>
internal sealed record ListedScreenshot(int Number, string File, string Caption);
