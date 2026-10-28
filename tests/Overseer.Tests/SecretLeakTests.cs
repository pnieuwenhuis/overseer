using Microsoft.Extensions.DependencyInjection;
using Overseer.Secrets;

namespace Overseer.Tests;

public class SecretLeakTests(PostgresServer postgres)
{
    private const string Value = "github_pat_11ABCDEFG0123456789";

    // No application seam can show what a database or a log does not hold, so this reads both directly.
    [Fact]
    public async Task A_secret_value_reaches_neither_the_database_nor_the_logs()
    {
        var logs = new CapturedLogs();
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync(), new Dictionary<string, string?>
        {
            ["OVERSEER_LOGGING__LOGLEVEL__DEFAULT"] = "Trace",
            ["OVERSEER_LOGGING__LOGLEVEL__MICROSOFT.ASPNETCORE"] = "Trace",
            ["OVERSEER_LOGGING__LOGLEVEL__MICROSOFT.ENTITYFRAMEWORKCORE"] = "Trace",
        }, logs);
        await overseer.LogInAsync();
        var store = overseer.Services.GetRequiredService<ISecretStore>();
        var reader = overseer.Services.GetRequiredService<ISecretReader>();

        await store.SetAsync("github-token", "github_pat_old", TestContext.Current.CancellationToken);
        await store.SetAsync("github-token", Value, TestContext.Current.CancellationToken);
        Assert.True(await store.IsSetAsync("github-token", TestContext.Current.CancellationToken));
        Assert.Equal(Value, await reader.ReadAsync("github-token", TestContext.Current.CancellationToken));
        await store.SetAsync("model-provider-token", "sk-provider", TestContext.Current.CancellationToken);
        await store.RemoveAsync("model-provider-token", TestContext.Current.CancellationToken);
        await overseer.IsLoggedInAsync();

        var database = string.Join("\n", await overseer.DumpDatabaseAsync());
        var logged = string.Join("\n", logs.Entries);
        Assert.Contains("<MigrationId>", database);
        Assert.Contains("github-token", logged);
        Assert.Contains("model-provider-token", logged);
        foreach (var value in new[] { Value, "github_pat_old", "sk-provider" })
        {
            Assert.DoesNotContain(value, database);
            Assert.DoesNotContain(value, logged);
        }
    }
}
