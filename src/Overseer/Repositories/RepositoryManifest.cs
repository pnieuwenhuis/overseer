using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Overseer.Repositories;

/// <summary>
/// The Repository Manifest: a repository's own <c>.overseer.yml</c>, which says how to set it up, build it, test it and start
/// the services its tests need (ADR 0003). Refinement only checks whether a repository has one; implementation reads it
/// (<see cref="Read"/>) from the repository in the Intent's Sandbox.
/// </summary>
/// <param name="Setup">The shell command that prepares a fresh clone, such as installing dependencies, run from the repository's root.</param>
/// <param name="Build">The shell command that builds it, run from the repository's root.</param>
/// <param name="Test">The shell command that tests it, run from the repository's root.</param>
/// <param name="Compose">
/// The path of the Docker Compose file that starts the services the tests need, from the repository's root; <c>null</c> when it
/// has none.
/// </param>
public sealed partial record RepositoryManifest(string Setup, string Build, string Test, string? Compose)
{
    /// <summary>Its path from the repository's root.</summary>
    public const string Path = ".overseer.yml";

    /// <summary>What it holds, in words for an agent that may have to create one.</summary>
    public const string Format =
        "It holds the shell commands `setup` (prepares a fresh clone, such as installing dependencies), `build` and `test`, " +
        "run from the repository's root, and optionally `compose`, the path of a Docker Compose file that starts the services " +
        "the tests need.";

    private const string Keys = "setup, build, test and, optionally, compose";
    private const string NotAMapping = $"It must be a mapping of {Keys}.";

    /// <summary>Reads and validates a Repository Manifest.</summary>
    /// <param name="content">The file's text; <c>null</c> when the repository has no such file.</param>
    public static RepositoryManifestReading Read(string? content)
    {
        if (content is null)
        {
            return new RepositoryManifestReading.Missing();
        }
        try
        {
            return Parse(content.TrimStart('﻿'));
        }
        catch (YamlException exception)
        {
            return Invalid($"It is not valid YAML, at line {exception.Start.Line}, column {exception.Start.Column}: {exception.Message}");
        }
        catch (Exception exception)
        {
            // Some malformed files make the parser fail with whatever it tripped over, such as an ArgumentException.
            return Invalid($"It is not valid YAML: {exception.Message}");
        }
    }

    // Event by event rather than into objects, so that a file nested however deeply is refused without recursing into it.
    private static RepositoryManifestReading Parse(string content)
    {
        var parser = new Parser(new StringReader(content));
        parser.Consume<StreamStart>();
        if (parser.TryConsume<StreamEnd>(out _))
        {
            return Invalid("It is empty.");
        }
        parser.Consume<DocumentStart>();
        if (!parser.TryConsume<MappingStart>(out _))
        {
            return Invalid(NotAMapping);
        }

        var errors = new List<string>();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var anchored = new Dictionary<AnchorName, Scalar>();
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            if (!parser.TryConsume<Scalar>(out var key))
            {
                return Invalid(NotAMapping);
            }
            Scalar? value = null;
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                value = scalar;
                if (!scalar.Anchor.IsEmpty)
                {
                    anchored[scalar.Anchor] = scalar;
                }
            }
            else if (parser.TryConsume<AnchorAlias>(out var alias))
            {
                value = anchored.GetValueOrDefault(alias.Value);
            }
            else
            {
                parser.SkipThisAndNestedEvents();
            }

            if (key.Value is not ("setup" or "build" or "test" or "compose"))
            {
                errors.Add($"`{key.Value}` is not part of a Repository Manifest, which holds {Keys}.");
            }
            else if (!values.TryAdd(key.Value, TextOf(value)))
            {
                errors.Add($"`{key.Value}` is given more than once.");
            }
            else if (value is null)
            {
                errors.Add($"`{key.Value}` must be text.");
            }
            else if (values[key.Value] is null)
            {
                errors.Add($"`{key.Value}` is empty.");
            }
        }
        parser.Consume<DocumentEnd>();
        if (!parser.TryConsume<StreamEnd>(out _))
        {
            return Invalid("It holds more than one YAML document.");
        }

        errors.AddRange(new[] { "setup", "build", "test" }.Where(command => !values.ContainsKey(command)).Select(command => $"`{command}` is missing."));
        var compose = values.GetValueOrDefault("compose");
        if (compose is not null && !IsPathInRepository(compose))
        {
            errors.Add("`compose` must be the path of a file in the repository, relative to its root.");
        }
        return errors.Count > 0
            ? new RepositoryManifestReading.Invalid(errors)
            : new RepositoryManifestReading.Valid(new RepositoryManifest(values["setup"]!, values["build"]!, values["test"]!, compose));
    }

    /// <summary>The scalar's text without surrounding whitespace; <c>null</c> when there is none, as when YAML takes it for null.</summary>
    private static string? TextOf(Scalar? scalar) =>
        scalar is null || scalar is { Style: ScalarStyle.Plain, Value: "~" or "null" or "Null" or "NULL" } || string.IsNullOrWhiteSpace(scalar.Value)
            ? null
            : scalar.Value.Trim();

    private static bool IsPathInRepository(string path) =>
        !path.StartsWith('/') && !path.Any(char.IsControl) && !path.Split('/').Contains("..");

    private static RepositoryManifestReading.Invalid Invalid(string error) => new([error]);

    /// <summary>
    /// Whether <paramref name="text"/> names the manifest's file: <c>.overseer.yml</c>, in that case, as a file name of its own,
    /// not part of another such as <c>.overseer.yml.example</c>.
    /// </summary>
    public static bool IsNamedIn(string? text) => text is not null && FileName().IsMatch(text);

    [GeneratedRegex(@"(?<![\w.-])\.overseer\.yml(?![\w-]|\.\w)")]
    private static partial Regex FileName();
}

/// <summary>What reading a repository's Repository Manifest gave.</summary>
public abstract record RepositoryManifestReading
{
    private RepositoryManifestReading()
    {
    }

    /// <summary>The repository has no Repository Manifest.</summary>
    public sealed record Missing : RepositoryManifestReading;

    /// <summary>The file isn't a Repository Manifest, for these reasons.</summary>
    public sealed record Invalid(IReadOnlyList<string> Errors) : RepositoryManifestReading;

    /// <summary>The repository's Repository Manifest.</summary>
    public sealed record Valid(RepositoryManifest Manifest) : RepositoryManifestReading;
}
