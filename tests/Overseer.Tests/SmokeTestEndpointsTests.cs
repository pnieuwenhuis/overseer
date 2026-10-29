using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Overseer.Sandboxes;
using Overseer.Server;

namespace Overseer.Tests;

/// <summary>
/// What lets the opt-in smoke test have a deployed Overseer create a Sandbox, run a command in it and destroy it, here
/// with the fake Sandbox Provider.
/// </summary>
public class SmokeTestEndpointsTests(PostgresServer postgres)
{
    private static readonly Dictionary<string, string?> Enabled = new() { ["OVERSEER_SMOKETEST__ENABLED"] = "true" };

    [Fact]
    public async Task A_normal_installation_creates_no_Sandbox_over_HTTP()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await overseer.Http.PostAsync("/smoke-test/sandboxes", null, TestContext.Current.CancellationToken);

        // Only the Portal's pages answer, and they refuse a POST without their antiforgery token.
        Assert.False(response.IsSuccessStatusCode);
        Assert.Empty(await Sandboxes(overseer).ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Only_the_logged_in_user_can_create_a_Sandbox()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync(), Enabled);

        using var response = await overseer.Http.PostAsync("/smoke-test/sandboxes", null, TestContext.Current.CancellationToken);

        Assert.Equal("/login", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Empty(await Sandboxes(overseer).ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Enabled_the_logged_in_user_creates_a_Sandbox_runs_a_command_in_it_and_destroys_it()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync(), Enabled);
        await overseer.LogInAsync();
        var fake = (FakeSandboxProvider)Sandboxes(overseer);
        fake.ScriptCommand("echo hello", new CommandResult(0, "hello\n"));

        using var created = await overseer.Http.PostAsync("/smoke-test/sandboxes", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var sandbox = created.Headers.Location!.ToString();
        var ready = await WaitForAsync(overseer, sandbox, "Ready");
        Assert.Equal([ready.ResourceId], (await fake.ListAsync(TestContext.Current.CancellationToken)).Select(listed => listed.ResourceId));

        using var ran = await overseer.Http.PostAsJsonAsync($"{sandbox}/commands", new { Command = "echo hello" }, TestContext.Current.CancellationToken);
        Assert.Equal(new CommandResult(0, "hello\n"), await ran.Content.ReadFromJsonAsync<CommandResult>(TestContext.Current.CancellationToken));

        using var destroyed = await overseer.Http.DeleteAsync(sandbox, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, destroyed.StatusCode);
        await WaitForAsync(overseer, sandbox, "Destroyed");
        Assert.Empty(await fake.ListAsync(TestContext.Current.CancellationToken));
        // Through the Sandbox registry, like any other Sandbox.
        await using var database = overseer.OpenDatabase();
        var record = await database.Sandboxes.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ready.ResourceId, record.ResourceId);
        Assert.NotNull(record.DestroyedAt);
    }

    [Fact]
    public async Task A_Sandbox_that_could_not_be_created_shows_why()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync(), Enabled);
        await overseer.LogInAsync();
        ((FakeSandboxProvider)Sandboxes(overseer)).FailNextCreation("(QuotaExceeded) Not enough standardDSv5Family cores.");

        using var created = await overseer.Http.PostAsync("/smoke-test/sandboxes", null, TestContext.Current.CancellationToken);

        var failed = await WaitForAsync(overseer, created.Headers.Location!.ToString(), "Failed");
        Assert.Contains("(QuotaExceeded) Not enough standardDSv5Family cores.", failed.Error);
    }

    private static ISandboxProvider Sandboxes(RunningOverseer overseer) => overseer.Services.GetRequiredService<ISandboxProvider>();

    private static async Task<SmokeTestSandbox> WaitForAsync(RunningOverseer overseer, string sandbox, string state)
    {
        while (true)
        {
            var status = await overseer.Http.GetFromJsonAsync<SmokeTestSandbox>(sandbox, TestContext.Current.CancellationToken);
            if (status!.State == state || status.State == "Failed")
            {
                Assert.Equal(state, status.State);
                return status;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        }
    }
}
