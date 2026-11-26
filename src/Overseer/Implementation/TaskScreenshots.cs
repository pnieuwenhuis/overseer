using System.Text;
using Microsoft.EntityFrameworkCore;
using Overseer.Persistence;
using Overseer.Sandboxes;

namespace Overseer.Implementation;

/// <summary>
/// The screenshots of each Designer Task's design, taken from the Sandbox as the Designer hands them in
/// (<see cref="ScreenshotHandIn"/>) and kept in PostgreSQL, so that the Portal shows them also once the Sandbox is gone. A
/// Task has one set, the last one handed in that was valid.
/// </summary>
public sealed class TaskScreenshots(SandboxRegistry sandboxes, IDbContextFactory<OverseerDbContext> databases)
{
    private const int LargestImage = ScreenshotHandIn.LargestImageMegabytes * 1024 * 1024;

    /// <summary>
    /// Takes away a list of screenshots left in the Sandbox, as by a session that broke off, so that a new session's Task
    /// doesn't count what it didn't hand in.
    /// </summary>
    public async Task DiscardHandedInAsync(Sandbox sandbox, CancellationToken cancellationToken) =>
        await sandboxes.TakeFileAsync(sandbox, ScreenshotHandIn.FilePath, cancellationToken);

    /// <summary>
    /// Takes the screenshots the Designer handed in at the end of its turn, if it did, and keeps them as the Task's, in place
    /// of any it had. Nothing of a hand-in is kept unless all of it is valid.
    /// </summary>
    /// <param name="handedInBefore">Whether the session has handed in screenshots that still stand.</param>
    /// <returns>
    /// The failed step of the verification that says what is missing or wrong; <c>null</c> when the Task has screenshots of this
    /// session, handed in now or before.
    /// </returns>
    /// <exception cref="InvalidOperationException">The Sandbox couldn't give them; the message says why.</exception>
    public async Task<VerificationStep?> TakeAsync(
        Guid intentId, int taskNumber, Sandbox sandbox, bool handedInBefore, CancellationToken cancellationToken)
    {
        switch (await ScreenshotHandIn.Instance.TakeAsync(sandboxes, sandbox, cancellationToken))
        {
            case HandedIn<IReadOnlyList<ListedScreenshot>>.Valid(var listed):
                var images = new List<TaskScreenshotRecord>();
                var errors = new List<string>();
                foreach (var screenshot in listed)
                {
                    var (image, error) = await TakeImageAsync(sandbox, screenshot, cancellationToken);
                    if (image is null)
                    {
                        errors.Add(error!);
                        continue;
                    }
                    images.Add(new TaskScreenshotRecord
                    {
                        IntentId = intentId,
                        TaskNumber = taskNumber,
                        Number = screenshot.Number,
                        Caption = await sandboxes.WithoutSecretsAsync(screenshot.Caption, cancellationToken),
                        ContentType = image.ContentType,
                        Content = image.Content,
                    });
                }
                if (errors.Count > 0)
                {
                    return await InvalidAsync(errors, cancellationToken);
                }
                await ReplaceAsync(intentId, taskNumber, images, cancellationToken);
                return null;
            case HandedIn<IReadOnlyList<ListedScreenshot>>.Invalid(var invalid):
                return await InvalidAsync(invalid, cancellationToken);
            default:
                return handedInBefore
                    ? null
                    : VerificationStep.OfScreenshots("No screenshots of the design were handed in.", output: "");
        }
    }

    /// <summary>The screenshots of the Intent's Tasks, by Task, in the order they were listed.</summary>
    public async Task<IReadOnlyList<TaskScreenshot>> OfAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        // Without the images, which the browser asks for one by one.
        return await database.TaskScreenshots
            .Where(screenshot => screenshot.IntentId == intentId)
            .OrderBy(screenshot => screenshot.TaskNumber).ThenBy(screenshot => screenshot.Number)
            .Select(screenshot => new TaskScreenshot(screenshot.Id, screenshot.TaskNumber, screenshot.Caption))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The image of one of the Intent's screenshots; <c>null</c> when it has none of this id.</summary>
    public async Task<ScreenshotImage?> ImageOfAsync(Guid intentId, Guid id, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return await database.TaskScreenshots
            .Where(screenshot => screenshot.IntentId == intentId && screenshot.Id == id)
            .Select(screenshot => new ScreenshotImage(screenshot.ContentType, screenshot.Content))
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>Takes a listed image from the Sandbox, and gives it, or what is wrong with it.</summary>
    private async Task<(ScreenshotImage? Image, string? Error)> TakeImageAsync(
        Sandbox sandbox, ListedScreenshot screenshot, CancellationToken cancellationToken)
    {
        var named = $"Screenshot {screenshot.Number}, {screenshot.File},";
        byte[]? content;
        try
        {
            content = await sandboxes.TakeBinaryFileAsync(
                sandbox, $"{ScreenshotHandIn.Directory}/{screenshot.File}", LargestImage, cancellationToken);
        }
        catch (SandboxFileTooLargeException)
        {
            return (null, $"{named} is larger than {ScreenshotHandIn.LargestImageMegabytes} MB.");
        }
        if (content is null)
        {
            return (null, $"{named} isn't there: there is no such file in {ScreenshotHandIn.Directory}.");
        }
        if (ContentTypeOf(content) is not { } contentType)
        {
            return (null, $"{named} isn't a PNG, JPEG, GIF or WebP image.");
        }
        // The Sandbox holds the GitHub token, so a file can hold it; a secret never goes into PostgreSQL.
        await sandboxes.WithoutSecretsAsync("", cancellationToken);
        return sandboxes.SecretsGiven.Any(secret => secret.Length > 0 && content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0)
            ? (null, $"{named} holds a secret, such as a token, so it isn't kept.")
            : (new ScreenshotImage(contentType, content), null);
    }

    /// <summary>The kind of image the bytes begin as; <c>null</c> when they begin as none a screenshot may be.</summary>
    private static string? ContentTypeOf(ReadOnlySpan<byte> content) => content switch
    {
        [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        _ when content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8) => "image/gif",
        _ when content.Length >= 12 && content.StartsWith("RIFF"u8) && content[8..].StartsWith("WEBP"u8) => "image/webp",
        _ => null,
    };

    private async Task<VerificationStep> InvalidAsync(IReadOnlyList<string> errors, CancellationToken cancellationToken) =>
        // They quote what the agent wrote.
        VerificationStep.OfScreenshots(
            "The screenshots handed in aren't valid.",
            await sandboxes.WithoutSecretsAsync(string.Join("\n", errors.Select(error => $"- {error}")), cancellationToken));

    private async Task ReplaceAsync(
        Guid intentId, int taskNumber, IReadOnlyList<TaskScreenshotRecord> screenshots, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.TaskScreenshots
            .Where(screenshot => screenshot.IntentId == intentId && screenshot.TaskNumber == taskNumber)
            .ExecuteDeleteAsync(cancellationToken);
        database.TaskScreenshots.AddRange(screenshots);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>A screenshot of a Task's design, without its image (<see cref="TaskScreenshots.ImageOfAsync"/>).</summary>
public sealed record TaskScreenshot(Guid Id, int TaskNumber, string Caption);

/// <param name="ContentType">The media type of the image, such as <c>image/png</c>.</param>
public sealed record ScreenshotImage(string ContentType, byte[] Content);

/// <summary>A screenshot, as PostgreSQL holds it.</summary>
public sealed class TaskScreenshotRecord
{
    /// <summary>New for each image handed in, so that its address never shows another one.</summary>
    public Guid Id { get; init; }

    public required Guid IntentId { get; init; }
    public required int TaskNumber { get; init; }

    /// <summary>Its place among the Task's, from 1.</summary>
    public required int Number { get; init; }

    public required string Caption { get; init; }
    public required string ContentType { get; init; }
    public required byte[] Content { get; init; }
}
