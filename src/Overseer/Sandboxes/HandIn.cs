using System.Text.Json;

namespace Overseer.Sandboxes;

/// <summary>
/// Something structured an agent hands in to Overseer, such as the Analyst's Round or Spec: it writes a file at a fixed path in
/// its Sandbox, outside the repositories, and ends its turn. Overseer then takes the file through the Sandbox Provider and
/// validates it. What isn't valid goes back to the agent with what is wrong, for it to hand in again.
/// </summary>
/// <typeparam name="T">What the file holds once it is valid.</typeparam>
public abstract class HandIn<T>
{
    /// <summary>The absolute path in the Sandbox at which the agent writes it.</summary>
    public abstract string Path { get; }

    /// <summary>What the file holds, or every reason it isn't valid, in words the agent can act on.</summary>
    protected abstract HandedIn<T> Validate(string content);

    /// <summary>
    /// Takes what the agent handed in from the Sandbox, so that the next turn starts without it, and validates it.
    /// </summary>
    public async Task<HandedIn<T>> TakeAsync(SandboxRegistry sandboxes, Sandbox sandbox, CancellationToken cancellationToken) =>
        await sandboxes.TakeFileAsync(sandbox, Path, cancellationToken) is { } content
            ? Validate(content)
            : new HandedIn<T>.Nothing();

    /// <summary>
    /// Parses <paramref name="content"/> as JSON and reads it with <paramref name="read"/>, for a hand-in written as JSON. Malformed JSON
    /// is invalid, with the parser's error.
    /// </summary>
    protected static HandedIn<T> FromJson(string content, Func<JsonElement, HandedIn<T>> read)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return read(document.RootElement);
        }
        catch (JsonException exception)
        {
            return new HandedIn<T>.Invalid([$"The file is not valid JSON: {exception.Message}"]);
        }
    }

    /// <summary>
    /// The text of <paramref name="element"/>'s <paramref name="property"/>, without surrounding whitespace and with LF line breaks;
    /// <c>null</c> when it isn't text, or is blank.
    /// </summary>
    protected static string? TextOf(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString()!.Trim() is { Length: > 0 } text
            ? text.ReplaceLineEndings("\n")
            : null;
}

/// <summary>What an agent handed in at the end of its turn.</summary>
public abstract record HandedIn<T>
{
    private HandedIn()
    {
    }

    /// <summary>It wrote nothing at the path.</summary>
    public sealed record Nothing : HandedIn<T>;

    /// <summary>What it wrote isn't valid, for these reasons.</summary>
    public sealed record Invalid(IReadOnlyList<string> Errors) : HandedIn<T>;

    /// <summary>What it wrote is valid, and holds this.</summary>
    public sealed record Valid(T Value) : HandedIn<T>;
}
