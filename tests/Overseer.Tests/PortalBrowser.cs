using System.Net;
using System.Text.RegularExpressions;

namespace Overseer.Tests;

/// <summary>A browser-like client for a Portal: it follows redirects and keeps cookies, so it stays logged in.</summary>
public sealed class PortalBrowser : IDisposable
{
    public PortalBrowser(Uri portal)
    {
        Http = new HttpClient(new HttpClientHandler { CookieContainer = Cookies }) { BaseAddress = portal };
    }

    public HttpClient Http { get; }

    /// <summary>The client's cookies; copy them to another browser to keep a session across a restart.</summary>
    public CookieContainer Cookies { get; } = new();

    /// <summary>Fills in and submits the login form, the way a browser would.</summary>
    /// <param name="loginPage">The login page's address, including the query Overseer redirected with.</param>
    public async Task<HttpResponseMessage> LogInAsync(string username, string password, string loginPage = "/login")
    {
        var form = await Http.GetStringAsync(loginPage, TestContext.Current.CancellationToken);
        var antiforgeryToken = Regex.Match(form, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;

        return await Http.PostAsync(loginPage, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["__RequestVerificationToken"] = antiforgeryToken,
            ["Username"] = username,
            ["Password"] = password,
        }), TestContext.Current.CancellationToken);
    }

    /// <summary>Opens the Portal's home page and tells whether Overseer showed it or sent the client to log in.</summary>
    public async Task<bool> IsLoggedInAsync()
    {
        using var response = await Http.GetAsync("/", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return response.RequestMessage!.RequestUri!.AbsolutePath != "/login";
    }

    public void Dispose() => Http.Dispose();
}
