using System.Net;
using System.Text.RegularExpressions;
using Overseer.Sandboxes;
using static Overseer.Tests.FinalVerificationTests;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;
using static Overseer.Tests.TaskTests;

namespace Overseer.Tests;

/// <summary>
/// Pull requests: once the final verification passes, Overseer opens one through the GitHub Gateway for each repository that changed,
/// and the Intent moves to In Review.
/// </summary>
public class PullRequestTests(PostgresServer postgres)
{
    [Fact]
    public async Task Each_changed_repository_gets_a_pull_request_from_the_Intent_branch_which_the_detail_page_links_to_and_the_others_get_none()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        string page;
        await using (var overseer = await StartWithManifestsAsync(connectionString, ["acme/api", "acme/docs", "acme/web"]))
        {
            overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
            address = await AwaitingApprovalAsync(overseer, "Export data", "acme/api", "acme/docs", "acme/web");
            Changes(overseer, address, "acme/api", "acme/web");

            await ApproveAsync(overseer, address);

            page = await InReviewAsync(overseer, address);
            var branch = BranchOf(address);
            Assert.Equal(
                [
                    new FakePullRequest(
                        Repository("acme/api"), 1, "https://github.com/acme/api/pull/1", branch, "main", "Export data",
                        "Orders can be exported as **CSV**."),
                    new FakePullRequest(
                        Repository("acme/web"), 1, "https://github.com/acme/web/pull/1", branch, "main", "Export data",
                        "Orders can be exported as **CSV**."),
                ],
                overseer.GitHub.PullRequests);
            Assert.Equal(
                [("acme/api #1", "https://github.com/acme/api/pull/1"), ("acme/web #1", "https://github.com/acme/web/pull/1")],
                PullRequestsOn(page));
            Assert.Equal(("Orange", "Needs Attention"), LightOn(page));
            // What was done, and the Spec the review is held against.
            Assert.Equal(["Done"], TasksOn(page).Select(task => task.State));
            Assert.Contains("Orders can be exported as <strong>CSV</strong>.", page);
        }

        // Kept, also over a restart.
        await using var restarted = await StartWithManifestsAsync(connectionString, []);
        var after = await GetAsync(restarted, address);
        Assert.Equal(PullRequestsOn(page), PullRequestsOn(after));
        Assert.Equal(("Orange", "Needs Attention"), LightOn(after));
        Assert.Equal("In Review", ElementText(after, "intent-state"));
    }

    [Fact]
    public async Task An_Intent_that_changed_no_repository_moves_to_In_Review_without_pull_requests()
    {
        await using var overseer = await StartWithManifestsAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"]);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());

        var address = await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        var page = await InReviewAsync(overseer, address);
        Assert.Empty(overseer.GitHub.PullRequests);
        Assert.Empty(PullRequestsOn(page));
        Assert.Equal("No repository changed, so there is no pull request.", ElementText(page, "pull-requests-none"));
    }

    [Fact]
    public async Task A_pull_request_that_cant_be_opened_turns_the_Intent_red_with_GitHubs_error_and_Retry_opens_only_the_missing_ones()
    {
        await using var overseer = await StartWithManifestsAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/api", "acme/web"]);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        var address = await AwaitingApprovalAsync(overseer, "Export data", "acme/api", "acme/web");
        Changes(overseer, address, "acme/api", "acme/web");
        const string refusal = "Resource not accessible by personal access token (HTTP 403)";
        overseer.GitHub.RefusePullRequests("acme/web", refusal);

        await ApproveAsync(overseer, address);

        var failed = await ImplementationTests.FailedAsync(overseer, address);
        Assert.Equal($"The pull request of acme/web could not be opened: {refusal}", failed.Status);
        Assert.Equal(("Red", "Failed"), failed.Light);
        Assert.Equal($"intents/{address["/intents/".Length..]}/implementation/retry", failed.RetryAction);
        var page = await GetAsync(overseer, address);
        Assert.Equal("Implementing", ElementText(page, "intent-state"));
        // The one that was opened.
        Assert.Equal([("acme/api #1", "https://github.com/acme/api/pull/1")], PullRequestsOn(page));
        var opened = Assert.Single(overseer.GitHub.PullRequests);
        var commands = overseer.Sandboxes.CommandsRun.Count;
        var sessions = SquadSessionsStarted(overseer).Count;
        var sandbox = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        overseer.GitHub.AllowPullRequests("acme/web");

        using var retried = await ImplementationTests.RetryAsync(overseer, address);

        page = await InReviewAsync(overseer, address);
        Assert.Equal(
            [("acme/api #1", "https://github.com/acme/api/pull/1"), ("acme/web #1", "https://github.com/acme/web/pull/1")],
            PullRequestsOn(page));
        Assert.Equal([opened.Repository, Repository("acme/web")], overseer.GitHub.PullRequests.Select(open => open.Repository));
        // No Task ran again, nor anything else in the Sandbox.
        Assert.Equal(commands, overseer.Sandboxes.CommandsRun.Count);
        Assert.Equal(sessions, SquadSessionsStarted(overseer).Count);
        Assert.Equal([sandbox], await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task After_a_restart_Retry_opens_the_pull_requests_still_missing_without_a_Sandbox(bool interrupted)
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        await using (var first = await StartWithManifestsAsync(connectionString, ["acme/web"]))
        {
            first.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
            address = await AwaitingApprovalAsync(first, "Export data", "acme/web");
            Changes(first, address, "acme/web");
            if (interrupted)
            {
                first.GitHub.HoldPullRequests(new TaskCompletionSource().Task);
            }
            else
            {
                first.GitHub.RefusePullRequests("acme/web", "Server Error (HTTP 500)");
            }
            await ApproveAsync(first, address);
            if (interrupted)
            {
                await ImplementationTests.InPhaseAsync(first, address, "OpeningPullRequests");
                var shown = ImplementationTests.ImplementationOn(await GetAsync(first, address))!;
                Assert.Equal(("Opening a pull request for each changed repository.", ("Green", "Working")), (shown.Status, shown.Light));
            }
            else
            {
                await ImplementationTests.FailedAsync(first, address);
            }
        }

        await using var second = await StartWithManifestsAsync(connectionString, []);
        // The new fake GitHub knows nothing of the old one's.
        second.GitHub.AddRepository("acme/web");
        second.GitHub.AddBranch("acme/web", BranchOf(address));
        var failed = ImplementationTests.ImplementationOn(await GetAsync(second, address))!;
        Assert.Equal(
            interrupted
                ? "Overseer restarted, which interrupted the implementation. Retry to open the pull requests."
                : "The pull request of acme/web could not be opened: Server Error (HTTP 500)",
            failed.Status);

        using var retried = await ImplementationTests.RetryAsync(second, address);

        var page = await InReviewAsync(second, address);
        Assert.Equal([("acme/web #1", "https://github.com/acme/web/pull/1")], PullRequestsOn(page));
        Assert.Empty(await second.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(second.Sandboxes.CommandsRun);
    }

    [Fact]
    public async Task A_pull_request_that_is_open_for_the_branch_already_is_taken_as_opened()
    {
        await using var overseer = await StartWithManifestsAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"]);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        var address = await AwaitingApprovalAsync(overseer, "Export data", "acme/web");
        Changes(overseer, address, "acme/web");
        // As when Overseer stopped between opening it and recording it.
        await overseer.GitHub.OpenPullRequestAsync(
            Repository("acme/web"), BranchOf(address), "Export data", "Opened before.", TestContext.Current.CancellationToken);

        await ApproveAsync(overseer, address);

        var page = await InReviewAsync(overseer, address);
        Assert.Equal([("acme/web #1", "https://github.com/acme/web/pull/1")], PullRequestsOn(page));
        Assert.Single(overseer.GitHub.PullRequests);
    }

    [Fact]
    public async Task An_Intent_whose_first_line_is_longer_than_GitHub_takes_has_its_pull_requests_title_cut_short()
    {
        await using var overseer = await StartWithManifestsAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"]);
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Developer, DoneAtOnce());
        var text = $"Export {new string('x', 300)}";
        var address = await AwaitingApprovalAsync(overseer, text, "acme/web");
        Changes(overseer, address, "acme/web");

        await ApproveAsync(overseer, address);

        await InReviewAsync(overseer, address);
        Assert.Equal($"{text[..255]}…", Assert.Single(overseer.GitHub.PullRequests).Title);
    }

    private static Task<string> AwaitingApprovalAsync(RunningOverseer overseer, string text, params string[] repositories) =>
        ImplementationTests.AwaitingApprovalAsync(overseer, text, repositories);

    /// <summary>Approves the Intent's Spec and starts its implementation.</summary>
    private static async Task ApproveAsync(RunningOverseer overseer, string address)
    {
        using var approved = await ImplementationTests.ApproveAsync(overseer, address, start: true);
    }

    /// <summary>Has the Intent's work change these repositories: git finds it so in the Sandbox, and GitHub has the branch.</summary>
    private static void Changes(RunningOverseer overseer, string address, params string[] repositories)
    {
        foreach (var repository in repositories)
        {
            overseer.Sandboxes.ScriptCommandsContaining(IntentBranchComparedIn(repository), new CommandResult(0, "src/orders.ts\0"));
            overseer.GitHub.AddBranch(repository, BranchOf(address));
        }
    }

    private static string BranchOf(string address) => $"overseer/intent-{address["/intents/".Length..].Replace("-", "")}";

    private static Overseer.GitHub.RepositoryName Repository(string ownerAndName) => Overseer.GitHub.RepositoryName.Parse(ownerAndName);

    /// <summary>A session whose every turn ends as done at once.</summary>
    private static FakeAgentSession DoneAtOnce() => new(new FakeAgentTurn([], new AgentOutcome.Done()));

    /// <summary>The pull requests the Intent's detail page links to, in its order: what each link says, and where it leads.</summary>
    private static IReadOnlyList<(string Text, string Address)> PullRequestsOn(string page) =>
        [.. Regex.Matches(page, "<a[^>]*class=\"pull-request-link\"[^>]*href=\"([^\"]*)\"[^>]*>(.*?)</a>", RegexOptions.Singleline)
            .Select(link => (WebUtility.HtmlDecode(link.Groups[2].Value).Trim(), WebUtility.HtmlDecode(link.Groups[1].Value)))];
}
