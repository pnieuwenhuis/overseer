using Overseer.Repositories;

namespace Overseer.Tests;

/// <summary>Reading and validating the Repository Manifest, <c>.overseer.yml</c>, from sample files.</summary>
public class RepositoryManifestTests
{
    [Fact]
    public void A_manifest_holds_the_setup_build_and_test_commands_and_the_Compose_file()
    {
        var read = RepositoryManifest.Read("""
            # How Overseer works with this repository.
            setup: npm ci
            build: "npm run build"
            test: npm test -- --reporter=dot
            compose: docker/compose.yml
            """);

        Assert.Equal(
            new RepositoryManifest("npm ci", "npm run build", "npm test -- --reporter=dot", "docker/compose.yml"),
            Manifest(read));
    }

    [Fact]
    public void The_Compose_file_is_optional()
    {
        var read = RepositoryManifest.Read("setup: dotnet restore\nbuild: dotnet build\ntest: dotnet test\n");

        Assert.Null(Manifest(read).Compose);
    }

    [Fact]
    public void A_command_may_span_lines_and_a_file_may_start_with_a_byte_order_mark_and_use_Windows_line_breaks()
    {
        var read = RepositoryManifest.Read("﻿setup: |\r\n  npm ci\r\n  npm run generate\r\nbuild: 'true'\r\ntest: true\r\n");

        // Every scalar is the text of a command, also one that YAML would otherwise take for a boolean.
        Assert.Equal(new RepositoryManifest("npm ci\nnpm run generate", "true", "true", null), Manifest(read));
    }

    [Fact]
    public void A_repository_without_the_file_has_a_missing_manifest_which_is_not_an_invalid_one()
    {
        Assert.IsType<RepositoryManifestReading.Missing>(RepositoryManifest.Read(null));
    }

    [Theory]
    [InlineData("", "It is empty.")]
    [InlineData("# Nothing yet.\n", "It is empty.")]
    [InlineData("- npm ci\n- npm test\n", "It must be a mapping of setup, build, test and, optionally, compose.")]
    [InlineData("npm ci\n", "It must be a mapping of setup, build, test and, optionally, compose.")]
    [InlineData("setup: a\nbuild: b\ntest: c\n---\nsetup: d\n", "It holds more than one YAML document.")]
    public void A_file_that_isnt_one_mapping_is_invalid(string content, string error)
    {
        Assert.Equal([error], Errors(RepositoryManifest.Read(content)));
    }

    [Theory]
    [InlineData("setup: [\n")]
    [InlineData("setup: a\n  build: b\n")]
    [InlineData("setup: 'a\n")]
    public void A_file_that_isnt_YAML_is_invalid_saying_where(string content)
    {
        var error = Assert.Single(Errors(RepositoryManifest.Read(content)));

        Assert.StartsWith("It is not valid YAML, at line ", error);
    }

    [Theory]
    // The YAML parser breaks on these in ways of its own, rather than saying where.
    [InlineData("setup: !!]!")]
    [InlineData("setup: \"\\U00110000\"")]
    public void A_file_the_YAML_parser_breaks_on_is_invalid_too(string content)
    {
        var error = Assert.Single(Errors(RepositoryManifest.Read(content)));

        Assert.StartsWith("It is not valid YAML", error);
    }

    [Fact]
    public void A_file_nested_however_deeply_is_invalid()
    {
        var read = RepositoryManifest.Read("setup: " + new string('[', 100_000));

        Assert.IsType<RepositoryManifestReading.Invalid>(read);
    }

    [Fact]
    public void Every_missing_command_is_named()
    {
        var read = RepositoryManifest.Read("compose: compose.yml\n");

        Assert.Equal(["`setup` is missing.", "`build` is missing.", "`test` is missing."], Errors(read));
    }

    [Theory]
    [InlineData("setup:")]
    [InlineData("setup: ~")]
    [InlineData("setup: null")]
    [InlineData("setup: ''")]
    [InlineData("setup: '   '")]
    public void A_command_without_text_is_invalid(string setup)
    {
        var read = RepositoryManifest.Read($"{setup}\nbuild: make\ntest: make test\n");

        Assert.Equal(["`setup` is empty."], Errors(read));
    }

    [Theory]
    [InlineData("setup: [npm, ci]")]
    [InlineData("setup:\n  run: npm ci")]
    public void A_command_that_isnt_text_is_invalid(string setup)
    {
        var read = RepositoryManifest.Read($"{setup}\nbuild: make\ntest: make test\n");

        Assert.Equal(["`setup` must be text."], Errors(read));
    }

    [Fact]
    public void A_key_the_manifest_doesnt_have_is_invalid_so_that_a_typing_error_isnt_silently_ignored()
    {
        var read = RepositoryManifest.Read("setup: a\nbuild: b\ntest: c\ncompse: compose.yml\n");

        Assert.Equal(["`compse` is not part of a Repository Manifest, which holds setup, build, test and, optionally, compose."], Errors(read));
    }

    [Fact]
    public void A_key_given_twice_is_invalid()
    {
        var read = RepositoryManifest.Read("setup: a\nbuild: b\ntest: c\nsetup: d\n");

        Assert.Equal(["`setup` is given more than once."], Errors(read));
    }

    [Theory]
    [InlineData("compose.yml")]
    [InlineData("./compose.yml")]
    [InlineData("deploy/local/docker-compose.yaml")]
    [InlineData("a..b/compose.yml")]
    public void The_Compose_file_is_a_path_in_the_repository(string path)
    {
        var read = RepositoryManifest.Read($"setup: a\nbuild: b\ntest: c\ncompose: {path}\n");

        Assert.Equal(path, Manifest(read).Compose);
    }

    [Theory]
    [InlineData("/etc/compose.yml")]
    [InlineData("../api/compose.yml")]
    [InlineData("docker/../../compose.yml")]
    [InlineData("\"compose.yml\\nother.yml\"")]
    public void A_Compose_path_that_leaves_the_repository_or_isnt_one_path_is_invalid(string path)
    {
        var read = RepositoryManifest.Read($"setup: a\nbuild: b\ntest: c\ncompose: {path}\n");

        Assert.Equal(["`compose` must be the path of a file in the repository, relative to its root."], Errors(read));
    }

    private static RepositoryManifest Manifest(RepositoryManifestReading read) =>
        read is RepositoryManifestReading.Valid valid ? valid.Manifest : throw new Xunit.Sdk.XunitException($"Not a valid manifest: {read}");

    private static IReadOnlyList<string> Errors(RepositoryManifestReading read) => Assert.IsType<RepositoryManifestReading.Invalid>(read).Errors;
}
