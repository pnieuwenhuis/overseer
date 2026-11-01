using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Overseer.Persistence;

namespace Overseer.Intents;

/// <summary>Owns the Intents' state: everything else asks it to capture or change an Intent and never sets state directly.</summary>
public sealed class IntentLifecycle(IDbContextFactory<OverseerDbContext> databases, TimeProvider time)
{
    /// <summary>Captures what the user wrote as a new Draft.</summary>
    /// <returns>Whether it was captured; <c>false</c> when <paramref name="editorText"/> has nothing but whitespace.</returns>
    public async Task<bool> CaptureAsync(string? editorText, CancellationToken cancellationToken)
    {
        var text = Intent.TextFromEditor(editorText);
        if (text == "")
        {
            return false;
        }

        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        database.Intents.Add(new IntentRecord
        {
            Id = Guid.NewGuid(),
            Text = text,
            State = IntentState.Draft,
            CreatedAt = time.GetUtcNow(),
        });
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>The Backlog: every open Intent.</summary>
    public async Task<IReadOnlyList<Intent>> ListBacklogAsync(CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return await database.Intents
            .OrderByDescending(intent => intent.CreatedAt)
            .Select(intent => new Intent(intent.Text, intent.State))
            .ToListAsync(cancellationToken);
    }
}

/// <summary>An Intent, as the human described it, and how far it has come.</summary>
public sealed partial record Intent(string Text, IntentState State)
{
    /// <summary>
    /// An Intent's text as Overseer keeps it, from what the user wrote: LF line breaks whatever the browser sent, and empty when
    /// there is nothing but whitespace.
    /// </summary>
    public static string TextFromEditor(string? text) => string.IsNullOrWhiteSpace(text) ? "" : text.ReplaceLineEndings("\n");

    /// <summary>
    /// What the Backlog lists the Intent by, since an Intent has no title: the first line that says anything, without Markdown's
    /// heading markers. Text with nothing but heading markers shows its first line as it is.
    /// </summary>
    public string FirstLine =>
        Text.Split('\n').Select(line => AtxHeading().Replace(line, "${heading}").Trim()).FirstOrDefault(line => line != "")
        ?? Text.Trim().Split('\n', 2)[0].Trim();

    // As CommonMark defines an ATX heading: "# C#" is a heading about C#, "## ##" is an empty heading, and "#hashtag" isn't a heading.
    [GeneratedRegex(@"^ {0,3}#{1,6}(?:[ \t]+(?<heading>.*?))??(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex AtxHeading();
}

/// <summary>Where an Intent is in its lifecycle.</summary>
public enum IntentState
{
    Draft,
}

/// <summary>An Intent, as PostgreSQL holds it.</summary>
public sealed class IntentRecord
{
    public required Guid Id { get; init; }

    /// <summary>The Markdown the human wrote, with LF line breaks.</summary>
    public required string Text { get; init; }

    public required IntentState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
