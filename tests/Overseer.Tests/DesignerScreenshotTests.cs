using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Overseer.Sandboxes;
using static Overseer.Tests.DesignerHandsIn;
using static Overseer.Tests.ImplementationTests;
using static Overseer.Tests.IntentPortal;
using static Overseer.Tests.RepositorySetupPortal;
using static Overseer.Tests.SpecTests;
using static Overseer.Tests.TaskTests;

namespace Overseer.Tests;

/// <summary>
/// Designer screenshots: a Designer Task is done only with screenshots of the design, which its agent hands in outside the
/// repositories and the Portal shows with the Task, so that the human can see a design before Developers build on it.
/// </summary>
public class DesignerScreenshotTests(PostgresServer postgres)
{
    [Fact]
    public async Task A_Designer_session_is_asked_for_working_UI_code_with_placeholder_data_and_for_screenshots_handed_in_outside_the_repositories()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Designer, new FakeAgentSession(FakeAgentTurn.Endless()));

        await ImplementingAsync(overseer, DesignOnly(), "Export data", "acme/web");

        var message = Assert.Single(await SquadMessagesAsync(overseer, 1)).Message;
        Assert.Contains("working UI code", message);
        Assert.Contains("placeholder data", message);
        Assert.Contains("screenshots", message);
        Assert.Contains("headless browser", message);
        Assert.Contains($"{ScreenshotDirectory}/", message);
        Assert.Contains(ListPath, message);
        Assert.Contains("don't commit them", message);
    }

    [Fact]
    public async Task A_Developer_session_is_asked_for_no_screenshots()
    {
        await using var overseer = await StartWithRepositoriesAsync(postgres, "acme/web");

        await ImplementationTests.ImplementingAsync(overseer, "Export data", "acme/web");

        Assert.DoesNotContain("screenshot", Assert.Single(await SquadMessagesAsync(overseer, 1)).Message);
    }

    [Fact]
    public async Task A_Designer_Task_that_hands_in_no_screenshots_is_told_so_each_time_and_is_a_Failed_Task_once_its_attempts_are_used_up()
    {
        await using var overseer = await StartAsync();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Designer, new FakeAgentSession(Done()));

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        var failed = await FailedAsync(overseer, address);
        Assert.Equal(
            "Task 1 is a Failed Task: its work didn't pass verification in 3 attempts. "
            + "In the last one, attempt 3: No screenshots of the design were handed in.",
            failed.Status);
        await StatesAsync(overseer, address, "Failed", "Pending", "Pending");
        var messages = SquadMessages(overseer);
        Assert.Equal(3, messages.Count);
        Assert.Contains("No screenshots of the design were handed in.", messages[1].Message);
        Assert.Contains(ListPath, messages[1].Message);
        Assert.Equal(
            "Attempt 1: failed. No screenshots of the design were handed in.",
            VerificationTests.AttemptsOn(await GetAsync(overseer, address), 1)[0].Heading);
        Assert.DoesNotContain(overseer.Sandboxes.CommandsRun, run => run.Command.Contains("git push"));
    }

    [Fact]
    public async Task A_Designer_Task_that_hands_in_screenshots_is_Done_and_the_Portal_shows_them_with_it_with_their_captions_in_order()
    {
        await using var overseer = await StartAsync();
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Designer,
            new FakeAgentSession(HandsIn(("orders.png", "The orders page, with <Export>", Png), ("empty.jpg", "With no orders yet", Jpeg))));

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        await StatesAsync(overseer, address, "Done", "Running", "Pending");
        foreach (var page in new[] { await GetAsync(overseer, address), await GetAsync(overseer, $"{address}/tasks/1") })
        {
            var shown = ScreenshotsOn(page);
            Assert.Equal(["The orders page, with <Export>", "With no orders yet"], shown.Select(screenshot => screenshot.Caption));
            await AssertServedAsync(overseer, shown[0].Image, "image/png", Png);
            await AssertServedAsync(overseer, shown[1].Image, "image/jpeg", Jpeg);
        }
        // With the Task they are of.
        Assert.Empty(ScreenshotsOn(await GetAsync(overseer, $"{address}/tasks/2")));
        // Taken out of the Sandbox, where they were never in a repository.
        var sandbox = Assert.Single(await overseer.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        foreach (var path in new[] { ListPath, $"{ScreenshotDirectory}/orders.png", $"{ScreenshotDirectory}/empty.jpg" })
        {
            Assert.DoesNotContain($"{Sandbox.RepositoriesDirectory}/", path);
            Assert.Null(await overseer.Sandboxes.ReadFileAsync(sandbox, path, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Images_of_each_kind_are_kept_as_the_encoders_write_them()
    {
        await using var overseer = await StartAsync();
        (string File, string ContentType, byte[] Content)[] images =
        [
            ("base.jpg", "image/jpeg", Jpeg), ("progressive.jpg", "image/jpeg", ProgressiveJpeg),
            ("metadata.jpg", "image/jpeg", JpegWithMetadata), ("still.gif", "image/gif", Gif),
            ("animated.gif", "image/gif", AnimatedGif), ("lossy.webp", "image/webp", WebP),
            ("lossless.webp", "image/webp", LosslessWebP), ("transparent.webp", "image/webp", TransparentWebP),
            ("animated.webp", "image/webp", AnimatedWebP),
        ];
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Designer,
            new FakeAgentSession(HandsIn([.. images.Select(image => (image.File, image.File, image.Content))])));

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        await StatesAsync(overseer, address, "Done", "Running", "Pending");
        var shown = ScreenshotsOn(await GetAsync(overseer, address));
        Assert.Equal(images.Select(image => image.File), shown.Select(screenshot => screenshot.Caption));
        foreach (var (image, screenshot) in images.Zip(shown))
        {
            await AssertServedAsync(overseer, screenshot.Image, image.ContentType, image.Content);
        }
    }

    [Fact]
    public async Task Screenshots_are_still_shown_once_the_Sandbox_is_gone()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        string address;
        await using (var first = await StartWithRepositoriesAsync(connectionString, ["acme/web"]))
        {
            first.Sandboxes.ScriptAgentSessions(AgentRole.Designer, new FakeAgentSession(HandsIn(("orders.png", "The orders page", Png))));
            address = await ImplementingAsync(first, DesignOnly(), "Export data", "acme/web");
            // With nothing left of the implementation for a restart to carry on with.
            await FinalVerificationTests.InReviewAsync(first, address);
        }

        // A restart destroys the Sandbox, and the new fake provider holds none of its files.
        await using var second = await StartWithRepositoriesAsync(connectionString, []);

        Assert.Empty(await second.Sandboxes.ListAsync(TestContext.Current.CancellationToken));
        var shown = Assert.Single(ScreenshotsOn(await GetAsync(second, address)));
        Assert.Equal("The orders page", shown.Caption);
        await AssertServedAsync(second, shown.Image, "image/png", Png);
    }

    [Fact]
    public async Task A_screenshot_is_served_only_to_the_logged_in_user_and_only_as_the_Intents_own()
    {
        await using var overseer = await StartAsync();
        overseer.Sandboxes.ScriptAgentSessions(AgentRole.Designer, new FakeAgentSession(HandsIn(("orders.png", "The orders page", Png))));
        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");
        await StatesAsync(overseer, address, "Done", "Running", "Pending");
        var image = Assert.Single(ScreenshotsOn(await GetAsync(overseer, address))).Image;

        using var elsewhere = await overseer.Http.GetAsync(
            image.Replace(address["/intents/".Length..], Guid.NewGuid().ToString()), TestContext.Current.CancellationToken);
        using var loggedOut = await overseer.LogOutAsync();
        using var anonymous = await overseer.Http.GetAsync(image, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
        Assert.Equal("/login", anonymous.FinalPath());
    }

    [Theory]
    [InlineData("not an image", "Screenshot 1, orders.png, isn't a PNG, JPEG, GIF or WebP image.")]
    [InlineData("an SVG", "Screenshot 1, orders.png, isn't a PNG, JPEG, GIF or WebP image.")]
    [InlineData("a PNG signature alone", "Screenshot 1, orders.png, isn't a PNG, JPEG, GIF or WebP image.")]
    [InlineData("a truncated PNG", "Screenshot 1, orders.png, isn't a PNG, JPEG, GIF or WebP image.")]
    [InlineData("a truncated JPEG", "Screenshot 1, orders.png, isn't a PNG, JPEG, GIF or WebP image.")]
    [InlineData("a GIF header and text", "Screenshot 1, orders.png, isn't a PNG, JPEG, GIF or WebP image.")]
    [InlineData("a truncated GIF", "Screenshot 1, orders.png, isn't a PNG, JPEG, GIF or WebP image.")]
    [InlineData("a truncated WebP", "Screenshot 1, orders.png, isn't a PNG, JPEG, GIF or WebP image.")]
    [InlineData("no such file", "Screenshot 1, orders.png, isn't there: there is no such file in /home/overseer/.overseer/screenshots.")]
    [InlineData("too large", "Screenshot 1, orders.png, is larger than 5 MB.")]
    [InlineData("not JSON", "The file is not valid JSON")]
    [InlineData("no list", "must hold a JSON object whose \"screenshots\" is a list of screenshots.")]
    [InlineData("none listed", "must list at least 1 and at most 20 screenshots, not 0.")]
    [InlineData("a path", "Screenshot 1's \"file\" must be the name of an image in /home/overseer/.overseer/screenshots, without a path: ../orders.png")]
    [InlineData("no caption", "Screenshot 1 has no \"caption\": it must be text that isn't empty.")]
    [InlineData("listed twice", "Screenshot 2 lists orders.png again: list each image once.")]
    public async Task What_isnt_valid_screenshots_uses_an_attempt_and_the_agent_is_told_what_is_wrong_for_it_to_hand_them_in_again(
        string handedIn, string error)
    {
        await using var overseer = await StartAsync();
        const string path = $"{ScreenshotDirectory}/orders.png";
        var one = ListOf(("orders.png", "The orders page"));
        var (list, image) = handedIn switch
        {
            "not an image" => (one, "<html>orders</html>"u8.ToArray()),
            "an SVG" => (one, "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray()),
            "a PNG signature alone" => (one, Png[..8]),
            // Without the chunk that ends it.
            "a truncated PNG" => (one, Png[..^12]),
            "a truncated JPEG" => (one, Jpeg[..^20]),
            "a GIF header and text" => (one, "GIF89a is the format this file says it is."u8.ToArray()),
            "a truncated GIF" => (one, AnimatedGif[..(AnimatedGif.Length / 2)]),
            "a truncated WebP" => (one, WebP[..^8]),
            "no such file" => (one, null),
            "too large" => (one, [.. Png, .. new byte[5 * 1024 * 1024]]),
            "not JSON" => ("orders.png", Png),
            "no list" => ("""{ "screenshots": "orders.png" }""", Png),
            "none listed" => (ListOf(), Png),
            "a path" => (ListOf(("../orders.png", "The orders page")), Png),
            "no caption" => (ListOf(("orders.png", " ")), Png),
            _ => (ListOf(("orders.png", "The orders page"), ("orders.png", "Again")), Png),
        };
        var wrong = Done() with
        {
            Writes = new Dictionary<string, string> { [ListPath] = list },
            BinaryWrites = image is null ? new Dictionary<string, byte[]>() : new Dictionary<string, byte[]> { [path] = image },
        };
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Designer, new FakeAgentSession(wrong, HandsIn(("orders.png", "The orders page, at last", Png))));

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        await StatesAsync(overseer, address, "Done", "Running", "Pending");
        var told = SquadMessages(overseer)[1].Message;
        Assert.Contains("The screenshots handed in aren't valid.", told);
        Assert.Contains(error, told);
        Assert.Contains(ListPath, told);
        var page = await GetAsync(overseer, address);
        Assert.Equal(
            ["Attempt 1: failed. The screenshots handed in aren't valid.", "Attempt 2: passed."],
            VerificationTests.AttemptsOn(page, 1).Select(attempt => attempt.Heading));
        Assert.Contains(error, Assert.Single(VerificationTests.AttemptsOn(page, 1)[0].Steps).Output);
        Assert.Equal("The orders page, at last", Assert.Single(ScreenshotsOn(page)).Caption);
    }

    [Fact]
    public async Task Screenshots_handed_in_with_work_that_failed_verification_still_count_once_it_passes_without_new_ones()
    {
        await using var overseer = await StartWithFailingTestsAsync();
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Designer, new FakeAgentSession(HandsIn(("orders.png", "The orders page", Png)), Done()));

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        await StatesAsync(overseer, address, "Done", "Running", "Pending");
        Assert.DoesNotContain("The screenshots handed in", SquadMessages(overseer)[1].Message);
        var page = await GetAsync(overseer, address);
        Assert.Equal(
            ["Attempt 1: failed. The test command of acme/web failed with exit code 1.", "Attempt 2: passed."],
            VerificationTests.AttemptsOn(page, 1).Select(attempt => attempt.Heading));
        Assert.Equal("The orders page", Assert.Single(ScreenshotsOn(page)).Caption);
    }

    [Fact]
    public async Task Screenshots_handed_in_again_replace_all_the_earlier_ones()
    {
        await using var overseer = await StartWithFailingTestsAsync();
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Designer,
            new FakeAgentSession(
                HandsIn(("orders.png", "The first try", Png), ("empty.png", "With no orders", Png)),
                HandsIn(("orders.png", "The second try", Jpeg))));

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        await StatesAsync(overseer, address, "Done", "Running", "Pending");
        var shown = Assert.Single(ScreenshotsOn(await GetAsync(overseer, address)));
        Assert.Equal("The second try", shown.Caption);
        await AssertServedAsync(overseer, shown.Image, "image/jpeg", Jpeg);
    }

    [Fact]
    public async Task After_screenshots_that_arent_valid_the_earlier_ones_no_longer_count_though_they_stay_shown()
    {
        await using var overseer = await StartWithFailingTestsAsync();
        await SetOperatingLimitsAsync(overseer, verificationAttemptsPerTask: 3);
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Designer,
            new FakeAgentSession(
                HandsIn(("orders.png", "The first try", Png)),
                HandsIn(("orders.png", "Broken", [1, 2, 3])),
                Done()));

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        await FailedAsync(overseer, address);
        var page = await GetAsync(overseer, address);
        Assert.Equal(
            [
                "Attempt 1: failed. The test command of acme/web failed with exit code 1.",
                "Attempt 2: failed. The screenshots handed in aren't valid.",
                "Attempt 3: failed. No screenshots of the design were handed in.",
            ],
            VerificationTests.AttemptsOn(page, 1).Select(attempt => attempt.Heading));
        Assert.Equal("The first try", Assert.Single(ScreenshotsOn(page)).Caption);
    }

    [Fact]
    public async Task A_new_session_hands_in_screenshots_of_its_own_and_the_earlier_ones_stay_until_it_does()
    {
        await using var overseer = await StartWithFailingTestsAsync();
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Designer,
            // Hands in screenshots with work that fails, then breaks off having listed others.
            new FakeAgentSession(
                HandsIn(("orders.png", "The first session's", Png)),
                HandsIn(("orders.png", "Never taken", Png)) with { Failure = "The agent process exited with code 1." }),
            new FakeAgentSession(Done()));
        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");
        await FailedAsync(overseer, address);

        using var retried = await ImplementationTests.RetryAsync(overseer, address);

        // The work passes now, but without screenshots of the new session's own.
        await EventuallyAsync(
            async () => ImplementationOn(await GetAsync(overseer, address)) is { Phase: "Failed", Status: var status }
                && status.StartsWith("Task 1 is a Failed Task", StringComparison.Ordinal) ? status : null,
            "Task 1 to be a Failed Task");
        var page = await GetAsync(overseer, address);
        Assert.Equal(
            "Run 2, attempt 1: failed. No screenshots of the design were handed in.",
            VerificationTests.AttemptsOn(page, 1)[1].Heading);
        Assert.Equal("The first session's", Assert.Single(ScreenshotsOn(page)).Caption);
    }

    [Fact]
    public async Task A_secret_in_what_is_handed_in_is_neither_kept_nor_shown()
    {
        await using var overseer = await StartAsync();
        overseer.Sandboxes.ScriptAgentSessions(
            AgentRole.Designer,
            new FakeAgentSession(
                HandsIn(("orders.png", "The orders page", [.. Png, .. Encoding.UTF8.GetBytes(Token)])),
                HandsIn(("orders.png", $"Taken with {Token}", Png))));

        var address = await ImplementingAsync(overseer, ThreeTasks(), "Export data", "acme/api", "acme/web");

        await StatesAsync(overseer, address, "Done", "Running", "Pending");
        Assert.Contains("Screenshot 1, orders.png, holds a secret", SquadMessages(overseer)[1].Message);
        Assert.Equal("Taken with ***", Assert.Single(ScreenshotsOn(await GetAsync(overseer, address))).Caption);
        Assert.DoesNotContain(await overseer.DumpDatabaseAsync(), table => table.Contains(Token));
    }

    /// <summary>The screenshots a Portal page shows, in its order: each one's caption, and the address of its image.</summary>
    private static IReadOnlyList<(string Caption, string Image)> ScreenshotsOn(string page) =>
        [.. Regex.Matches(page, "<figure class=\"task-screenshot\"[^>]*>(.*?)</figure>", RegexOptions.Singleline).Select(figure =>
        {
            var image = Regex.Match(figure.Groups[1].Value, "<img[^>]*src=\"([^\"]*)\"[^>]*alt=\"([^\"]*)\"");
            var caption = ElementText(figure.Groups[1].Value, "task-screenshot-caption");
            Assert.Equal(caption, WebUtility.HtmlDecode(image.Groups[2].Value));
            return (caption, "/" + WebUtility.HtmlDecode(image.Groups[1].Value));
        })];

    private static async Task AssertServedAsync(RunningOverseer overseer, string address, string contentType, byte[] content)
    {
        using var served = await overseer.Http.GetAsync(address, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(contentType, served.Content.Headers.ContentType?.MediaType);
        // The agent made the file, so the browser mustn't take it for anything else.
        Assert.Equal(["nosniff"], served.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(content, await served.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Overseer with acme/api and acme/web configured.</summary>
    private Task<RunningOverseer> StartAsync() => StartWithRepositoriesAsync(postgres, "acme/api", "acme/web");

    /// <summary>As <see cref="StartAsync"/>, with acme/web's tests failing the first time they run, and passing from then on.</summary>
    private async Task<RunningOverseer> StartWithFailingTestsAsync()
    {
        var overseer = await StartAsync();
        overseer.Sandboxes.AddRepositoryFile("acme/web", ".overseer.yml", "setup: npm ci\nbuild: npm run build\ntest: npm test\n");
        overseer.Sandboxes.ScriptCommandsContaining("npm test", new CommandResult(1, "1 test failed"), new CommandResult(0, ""));
        return overseer;
    }

    /// <summary>A turn that ends as done, with nothing handed in.</summary>
    private static FakeAgentTurn Done() => new([], new AgentOutcome.Done());

    /// <summary>A valid Spec with one Task, the Designer's, in acme/web.</summary>
    private static JsonObject DesignOnly()
    {
        var spec = Spec();
        spec["tasks"] = new JsonArray(TaskJson("Design the Export button", "Place it above the table.", ["acme/web"], role: "Designer"));
        return spec;
    }
}
