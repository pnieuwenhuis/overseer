using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Overseer.Repositories;
using Overseer.Sandboxes;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;
using static Overseer.Tests.SpecTests;

namespace Overseer.Tests;

/// <summary>
/// Refinement makes sure the Squad can verify its work: the Analyst hears which Repositories in scope lack a Repository Manifest, and
/// its Spec must create each one that is missing.
/// </summary>
public class RepositoryManifestRefinementTests(PostgresServer postgres)
{
    [Fact]
    public async Task The_Analysts_first_message_names_the_Repositories_in_scope_without_a_Repository_Manifest_on_their_default_branch()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api", "acme/docs", "acme/ops");
        WithoutManifest(overseer, "acme/api");
        // Not in the Intent's scope, so not named either.
        WithoutManifest(overseer, "acme/docs");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web", "acme/api", "acme/ops");

        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Export data"));

        var instructions = await FirstMessageAsync(overseer);
        Assert.Contains(
            """
            These Repositories in scope have none on their default branch:

            - acme/api

            For each of them, the Spec must have a Task that touches it and creates its `.overseer.yml`, naming that file in its title or description. Overseer doesn't accept a Spec without one.
            """,
            instructions);
        Assert.DoesNotContain("acme/docs", instructions);
        Assert.DoesNotContain("Every Repository in scope has one.", instructions);
    }

    [Fact]
    public async Task The_Analysts_first_message_says_when_every_Repository_in_scope_has_a_Repository_Manifest()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web", "acme/api");

        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Export data"));

        var instructions = await FirstMessageAsync(overseer);
        Assert.Contains("Every Repository in scope has one.", instructions);
        Assert.DoesNotContain("have none", instructions);
    }

    [Fact]
    public async Task Only_a_Repository_Manifest_on_the_default_branch_counts()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        // The manifest is on main, but the default branch is now trunk.
        overseer.GitHub.AddRepository("acme/web", defaultBranch: "trunk");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");

        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Export data"));

        Assert.Contains("have none on their default branch:\n\n- acme/web\n", await FirstMessageAsync(overseer));
    }

    [Fact]
    public async Task Without_a_GitHub_token_Refinement_starts_and_the_Analyst_is_told_to_look_for_the_Repository_Manifests_itself()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        using var removed = await overseer.SubmitFormAsync("/settings", "github-token-remove", new Dictionary<string, string>());

        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Export data"));

        var instructions = await FirstMessageAsync(overseer);
        Assert.Contains(
            """
            Overseer could not check on GitHub whether these Repositories in scope have one:

            - acme/web: Overseer has no GitHub token.

            Look for `.overseer.yml` at the root of each of them in this Sandbox yourself, on its default branch. For each that has none, the Spec must have a Task that touches it and creates its `.overseer.yml`, naming that file in its title or description.
            """,
            instructions);
        Assert.DoesNotContain("Every Repository in scope has one.", instructions);
    }

    [Fact]
    public async Task When_GitHub_fails_Refinement_starts_and_the_Analyst_is_told_the_check_failed_with_GitHubs_error()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        WithoutManifest(overseer, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        overseer.GitHub.RejectToken(Token, "Bad credentials (HTTP 401)");

        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Export data"));

        var instructions = await FirstMessageAsync(overseer);
        Assert.DoesNotContain("have none on their default branch", instructions);
        Assert.Contains(
            """
            Overseer could not check on GitHub whether these Repositories in scope have one:

            - acme/web: GitHub could not say: Bad credentials (HTTP 401)

            Look for `.overseer.yml` at the root of each of them in this Sandbox yourself
            """,
            instructions);
        Assert.Equal("Refining", ElementText(await GetAsync(overseer, await AddressAsync(overseer, "Export data")), "intent-state"));
    }

    [Fact]
    public async Task A_Repository_in_scope_the_GitHub_token_cannot_reach_is_not_taken_to_lack_a_Repository_Manifest()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        // Its access was taken away after it was configured.
        overseer.GitHub.AddRepository("acme/web", reachableBy: "github_pat_11SOMEONEELSE");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");

        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Export data"));

        var instructions = await FirstMessageAsync(overseer);
        Assert.DoesNotContain("have none on their default branch", instructions);
        Assert.Contains("Overseer could not check on GitHub whether these Repositories in scope have one:\n\n- acme/web: GitHub could not say: ", instructions);
    }

    [Fact]
    public async Task The_Analysts_instructions_cover_what_a_Repository_Manifest_holds_and_asking_about_external_test_dependencies()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");

        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Export data"));

        var instructions = await FirstMessageAsync(overseer);
        Assert.Contains(
            "It holds the shell commands `setup` (prepares a fresh clone, such as installing dependencies), `build` and `test`, run from "
            + "the repository's root, and optionally `compose`, the path of a Docker Compose file that starts the services the tests need.",
            instructions);
        Assert.Contains(
            """
            Ask the human, in a Round, whether testing the change needs external dependencies, such as a database, and suggest the answer the code points to.
            When it does, and a Repository in scope that needs them has no Docker Compose setup that starts them, add a Task to the Spec that touches that
            repository, creates a Docker Compose file that starts them, and references it as `compose` in its `.overseer.yml`.
            """,
            instructions);
    }

    [Fact]
    public async Task A_Spec_without_a_Task_creating_each_missing_Repository_Manifest_is_rejected_naming_the_repositories_and_the_Analyst_is_told()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api", "acme/ops");
        WithoutManifest(overseer, "acme/api");
        WithoutManifest(overseer, "acme/ops");
        var spec = Spec();
        // Naming the manifest, but in a Task that touches only a repository that has one.
        spec["tasks"] = new JsonArray(TaskJson("Update .overseer.yml", "Add the export test to `test`.", ["acme/web"]));
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(spec), FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web", "acme/api", "acme/ops");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var rejection = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.ElementAtOrDefault(1)), "the Analyst to be told");
        Assert.StartsWith($"The Spec you wrote to {SpecPath} is not valid", rejection.Message);
        Assert.Contains(
            "- No Task creates the Repository Manifest of acme/api, which has none on its default branch: add a Task that touches it "
            + "and creates its `.overseer.yml`, naming that file in its title or description.\n",
            rejection.Message);
        Assert.Contains("- No Task creates the Repository Manifest of acme/ops, which has none", rejection.Message);
        var page = await GetAsync(overseer, address);
        Assert.Equal("Refining", ElementText(page, "intent-state"));
        Assert.Null(SpecVersionOn(page));
        await using var database = overseer.OpenDatabase();
        Assert.False(await database.SpecVersions.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_Task_naming_another_file_that_contains_the_Repository_Manifests_name_does_not_create_it()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api");
        WithoutManifest(overseer, "acme/api");
        var spec = Spec();
        spec["tasks"]!.AsArray().Add(TaskJson("Add the example settings", "Copy `.overseer.yml.example` and `ci.overseer.yml` from acme/web.", ["acme/api"]));
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(spec), FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web", "acme/api");

        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Export data"));

        var rejection = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.ElementAtOrDefault(1)), "the Analyst to be told");
        Assert.Contains("- No Task creates the Repository Manifest of acme/api", rejection.Message);
    }

    [Fact]
    public async Task A_Spec_with_other_errors_is_rejected_naming_the_missing_Repository_Manifests_in_the_same_rejection()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api");
        WithoutManifest(overseer, "acme/api");
        var spec = SpecWithout("summary");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(spec), FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web", "acme/api");

        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Export data"));

        var rejection = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.ElementAtOrDefault(1)), "the Analyst to be told");
        Assert.Contains("- The Spec has no \"summary\"", rejection.Message);
        Assert.Contains("- No Task creates the Repository Manifest of acme/api", rejection.Message);
    }

    [Fact]
    public async Task A_Task_creating_a_missing_Repository_Manifest_counts_even_when_it_is_rejected_for_another_error()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api");
        WithoutManifest(overseer, "acme/api");
        var spec = Spec();
        var task = TaskJson("Add the Repository Manifest", "Create `.overseer.yml` with `setup`, `build` and `test`.", ["acme/api", "acme/nowhere"]);
        task.Remove("role");
        spec["tasks"]!.AsArray().Add(task);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(spec), FakeAgentTurn.Endless()));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web", "acme/api");

        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Export data"));

        var rejection = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.ElementAtOrDefault(1)), "the Analyst to be told");
        Assert.Contains("- Task 2 has no \"role\"", rejection.Message);
        Assert.Contains("- Task 2 touches acme/nowhere, which isn't a Repository in scope", rejection.Message);
        Assert.DoesNotContain("Repository Manifest of acme/api", rejection.Message);
    }

    [Fact]
    public async Task A_Spec_with_a_Task_creating_each_missing_Repository_Manifest_is_stored()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api", "acme/ops");
        WithoutManifest(overseer, "acme/api");
        WithoutManifest(overseer, "acme/ops");
        var spec = Spec();
        spec["tasks"] = new JsonArray(
            TaskJson("Add the Repository Manifests", "Create `.overseer.yml` with `setup`, `build` and `test`.", ["acme/api", "acme/ops"]),
            TaskJson("Add the export endpoint", "Stream every order as CSV.", ["acme/web"]));
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(spec)));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web", "acme/api", "acme/ops");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var page = await AwaitingSpecApprovalAsync(overseer, address);
        Assert.Equal(1, SpecVersionOn(page));
        Assert.Equal(["Add the Repository Manifests", "Add the export endpoint"], Tasks(page).Select(task => task.Title));
    }

    [Fact]
    public async Task When_Overseer_could_not_check_the_Repository_Manifests_a_Spec_is_not_rejected_for_lacking_one()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        WithoutManifest(overseer, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(HandsInSpec(Spec())));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        using var removed = await overseer.SubmitFormAsync("/settings", "github-token-remove", new Dictionary<string, string>());
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        Assert.Equal(1, SpecVersionOn(await AwaitingSpecApprovalAsync(overseer, address)));
    }

    [Fact]
    public async Task A_Retry_checks_the_Repository_Manifests_again()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        WithoutManifest(overseer, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst,
            new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done()) { Failure = "opencode exited with code 1." }),
            new FakeAgentSession(HandsInSpec(Spec())));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        using var started = await StartAsync(overseer, address);
        await RefinementTests.FailedAsync(overseer, address);
        Assert.Contains("have none on their default branch:\n\n- acme/web\n", overseer.Sandboxes.MessagesSent[0].Message);
        // Someone adds it meanwhile.
        overseer.GitHub.AddFile("acme/web", FakeGitHubGateway.DefaultBranch, RepositoryManifest.Path);

        using var retried = await RefinementTests.RetryAsync(overseer, address);

        await AwaitingSpecApprovalAsync(overseer, address);
        Assert.Contains("Every Repository in scope has one.", overseer.Sandboxes.MessagesSent[1].Message);
    }

    /// <summary>The first message the Analyst got.</summary>
    private static async Task<string> FirstMessageAsync(RunningOverseer overseer) =>
        (await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.FirstOrDefault()), "the Analyst's first message")).Message;
}
