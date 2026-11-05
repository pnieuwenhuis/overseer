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
    public Task<HttpResponseMessage> LogInAsync(string username, string password, string loginPage = "/login") =>
        SubmitFormAsync(loginPage, "login", new Dictionary<string, string>
        {
            ["Username"] = username,
            ["Password"] = password,
        });

    /// <summary>Uses the "Log out" button in the header of the Portal's home page, the way a browser would.</summary>
    public async Task<HttpResponseMessage> LogOutAsync()
    {
        var page = await Http.GetStringAsync("/", TestContext.Current.CancellationToken);
        var logOutForm = Regex.Match(page, "<form[^>]*action=\"logout\"[^>]*>.*?</form>", RegexOptions.Singleline).Value;
        Assert.Contains("Log out", logOutForm);
        var antiforgeryToken = AntiforgeryToken(logOutForm);

        return await Http.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
        }), TestContext.Current.CancellationToken);
    }

    /// <summary>Opens a Portal page and submits one of its forms with the given fields, the way a browser would.</summary>
    /// <param name="formName">The form's <c>@formname</c>.</param>
    /// <param name="fields">The fields to send: a name comes once per value, as with several ticked checkboxes of one name.</param>
    public async Task<HttpResponseMessage> SubmitFormAsync(string page, string formName, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var tab = await OpenAsync(page);
        return await tab.SubmitFormAsync(formName, fields);
    }

    /// <summary>Opens a Portal page in a tab that stays as it is, so that its forms can be submitted after what they show has changed.</summary>
    public async Task<PortalTab> OpenAsync(string page) =>
        new(this, page, AntiforgeryToken(await Http.GetStringAsync(page, TestContext.Current.CancellationToken)));

    /// <summary>Opens the Portal's home page and tells whether Overseer showed it or sent the client to log in.</summary>
    public async Task<bool> IsLoggedInAsync()
    {
        using var response = await Http.GetAsync("/", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return response.RequestMessage!.RequestUri!.AbsolutePath != "/login";
    }

    private static string AntiforgeryToken(string form) =>
        Regex.Match(form, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;

    public void Dispose() => Http.Dispose();
}

/// <summary>A Portal page as a browser tab shows it, however long ago it was opened.</summary>
public sealed class PortalTab(PortalBrowser browser, string page, string antiforgeryToken)
{
    /// <summary>Submits one of the page's forms with the given fields, the way a browser would.</summary>
    /// <param name="formName">The form's <c>@formname</c>.</param>
    /// <param name="fields">The fields to send: a name comes once per value, as with several ticked checkboxes of one name.</param>
    public Task<HttpResponseMessage> SubmitFormAsync(string formName, IEnumerable<KeyValuePair<string, string>> fields) =>
        browser.Http.PostAsync(page, new FormUrlEncodedContent(
        [
            .. fields,
            new("_handler", formName),
            new("__RequestVerificationToken", antiforgeryToken),
        ]), TestContext.Current.CancellationToken);
}
