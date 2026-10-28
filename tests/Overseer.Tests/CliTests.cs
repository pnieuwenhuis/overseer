using Overseer.Cli;

namespace Overseer.Tests;

public class CliTests
{
    [Fact]
    public async Task Without_a_command_overseer_lists_its_commands()
    {
        var (_, output) = await Run();

        Assert.Contains("serve", output);
        Assert.Contains("install", output);
        Assert.Contains("uninstall", output);
        Assert.Contains("reset-password", output);
    }

    [Fact]
    public async Task An_unknown_command_prints_usage_and_fails()
    {
        var (exitCode, output) = await Run("frobnicate");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Usage:", output);
        Assert.Contains("reset-password", output);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("reset-password")]
    public async Task A_command_that_is_not_built_yet_says_so_and_fails(string command)
    {
        var (exitCode, output) = await Run(command);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("not implemented yet", output);
    }

    [Fact]
    public async Task Version_prints_the_version_stamped_into_the_executable()
    {
        var (exitCode, output) = await Run("--version");

        Assert.Equal(0, exitCode);
        Assert.Equal(OverseerVersion.Current, output.Trim());
    }

    [Fact]
    public async Task A_local_build_reports_the_development_version()
    {
        var (_, output) = await Run("--version");

        Assert.Equal("0.0.0-dev", output.Trim());
    }

    private static async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var output = new StringWriter();
        var exitCode = await OverseerCli.InvokeAsync(args, output, output);
        return (exitCode, output.ToString());
    }
}
