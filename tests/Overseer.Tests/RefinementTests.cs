using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Overseer.Sandboxes;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;

namespace Overseer.Tests;

/// <summary>Refinement: starting an Intent launches the Analyst in a fresh Sandbox, which the Intent's detail page follows.</summary>
public class RefinementTests(PostgresServer postgres)
{
    [Fact]
    public async Task Starting_an_Intent_creates_a_Sandbox_it_owns_holding_exactly_its_Repositories_in_scope_on_the_machine_chosen_in_Settings()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api", "acme/docs");
        using var machine = await overseer.SubmitFormAsync("/settings", "sandbox-machine", new Dictionary<string, string>
        {
            ["SandboxVmSize"] = "Standard_D2_v5",
            ["SandboxSpot"] = "true",
        });
        using var saved = await SaveAsync(overseer, "Export data", "acme/web", "acme/api");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var sandbox = await EventuallyAsync(async () => (await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken)).SingleOrDefault(), "the Sandbox");
        Assert.Equal(new SandboxOwner(SandboxOwnerKind.Intent, IdOf(address)), sandbox.Owner);
        Assert.Equal(["acme/api", "acme/web"], overseer.Sandboxes.RepositoriesInScopeOf(sandbox).Order());
        Assert.Equal(new SandboxMachine("Standard_D2_v5", Spot: true), overseer.Sandboxes.MachineOf(sandbox));
    }

    [Fact]
    public async Task Once_the_Sandbox_is_ready_an_Analyst_session_starts_in_it_told_the_Intents_text_its_Repositories_in_scope_and_what_it_may_do()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web", "acme/api");
        using var saved = await SaveAsync(overseer, "# Export data\n\nAs CSV, from the *orders* page.", "acme/web", "acme/api");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var sent = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.MessagesSent.SingleOrDefault()), "the Analyst's first message");
        var session = Assert.Single(overseer.Sandboxes.AgentSessionsStarted);
        Assert.Equal((AgentRole.Analyst, session.Sandbox), (sent.Role, sent.Sandbox));
        Assert.Equal(new SandboxOwner(SandboxOwnerKind.Intent, IdOf(address)), sent.Sandbox.Owner);
        Assert.Contains("# Export data\n\nAs CSV, from the *orders* page.", sent.Message);
        Assert.Contains("- acme/api (https://github.com/acme/api)\n- acme/web (https://github.com/acme/web)", sent.Message);
        // The web over HTTP only: no search service comes with the Sandbox.
        Assert.Contains("Fetch web pages over HTTP", sent.Message);
        Assert.DoesNotContain("search the web", sent.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Read the code", sent.Message);
        Assert.Contains("Build them and run their tests", sent.Message);
        Assert.Contains("What you must never do: push.", sent.Message);
    }

    [Fact]
    public async Task The_Analyst_gets_the_current_agent_harness_configuration_and_model_provider_tokens()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        const string harness = """
            {
              "agent": {
                "analyst": { "model": "gateway/analyst-model" },
                "designer": { "model": "gateway/sonnet" },
                "developer": { "model": "gateway/sonnet" },
                "reviewer": { "model": "gateway/opus" }
              },
              "provider": { "gateway": { "options": { "apiKey": "{env:GATEWAY_API_KEY}" } } }
            }
            """;
        using var savedHarness = await overseer.SubmitFormAsync("/settings", "agent-harness", new Dictionary<string, string> { ["AgentHarness"] = harness });
        using var savedToken = await overseer.SubmitFormAsync("/settings", "provider-token", new Dictionary<string, string>
        {
            ["ProviderTokenName"] = "gateway",
            ["ProviderToken"] = "gw-secret-123",
        });
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");

        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Export data"));

        var session = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.AgentSessionsStarted.SingleOrDefault()), "the Analyst's session");
        Assert.Equal((AgentRole.Analyst, harness, "gateway/analyst-model"), (session.Role, session.HarnessJson, session.Model));
        Assert.Equal("gw-secret-123", session.Environment["GATEWAY_API_KEY"]);
    }

    [Fact]
    public async Task By_default_the_Analyst_may_fetch_web_pages_and_use_the_shell_without_asking_but_git_push_is_denied()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");

        using var started = await StartAsync(overseer, await AddressAsync(overseer, "Export data"));

        var session = await EventuallyAsync(() => Task.FromResult(overseer.Sandboxes.AgentSessionsStarted.SingleOrDefault()), "the Analyst's session");
        var configuration = JsonNode.Parse(session.HarnessJson)!;
        // OpenCode applies the last rule that matches a command, so these override the "*" before them.
        var bash = configuration["agent"]!["analyst"]!["permission"]!["bash"]!.AsObject();
        Assert.Equal(
            [("*", "allow"), ("git push", "deny"), ("git push *", "deny")],
            bash.Select(rule => (rule.Key, rule.Value!.GetValue<string>())));
        // Allowed, rather than asked about: no one is there to answer.
        var permissions = OpenCodePermissions.Of(session.HarnessJson, "analyst");
        Assert.Equal("allow", permissions.Evaluate("webfetch", "https://learn.microsoft.com/en-us/dotnet/"));
        Assert.Equal("allow", permissions.Evaluate("bash", "curl -fsSL https://learn.microsoft.com/en-us/dotnet/"));
        Assert.Equal("allow", permissions.Evaluate("bash", "git status"));
        Assert.Equal("deny", permissions.Evaluate("bash", "git push"));
        Assert.Equal("deny", permissions.Evaluate("bash", "git push origin main"));
    }

    [Fact]
    public async Task The_detail_page_shows_the_Sandbox_being_provisioned_and_then_the_Analyst_working_with_a_green_light_throughout()
    {
        var time = new FakeTimeProvider();
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        overseer.Sandboxes.ProvisioningDelay = TimeSpan.FromMinutes(4);
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var provisioning = RefinementOn(await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            new ShownRefinement("ProvisioningSandbox", "Provisioning a Sandbox for the Analyst. A new Sandbox takes a few minutes.", ("Green", "Working"), null),
            provisioning);
        var working = await EventuallyAsync(async () =>
        {
            // Until the fake has started waiting, advancing the clock doesn't end its wait, so it is advanced on each look.
            time.Advance(TimeSpan.FromMinutes(4));
            return RefinementOn(await GetAsync(overseer, address)) is { Phase: "AnalystWorking" } shown ? shown : null;
        }, "the Analyst to start working");
        Assert.Equal(new ShownRefinement("AnalystWorking", "The Analyst is working on the Intent.", ("Green", "Working"), null), working);
    }

    [Fact]
    public async Task When_the_Sandbox_cant_be_created_the_Intent_stays_Refining_in_its_slot_red_with_the_providers_error_until_a_Retry_succeeds()
    {
        const string quota = "Operation could not be completed as it results in exceeding approved standardDSv5Family Cores quota.";
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        await SetMaxActiveIntentsAsync(overseer, 1);
        overseer.Sandboxes.FailNextCreation(quota);
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        using var other = await SaveAsync(overseer, "Import data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var failed = await FailedAsync(overseer, address);
        Assert.Equal(new ShownRefinement("Failed", $"The Sandbox could not be created: {quota}", ("Red", "Failed"), $"intents/{IdOf(address)}/retry"), failed);
        var page = await GetAsync(overseer, address);
        Assert.Equal("Refining", ElementText(page, "intent-state"));
        Assert.Empty(overseer.Sandboxes.AgentSessionsStarted);
        var backlog = await GetAsync(overseer, "/");
        Assert.Matches("<span class=\"intent-light\" data-light=\"Red\">\\s*Failed\\s*</span>", backlog);
        Assert.Equal("All Active Intent slots are taken, by \"Export data\". You can raise the maximum in Settings.", Alert(await GetAsync(overseer, await AddressAsync(overseer, "Import data"))));

        using var retried = await RetryAsync(overseer, address);

        Assert.Equal(address, retried.RequestMessage!.RequestUri!.AbsolutePath);
        var working = await EventuallyAsync(async () => RefinementOn(await GetAsync(overseer, address)) is { Phase: "AnalystWorking" } shown ? shown : null, "the Analyst to start working");
        Assert.Equal(new ShownRefinement("AnalystWorking", "The Analyst is working on the Intent.", ("Green", "Working"), null), working);
        var sandbox = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new SandboxOwner(SandboxOwnerKind.Intent, IdOf(address)), sandbox.Owner);
    }

    [Fact]
    public async Task When_the_Analysts_session_fails_the_Intent_turns_red_with_its_error_and_Retry_starts_a_new_session_in_a_fresh_Sandbox()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst,
            new FakeAgentSession(new FakeAgentTurn(["Reading the code"], new AgentOutcome.Done()) { Failure = "opencode exited with code 1." }),
            new FakeAgentSession(FakeAgentTurn.Endless("Reading the code")));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var failed = await FailedAsync(overseer, address);
        Assert.Equal(("The Analyst's session failed: opencode exited with code 1.", ("Red", "Failed")), (failed.Status, failed.Light));
        Assert.Equal("Refining", ElementText(await GetAsync(overseer, address), "intent-state"));
        await NoSandboxLeftAsync(overseer);

        using var retried = await RetryAsync(overseer, address);

        var working = await EventuallyAsync(async () => RefinementOn(await GetAsync(overseer, address)) is { Phase: "AnalystWorking" } shown ? shown : null, "the Analyst to start working");
        Assert.Equal(("Green", "Working"), working.Light);
        Assert.Equal(2, overseer.Sandboxes.AgentSessionsStarted.Count);
        var (first, second) = (overseer.Sandboxes.AgentSessionsStarted[0].Sandbox, overseer.Sandboxes.AgentSessionsStarted[1].Sandbox);
        Assert.NotEqual(first, second);
        Assert.Equal([second], await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AgentRole.Analyst, overseer.Sandboxes.AgentSessionsStarted[1].Role);
        Assert.Contains("Export data", overseer.Sandboxes.MessagesSent[1].Message);
    }

    [Fact]
    public async Task When_the_Analysts_session_cant_be_started_the_Intent_turns_red_with_the_error_as_a_failed_session()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession() { StartFailure = "opencode is not installed." });
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var failed = await FailedAsync(overseer, address);
        Assert.Equal(("The Analyst's session failed: opencode is not installed.", ("Red", "Failed")), (failed.Status, failed.Light));
        Assert.NotNull(failed.RetryAction);
        await NoSandboxLeftAsync(overseer);
    }

    // Until the Analyst can hand in a Round or a Spec, nothing its turn ends with is something Overseer can act on.
    [Theory]
    [InlineData(false, "The Analyst's session failed: it ended its turn without a Round or a Spec, which Overseer can't act on.")]
    [InlineData(true, "The Analyst's session failed: it asked a question outside a Round, which Overseer can't act on: Which format?")]
    public async Task A_turn_ending_with_an_outcome_Overseer_cant_act_on_is_a_failed_session(bool askedQuestion, string error)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(
            new FakeAgentTurn([], askedQuestion ? new AgentOutcome.AskedQuestion("Which format?") : new AgentOutcome.Done())));
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        var failed = await FailedAsync(overseer, address);
        Assert.Equal((error, ("Red", "Failed")), (failed.Status, failed.Light));
        Assert.NotNull(failed.RetryAction);
        await NoSandboxLeftAsync(overseer);
    }

    [Fact]
    public async Task Stopping_serve_destroys_the_Sandbox_and_after_a_restart_the_Refinement_is_shown_failed_and_can_be_retried()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        FakeSandboxProvider firstSandboxes;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            firstSandboxes = first.Sandboxes;
            using var saved = await SaveAsync(first, "Export data", "acme/web");
            address = await AddressAsync(first, "Export data");
            using var started = await StartAsync(first, address);
            await EventuallyAsync(async () => RefinementOn(await GetAsync(first, address)) is { Phase: "AnalystWorking" } shown ? shown : null, "the Analyst to start working");
        }
        Assert.Empty(await firstSandboxes.ListAsync(TestContext.Current.CancellationToken));

        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        var interrupted = RefinementOn(await GetAsync(second, address));
        Assert.Equal(
            new ShownRefinement("Failed", "Overseer restarted while the Analyst was working. Retry to start the Analyst again.", ("Red", "Failed"), $"intents/{IdOf(address)}/retry"),
            interrupted);
        using var retried = await RetryAsync(second, address);
        await EventuallyAsync(async () => RefinementOn(await GetAsync(second, address)) is { Phase: "AnalystWorking" } shown ? shown : null, "the Analyst to start working");
    }

    [Fact]
    public async Task A_Sandbox_is_recorded_for_its_Intent_before_it_is_provisioned_so_a_crash_meanwhile_leaves_nothing_unrecorded()
    {
        var time = new FakeTimeProvider();
        await using var overseer = await StartWithRepositoriesAsync(await postgres.CreateEmptyDatabaseAsync(), ["acme/web"], time);
        overseer.Sandboxes.ProvisioningDelay = TimeSpan.FromMinutes(4);
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");

        using var started = await StartAsync(overseer, address);

        // The clock never moves, so the Sandbox is still being provisioned.
        var recorded = await EventuallyAsync(async () =>
        {
            await using var database = overseer.OpenDatabase();
            return await database.Sandboxes.SingleOrDefaultAsync(TestContext.Current.CancellationToken);
        }, "the Sandbox to be recorded");
        Assert.Equal((SandboxOwnerKind.Intent, IdOf(address), null), (recorded.OwnerKind, recorded.OwnerId, recorded.DestroyedAt));
        Assert.Equal("ProvisioningSandbox", RefinementOn(await GetAsync(overseer, address))!.Phase);
    }

    [Fact]
    public async Task After_a_restart_during_provisioning_the_Refinement_is_shown_failed_and_the_Sandbox_it_was_provisioning_is_destroyed()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"], new FakeTimeProvider()))
        {
            first.Sandboxes.ProvisioningDelay = TimeSpan.FromMinutes(4);
            using var saved = await SaveAsync(first, "Export data", "acme/web");
            address = await AddressAsync(first, "Export data");
            using var started = await StartAsync(first, address);
            await EventuallyAsync(async () =>
            {
                await using var database = first.OpenDatabase();
                return await database.Sandboxes.AnyAsync(TestContext.Current.CancellationToken) ? "" : null;
            }, "the Sandbox to be recorded");
        }

        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        Assert.Equal(
            new ShownRefinement(
                "Failed", "Overseer restarted while a Sandbox was being provisioned for the Analyst. Retry to start the Analyst again.",
                ("Red", "Failed"), $"intents/{IdOf(address)}/retry"),
            RefinementOn(await GetAsync(second, address)));
        await NoSandboxLeftAsync(second);
    }

    [Fact]
    public async Task After_a_restart_the_Portal_answers_no_request_until_the_interrupted_Refinements_are_marked_failed()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            using var saved = await SaveAsync(first, "Export data", "acme/web");
            address = await AddressAsync(first, "Export data");
            using var started = await StartAsync(first, address);
            await EventuallyAsync(async () => RefinementOn(await GetAsync(first, address)) is { Phase: "AnalystWorking" } shown ? shown : null, "the Analyst to start working");
        }
        // Holds the Refinement's row, so that marking it failed waits until the test lets go.
        await using var holder = new NpgsqlConnection(connectionString);
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await using var holding = await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var hold = new NpgsqlCommand("""SELECT 1 FROM "Refinements" FOR UPDATE""", holder, holding))
        {
            await hold.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var port = FreePort();

        var starting = RunningOverseer.StartAsync(
            connectionString, new Dictionary<string, string?> { ["OVERSEER_URLS"] = $"http://127.0.0.1:{port}" });

        await EventuallyAsync(async () =>
        {
            // On a connection of its own: within the holding transaction, what PostgreSQL says of its activity doesn't change.
            await using var watcher = new NpgsqlConnection(connectionString);
            await watcher.OpenAsync(TestContext.Current.CancellationToken);
            await using var waiting = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", watcher);
            return (long)(await waiting.ExecuteScalarAsync(TestContext.Current.CancellationToken))! > 0 ? "" : null;
        }, "marking the Refinement failed to wait for its row");
        using (var client = new TcpClient())
        {
            await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken).AsTask());
        }
        await holding.RollbackAsync(TestContext.Current.CancellationToken);
        await using var second = await starting;
        await second.LogInAsync();
        Assert.Equal("Failed", RefinementOn(await GetAsync(second, address))!.Phase);
    }

    [Fact]
    public async Task A_Sandbox_left_over_from_a_crash_is_destroyed_when_serve_starts_again()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            using var saved = await SaveAsync(first, "Export data", "acme/web");
            var address = await AddressAsync(first, "Export data");
            using var started = await StartAsync(first, address);
            await EventuallyAsync(async () => RefinementOn(await GetAsync(first, address)) is { Phase: "AnalystWorking" } shown ? shown : null, "the Analyst to start working");
            // As if serve crashed: the Sandbox outlives it.
            first.Sandboxes.FailNextDestruction("Azure could not be reached.");
        }

        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        await EventuallyAsync(async () =>
        {
            await using var database = second.OpenDatabase();
            return await database.Sandboxes.AnyAsync(sandbox => sandbox.DestroyedAt == null, TestContext.Current.CancellationToken) ? null : "";
        }, "the left-over Sandbox to be destroyed");
    }

    [Fact]
    public async Task A_Sandbox_whose_destruction_fails_while_serve_runs_stays_recorded_and_the_next_start_destroys_it()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            first.Sandboxes.ScriptAgentSessions(AgentRole.Analyst, new FakeAgentSession(new FakeAgentTurn([], new AgentOutcome.Done())));
            first.Sandboxes.FailNextDestruction("Azure could not be reached.");
            using var saved = await SaveAsync(first, "Export data", "acme/web");
            var address = await AddressAsync(first, "Export data");

            using var started = await StartAsync(first, address);

            // The Sandbox is destroyed before the Refinement is marked failed.
            await FailedAsync(first, address);
            await using var database = first.OpenDatabase();
            var recorded = await database.Sandboxes.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Null(recorded.DestroyedAt);
            Assert.Equal([recorded.ResourceId], (await first.Sandboxes.ListAsync(TestContext.Current.CancellationToken)).Select(sandbox => sandbox.ResourceId));
        }

        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        await NoSandboxLeftAsync(second);
    }

    /// <summary>Waits for every Sandbox to be destroyed, and recorded destroyed.</summary>
    private static Task NoSandboxLeftAsync(RunningOverseer overseer) =>
        EventuallyAsync(async () =>
        {
            await using var database = overseer.OpenDatabase();
            var recorded = await database.Sandboxes.AnyAsync(sandbox => sandbox.DestroyedAt == null, TestContext.Current.CancellationToken);
            return recorded || (await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken)).Count > 0 ? null : "";
        }, "every Sandbox to be destroyed");

    /// <summary>Waits for the Intent's detail page to show its Refinement failed.</summary>
    private static Task<ShownRefinement> FailedAsync(RunningOverseer overseer, string address) =>
        EventuallyAsync(async () => RefinementOn(await GetAsync(overseer, address)) is { Phase: "Failed" } shown ? shown : null, "the Refinement to fail");

    /// <summary>Presses the Retry button on the Intent's detail page, as a browser would.</summary>
    private static async Task<HttpResponseMessage> RetryAsync(RunningOverseer overseer, string address)
    {
        var page = await GetAsync(overseer, address);
        var form = Regex.Match(page, "<form[^>]*action=\"([^\"]*retry)\".*?</form>", RegexOptions.Singleline);
        Assert.True(form.Success, $"The detail page offers no Retry: {page}");
        var token = Regex.Match(form.Value, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        return await overseer.Http.PostAsync(
            "/" + WebUtility.HtmlDecode(form.Groups[1].Value),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }),
            TestContext.Current.CancellationToken);
    }

    /// <summary>A local port nothing listens on at the moment.</summary>
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The id of the Intent at this address of its detail page.</summary>
    private static Guid IdOf(string address) => Guid.Parse(address["/intents/".Length..]);

    /// <summary>Where the Intent's Refinement stands as its detail page shows it, with its light; <c>null</c> when the page shows none.</summary>
    private static ShownRefinement? RefinementOn(string page)
    {
        var refinement = Regex.Match(page, "<section class=\"refinement\" data-phase=\"([^\"]+)\"[^>]*>(.*?)</section>", RegexOptions.Singleline);
        if (!refinement.Success)
        {
            return null;
        }
        var light = Regex.Match(page, "<[^>]*class=\"intent-light\"[^>]*data-light=\"([^\"]*)\"[^>]*>(.*?)</", RegexOptions.Singleline);
        var retry = Regex.Match(refinement.Groups[2].Value, "<form[^>]*action=\"([^\"]*retry)\"", RegexOptions.Singleline);
        return new ShownRefinement(
            refinement.Groups[1].Value,
            ElementText(refinement.Groups[2].Value, "refinement-status"),
            light.Success ? (light.Groups[1].Value, light.Groups[2].Value.Trim()) : null,
            retry.Success ? WebUtility.HtmlDecode(retry.Groups[1].Value) : null);
    }

    /// <summary>The Intent's Refinement as its detail page shows it.</summary>
    /// <param name="Phase">ProvisioningSandbox, AnalystWorking or Failed.</param>
    /// <param name="Status">What the page says about it, such as the error it failed with.</param>
    /// <param name="Light">The colour and label of the Intent's light; <c>null</c> when it shows none.</param>
    /// <param name="RetryAction">Where its Retry button posts to; <c>null</c> when it has none.</param>
    private sealed record ShownRefinement(string Phase, string Status, (string Colour, string Label)? Light, string? RetryAction);
}
