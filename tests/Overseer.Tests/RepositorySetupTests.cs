using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Overseer.GitHub;
using Overseer.Repositories;

using static Overseer.Tests.RepositorySetupPortal;

namespace Overseer.Tests;

public class RepositorySetupTests(PostgresServer postgres)
{
    [Fact]
    public async Task A_Repository_Setup_creates_the_repository_on_GitHub_and_adds_it_to_the_Configured_Repositories()
    {
        await using var overseer = await StartWithTokenAsync();

        using var response = await StartSetupAsync(overseer, "acme", "web");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("acme/web is created on GitHub and added to the Configured Repositories.", page);
        Assert.Equal([new RepositoryName("acme", "web")], overseer.GitHub.CreatedRepositories);
        Assert.Equal(["acme/web"], Listed(page));
    }

    [Fact]
    public async Task The_Repository_Setup_is_kept_with_its_purpose_and_tech_stack_across_a_restart()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var first = await StartWithTokenAsync(connectionString))
        {
            // A browser submits a textarea's line breaks as CRLF.
            using var started = await StartSetupAsync(
                first, "acme", "web", "  The public website of Acme.\r\nIt sells anvils.  ", "Astro and TypeScript.");
        }

        await using var second = await RunningOverseer.StartAsync(connectionString);
        var setups = await second.Services.GetRequiredService<RepositorySetups>().ListAsync(TestContext.Current.CancellationToken);

        var setup = Assert.Single(setups);
        Assert.Equal(new RepositoryName("acme", "web"), setup.Repository);
        Assert.Equal("The public website of Acme.\nIt sells anvils.", setup.Purpose);
        Assert.Equal("Astro and TypeScript.", setup.TechStack);
    }

    // With the fewest Active Intent slots there can be, Repository Setups still start, so Setups are never counted by accident.
    // StartingIntentsTests states the other side: a Repository Setup under way leaves the slot free for an Intent.
    [Fact]
    public async Task Repository_Setups_take_no_Active_Intent_slot()
    {
        await using var overseer = await StartWithTokenAsync();
        using var limited = await overseer.SubmitFormAsync("/settings", "operating-limits", new Dictionary<string, string>
        {
            ["MaxActiveIntents"] = "1",
            ["RefinementIdleTimeout"] = "30",
            ["VerificationAttemptsPerTask"] = "3",
            ["MaxSandboxLifetime"] = "12",
        });
        Assert.Contains("The operating limits are saved.", await limited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var first = await StartSetupAsync(overseer, "acme", "web");
        using var second = await StartSetupAsync(overseer, "acme", "api");

        Assert.Contains("acme/api is created on GitHub", await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, (await overseer.Services.GetRequiredService<RepositorySetups>().ListAsync(TestContext.Current.CancellationToken)).Count);
    }

    [Theory]
    [InlineData("", "web", "A website.", "Astro.")]
    [InlineData("acme", " ", "A website.", "Astro.")]
    [InlineData("acme", "web", "", "Astro.")]
    [InlineData("acme", "web", "A website.", " \n ")]
    public async Task Owner_name_purpose_and_tech_stack_are_all_required(string owner, string name, string purpose, string techStack)
    {
        await using var overseer = await StartWithTokenAsync();

        using var response = await StartSetupAsync(overseer, owner, name, purpose, techStack);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Enter the new repository&#x27;s owner, name, purpose and tech stack.", page);
        Assert.Empty(overseer.GitHub.CreatedRepositories);
        Assert.Empty(Listed(page));
    }

    [Theory]
    [InlineData("-acme", "web", "An owner is up to 39 letters, digits and hyphens, not starting with a hyphen.")]
    [InlineData("acme/corp", "web", "An owner is up to 39 letters, digits and hyphens, not starting with a hyphen.")]
    [InlineData("acme", "web site", "A name is up to 100 letters, digits, hyphens, underscores and dots, but not \".\" or \"..\".")]
    [InlineData("acme", "..", "A name is up to 100 letters, digits, hyphens, underscores and dots, but not \".\" or \"..\".")]
    [InlineData("acme", "web/app", "A name is up to 100 letters, digits, hyphens, underscores and dots, but not \".\" or \"..\".")]
    public async Task An_owner_or_name_GitHub_would_not_accept_is_refused_without_asking_GitHub(string owner, string name, string error)
    {
        await using var overseer = await StartWithTokenAsync();

        using var response = await StartSetupAsync(overseer, owner, name);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(error, WebUtility.HtmlDecode(page));
        Assert.Empty(overseer.GitHub.CreatedRepositories);
        Assert.Empty(Listed(page));
    }

    [Fact]
    public async Task When_the_name_is_taken_GitHubs_error_is_shown_and_nothing_is_registered()
    {
        await using var overseer = await StartWithTokenAsync();
        overseer.GitHub.AddRepository("acme/web");

        using var response = await StartSetupAsync(overseer, "acme", "web", purpose: "The public website.");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("GitHub did not create acme/web: Repository creation failed: name already exists on this account (HTTP 422)", page);
        Assert.Empty(overseer.GitHub.CreatedRepositories);
        Assert.Empty(Listed(page));
        Assert.Empty(await overseer.Services.GetRequiredService<RepositorySetups>().ListAsync(TestContext.Current.CancellationToken));
        // The form keeps what the user entered, to correct it.
        Assert.Contains("value=\"acme\"", page);
        Assert.Contains("The public website.</textarea>", page);
    }

    [Fact]
    public async Task When_the_token_may_not_create_repositories_for_the_owner_GitHubs_error_is_shown_and_nothing_is_registered()
    {
        await using var overseer = await StartWithTokenAsync();
        overseer.GitHub.RefuseRepositoryCreation("acme", "Resource not accessible by personal access token (HTTP 403)");

        using var response = await StartSetupAsync(overseer, "acme", "web");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("GitHub did not create acme/web: Resource not accessible by personal access token (HTTP 403)", page);
        Assert.Empty(overseer.GitHub.CreatedRepositories);
        Assert.Empty(Listed(page));
    }

    [Fact]
    public async Task Without_a_GitHub_token_no_repository_is_created()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await StartSetupAsync(overseer, "acme", "web");

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Set a GitHub token first: Overseer creates the repository on GitHub with it.", page);
        Assert.Empty(overseer.GitHub.CreatedRepositories);
        Assert.Empty(Listed(page));
    }

    private Task<RunningOverseer> StartWithTokenAsync(string? connectionString = null) =>
        RepositorySetupPortal.StartWithTokenAsync(postgres, connectionString);
}
