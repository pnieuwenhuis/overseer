using Microsoft.Extensions.DependencyInjection;
using Overseer.Intents;
using static Overseer.Tests.IntentPortal;

namespace Overseer.Tests;

public class IntentChangeAnnouncementTests(PostgresServer postgres)
{
    [Fact]
    public async Task Each_change_to_an_Intent_is_announced_once_when_a_read_already_sees_it_and_a_refused_one_not_at_all()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        var lifecycle = overseer.Services.GetRequiredService<IntentLifecycle>();
        List<string> seenWhenAnnounced = [];
        lifecycle.Changed += () =>
        {
            // On another connection, as a live page reads: it sees only what is committed. Off the thread of the announcing page's
            // renderer, which it blocks while this waits, as the read's continuations would wait for that renderer.
            var backlog = Task.Run(() => lifecycle.ListBacklogAsync(BacklogFilter.Open, TestContext.Current.CancellationToken)).GetAwaiter().GetResult();
            seenWhenAnnounced.Add(string.Join(", ", backlog.Select(intent => $"{intent.FirstLine}: {intent.State}")));
        };

        using var captured = await SaveAsync(overseer, "Export data");
        var address = await AddressAsync(overseer, "Export data");
        using var refused = await overseer.SubmitFormAsync(address, "start-intent", []);
        Assert.Equal(["Export data: Draft"], seenWhenAnnounced);

        using var edited = await EditAsync(overseer, address, "save", "Import data", "acme/web");
        using var started = await overseer.SubmitFormAsync(address, "start-intent", []);
        Assert.Equal(["Export data: Draft", "Import data: Draft", "Import data: Refining"], seenWhenAnnounced);

        using var second = await SaveAsync(overseer, "Delete data");
        using var deleted = await DeleteAsync(overseer, await AddressAsync(overseer, "Delete data"));
        Assert.Equal(["Delete data: Draft, Import data: Refining", "Import data: Refining"], seenWhenAnnounced[3..]);
    }
}
