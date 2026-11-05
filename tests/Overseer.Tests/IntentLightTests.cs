using System.Text.RegularExpressions;
using Overseer.Intents;
using static Overseer.Tests.IntentPortal;

namespace Overseer.Tests;

public class IntentLightTests(PostgresServer postgres)
{
    [Fact]
    public async Task A_Draft_has_no_light_and_starting_it_turns_it_green_with_a_label_in_the_Backlog_and_on_its_detail_page()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        Assert.Null(ShownLight(await GetAsync(overseer, "/")));
        Assert.Null(ShownLight(await GetAsync(overseer, address)));

        using var started = await overseer.SubmitFormAsync(address, "start-intent", []);

        // The label says it without the colour.
        Assert.Equal(("Green", "Working"), ShownLight(await GetAsync(overseer, "/")));
        Assert.Equal(("Green", "Working"), ShownLight(await GetAsync(overseer, address)));
    }

    // Only the states in which nothing runs. The tickets that make the others reachable decide their light, and test it.
    [Theory]
    [InlineData(IntentState.Draft)]
    [InlineData(IntentState.Approved)]
    [InlineData(IntentState.Paused)]
    [InlineData(IntentState.Done)]
    [InlineData(IntentState.Cancelled)]
    public async Task An_Intent_in_a_state_in_which_nothing_runs_shows_no_light_in_the_Backlog_or_on_its_detail_page(IntentState state)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        await SetStateAsync(overseer, "Export data", state);
        var backlog = await GetAsync(overseer, state is IntentState.Done or IntentState.Cancelled ? "/?filter=closed" : "/");

        Assert.Equal("Export data", Assert.Single(Backlog(backlog)).FirstLine);
        Assert.Null(ShownLight(backlog));
        Assert.Null(ShownLight(await GetAsync(overseer, address)));
    }

    /// <summary>The colour and label of the light on the page; <c>null</c> when it shows none.</summary>
    private static (string Colour, string Label)? ShownLight(string page) =>
        Regex.Match(page, "<[^>]*class=\"intent-light\"[^>]*data-light=\"([^\"]*)\"[^>]*>(.*?)</", RegexOptions.Singleline) is { Success: true } light
            ? (light.Groups[1].Value, light.Groups[2].Value.Trim())
            : null;
}
