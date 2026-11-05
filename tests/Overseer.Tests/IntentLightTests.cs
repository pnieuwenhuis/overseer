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

    // The light of the states nothing reaches yet is settled here until their tickets add to it. Implementing and InReview are Active
    // Intents, whose agents are working. AwaitingSpecApproval isn't, and turns orange with the ticket that introduces the Spec.
    [Theory]
    [InlineData(IntentState.Draft, null, null)]
    [InlineData(IntentState.Refining, "Green", "Working")]
    [InlineData(IntentState.AwaitingSpecApproval, null, null)]
    [InlineData(IntentState.Approved, null, null)]
    [InlineData(IntentState.Implementing, "Green", "Working")]
    [InlineData(IntentState.InReview, "Green", "Working")]
    [InlineData(IntentState.Paused, null, null)]
    [InlineData(IntentState.Done, null, null)]
    [InlineData(IntentState.Cancelled, null, null)]
    public async Task An_Intent_shows_the_light_of_its_state_in_the_Backlog_and_on_its_detail_page(IntentState state, string? colour, string? label)
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        using var saved = await SaveAsync(overseer, "Export data", "acme/web");
        var address = await AddressAsync(overseer, "Export data");
        await SetStateAsync(overseer, "Export data", state);
        (string, string)? expected = colour is null ? null : (colour, label!);
        var backlog = await GetAsync(overseer, state is IntentState.Done or IntentState.Cancelled ? "/?filter=closed" : "/");

        Assert.Equal(expected, ShownLight(backlog));
        Assert.Equal(expected, ShownLight(await GetAsync(overseer, address)));
    }

    /// <summary>The colour and label of the light on the page; <c>null</c> when it shows none.</summary>
    private static (string Colour, string Label)? ShownLight(string page) =>
        Regex.Match(page, "<[^>]*class=\"intent-light\"[^>]*data-light=\"([^\"]*)\"[^>]*>(.*?)</", RegexOptions.Singleline) is { Success: true } light
            ? (light.Groups[1].Value, light.Groups[2].Value.Trim())
            : null;
}
