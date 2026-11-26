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

    /// <summary>What a Designer's session on the Task hands in, in its Sandbox.</summary>
    public Session OfSession(TaskKey task, Sandbox sandbox) => new(this, task, sandbox);

    /// <summary>
    /// The screenshots one Designer session hands in, taken at the end of each of its turns, and whether they stand: the Task
    /// needs a valid set of this session to be Done.
    /// </summary>
    public sealed class Session(TaskScreenshots screenshots, TaskKey task, Sandbox sandbox)
    {
        /// <summary>Whether the session has handed in screenshots that still stand.</summary>
        private bool _standing;

        /// <summary>What is wrong with what it last handed in, until a check has said so.</summary>
        private VerificationStep? _invalid;

        /// <summary>
        /// Takes away a list of screenshots left in the Sandbox, as by a session that broke off, so that this one's Task doesn't
        /// count what it didn't hand in. One that can't be read stays, for the first turn's check to say so.
        /// </summary>
        public Task DiscardLeftOverAsync(CancellationToken cancellationToken) =>
            screenshots.DiscardListAsync(sandbox, cancellationToken);

        /// <summary>
        /// Takes the screenshots the Designer handed in during the turn that has ended, if it did, and keeps them as the Task's,
        /// in place of any it had. Nothing of a hand-in is kept unless all of it is valid; one that isn't leaves the Task the
        /// screenshots it had, which no longer stand.
        /// </summary>
        /// <returns>Whether the Task has other screenshots now.</returns>
        /// <exception cref="InvalidOperationException">The Sandbox couldn't give them; the message says why.</exception>
        public async Task<bool> TakeAsync(CancellationToken cancellationToken)
        {
            switch (await screenshots.HandedInAsync(task, sandbox, cancellationToken))
            {
                case HandedIn<IReadOnlyList<TaskScreenshotRecord>>.Valid(var images):
                    await screenshots.ReplaceAsync(task, images, cancellationToken);
                    (_standing, _invalid) = (true, null);
                    return true;
                case HandedIn<IReadOnlyList<TaskScreenshotRecord>>.Invalid(var errors):
                    (_standing, _invalid) = (false, await screenshots.InvalidAsync(errors, cancellationToken));
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The check of the screenshots, as part of verifying the Task's work. What was wrong with a hand-in is said once: a later
        /// check, with nothing handed in since, finds them missing.
        /// </summary>
        /// <returns>
        /// The failed step of the verification that says what is missing or wrong; <c>null</c> when the Task has screenshots of
        /// this session, handed in with this turn or before.
        /// </returns>
        public VerificationStep? Check()
        {
            if (_standing)
            {
                return null;
            }
            var failed = _invalid ?? VerificationStep.OfScreenshots("No screenshots of the design were handed in.", output: "");
            _invalid = null;
            return failed;
        }
    }

    private async Task DiscardListAsync(Sandbox sandbox, CancellationToken cancellationToken)
    {
        try
        {
            await sandboxes.TakeFileAsync(sandbox, ScreenshotHandIn.FilePath, cancellationToken);
        }
        catch (SandboxFileUnreadableException)
        {
            // Nothing of it can count: the hand-in that takes it next finds it unreadable too, unless the agent writes it anew.
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

    /// <summary>
    /// Takes what the Designer handed in from the Sandbox: the Task's screenshots as they are to be kept, when all of it is valid,
    /// else every reason it isn't.
    /// </summary>
    private async Task<HandedIn<IReadOnlyList<TaskScreenshotRecord>>> HandedInAsync(
        TaskKey task, Sandbox sandbox, CancellationToken cancellationToken)
    {
        HandedIn<IReadOnlyList<ListedScreenshot>> handedIn;
        try
        {
            handedIn = await ScreenshotHandIn.Instance.TakeAsync(sandboxes, sandbox, cancellationToken);
        }
        catch (SandboxFileUnreadableException)
        {
            return new HandedIn<IReadOnlyList<TaskScreenshotRecord>>.Invalid(
                [$"{ScreenshotHandIn.FilePath} is there, but can't be read."]);
        }
        switch (handedIn)
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
                        IntentId = task.IntentId,
                        TaskNumber = task.Number,
                        Number = screenshot.Number,
                        Caption = await sandboxes.WithoutSecretsAsync(screenshot.Caption, cancellationToken),
                        ContentType = image.ContentType,
                        Content = image.Content,
                    });
                }
                return errors.Count > 0
                    ? new HandedIn<IReadOnlyList<TaskScreenshotRecord>>.Invalid(errors)
                    : new HandedIn<IReadOnlyList<TaskScreenshotRecord>>.Valid(images);
            case HandedIn<IReadOnlyList<ListedScreenshot>>.Invalid(var invalid):
                return new HandedIn<IReadOnlyList<TaskScreenshotRecord>>.Invalid(invalid);
            default:
                return new HandedIn<IReadOnlyList<TaskScreenshotRecord>>.Nothing();
        }
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
        catch (SandboxFileUnreadableException)
        {
            return (null, $"{named} is there, but can't be read.");
        }
        if (content is null)
        {
            return (null, $"{named} isn't there: there is no such file in {ScreenshotHandIn.Directory}.");
        }
        if (ScreenshotImageFormats.ContentTypeOf(content) is not { } contentType)
        {
            return (null, $"{named} isn't a PNG, JPEG, GIF or WebP image.");
        }
        // The Sandbox holds the GitHub token, so a file can hold it; a secret never goes into PostgreSQL.
        await sandboxes.WithoutSecretsAsync("", cancellationToken);
        return sandboxes.SecretsGiven.Any(secret => secret.Length > 0 && content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0)
            ? (null, $"{named} holds a secret, such as a token, so it isn't kept.")
            : (new ScreenshotImage(contentType, content), null);
    }

    private async Task<VerificationStep> InvalidAsync(IReadOnlyList<string> errors, CancellationToken cancellationToken) =>
        // They quote what the agent wrote.
        VerificationStep.OfScreenshots(
            "The screenshots handed in aren't valid.",
            await sandboxes.WithoutSecretsAsync(string.Join("\n", errors.Select(error => $"- {error}")), cancellationToken));

    private async Task ReplaceAsync(TaskKey task, IReadOnlyList<TaskScreenshotRecord> screenshots, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.TaskScreenshots
            .Where(screenshot => screenshot.IntentId == task.IntentId && screenshot.TaskNumber == task.Number)
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
