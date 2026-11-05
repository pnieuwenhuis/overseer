using Microsoft.Playwright;

namespace Overseer.Tests;

/// <summary>
/// A real headless Chromium, for what only a browser shows: the Portal's live updates over Blazor's SignalR connection. It installs
/// Chromium the first time, into Playwright's cache in the user's home; a later run finds it there and is done at once.
/// </summary>
public sealed class PortalChromium : IAsyncLifetime
{
    /// <summary>
    /// The environment of an Overseer a browser opens. Only Development finds the Portal's scripts and styles in a build's output, through
    /// the static web assets manifest; a publish has them in its wwwroot.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> ServerEnvironment = new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = "Development" };

    private IPlaywright _playwright = default!;
    private IBrowser _browser = default!;

    public async ValueTask InitializeAsync()
    {
        var installed = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        Assert.True(installed == 0, $"Installing Chromium for Playwright exited with {installed}.");

        // What a test waits for arrives within moments, but a full test run keeps the machine busy.
        Assertions.SetDefaultExpectTimeout(30_000);
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
    }

    /// <summary>A new browser session, logged in to this Overseer as its user. Its pages are tabs that share the login.</summary>
    public async Task<IBrowserContext> LogInAsync(RunningOverseer overseer)
    {
        var session = await _browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = overseer.Http.BaseAddress!.ToString() });
        var tab = await session.NewPageAsync();
        await tab.GotoAsync("/login");
        await tab.GetByLabel("Username").FillAsync(RunningOverseer.Username);
        await tab.GetByLabel("Password").FillAsync(RunningOverseer.Password);
        await tab.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in" }).ClickAsync();
        await tab.WaitForURLAsync("/");
        await tab.CloseAsync();
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }
}
