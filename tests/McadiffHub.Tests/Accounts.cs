using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace McadiffHub.Tests;

/// <summary>
/// Drives the hub's accounts-mode flows over HTTP exactly as a browser + CLI would, so integration
/// tests can build realistic ownership/visibility scenarios without reaching into HubDb internals.
/// Sign-in uses the insecure dev-login (the factory enables it in <see cref="HubMode.Accounts"/>);
/// pushes use a Bearer personal access token, like <c>mcadiff push</c>.
/// </summary>
internal static class Accounts
{
    private const string DummyHash = "0000000000000000000000000000000000000000";
    private static readonly Regex CsrfRe = new("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex TokenRe = new("class=\"token\">([^<]+)<", RegexOptions.Compiled);

    /// <summary>Sign a dev user in and return their cookie-authenticated client.</summary>
    public static async Task<HttpClient> SignInAsync(HubFactory f, string user)
    {
        HttpClient c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        string csrf = Csrf(await GetStringAsync(c, "/auth/dev"));
        HttpResponseMessage resp = await c.PostAsync("/auth/dev", Form(("__RequestVerificationToken", csrf), ("user", user)));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode); // → /account on success
        return c;
    }

    /// <summary>Mint a personal access token (default write scope) for the signed-in user; returns its plaintext.</summary>
    public static async Task<string> MintTokenAsync(HttpClient signedIn, string scope = "write")
    {
        string csrf = Csrf(await GetStringAsync(signedIn, "/account"));
        HttpResponseMessage resp = await signedIn.PostAsync("/account/tokens",
            Form(("__RequestVerificationToken", csrf), ("label", "test"), ("scope", scope)));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Match m = TokenRe.Match(await resp.Content.ReadAsStringAsync());
        Assert.True(m.Success, "fresh token not found in /account response");
        return m.Groups[1].Value;
    }

    /// <summary>Regenerate a token by prefix and return the new plaintext.</summary>
    public static async Task<string> RegenerateAsync(HttpClient signedIn, string prefix)
    {
        string csrf = Csrf(await GetStringAsync(signedIn, "/account"));
        HttpResponseMessage resp = await signedIn.PostAsync("/account/tokens/regenerate",
            Form(("__RequestVerificationToken", csrf), ("prefix", prefix)));
        Match m = TokenRe.Match(await resp.Content.ReadAsStringAsync());
        Assert.True(m.Success, "regenerated token not found");
        return m.Groups[1].Value;
    }

    /// <summary>Trigger "sign out everywhere" for the signed-in user.</summary>
    public static async Task SignOutEverywhereAsync(HttpClient signedIn)
    {
        string csrf = Csrf(await GetStringAsync(signedIn, "/account"));
        await signedIn.PostAsync("/account/sign-out-everywhere", Form(("__RequestVerificationToken", csrf)));
    }

    /// <summary>Owner deletes a world from its page.</summary>
    public static async Task DeleteWorldAsync(HttpClient owner, string repo)
    {
        string csrf = Csrf(await GetStringAsync(owner, $"/r/{repo}"));
        await owner.PostAsync($"/r/{repo}/delete", Form(("__RequestVerificationToken", csrf)));
    }

    /// <summary>Delete the signed-in user's account (GDPR erasure).</summary>
    public static async Task DeleteAccountAsync(HttpClient signedIn)
    {
        string csrf = Csrf(await GetStringAsync(signedIn, "/account"));
        await signedIn.PostAsync("/account/delete", Form(("__RequestVerificationToken", csrf)));
    }

    /// <summary>Acknowledge the age gate for the signed-in user.</summary>
    public static async Task ConfirmAgeAsync(HttpClient signedIn)
    {
        string csrf = Csrf(await GetStringAsync(signedIn, "/auth/age-gate"));
        await signedIn.PostAsync("/auth/age-gate", Form(("__RequestVerificationToken", csrf)));
    }

    /// <summary>Create + claim a repo as the token's owner. The first authenticated write to a new name
    /// auto-creates and claims it (the dummy object is rejected, but ownership is established first).</summary>
    public static async Task CreateRepoAsync(HubFactory f, string ownerToken, string repo)
    {
        using HttpClient c = f.CreateClient();
        await PushAsync(f, ownerToken, repo); // side effect: creates + claims the repo
        // self-check: the owner can now list the repo's refs (proves it exists + is owned)
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/r/{repo}/info/refs");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        HttpResponseMessage refs = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, refs.StatusCode);
    }

    /// <summary>Toggle a repo's visibility as its owner (cookie + CSRF, like the settings form).</summary>
    public static async Task SetPrivateAsync(HttpClient owner, string repo, bool isPrivate)
    {
        string csrf = Csrf(await GetStringAsync(owner, $"/r/{repo}"));
        HttpResponseMessage resp = await owner.PostAsync($"/r/{repo}/settings",
            Form(("__RequestVerificationToken", csrf), ("private", isPrivate ? "on" : "off")));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
    }

    /// <summary>Attempt a transport push (single-object) with a Bearer token; returns the raw response.</summary>
    public static async Task<HttpResponseMessage> PushAsync(HubFactory f, string token, string repo)
    {
        HttpClient c = f.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/r/{repo}/objects/{DummyHash}")
        {
            Content = new ByteArrayContent([1, 2, 3]),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await c.SendAsync(req);
    }

    private static async Task<string> GetStringAsync(HttpClient c, string url) =>
        await (await c.GetAsync(url)).Content.ReadAsStringAsync();

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(x => new KeyValuePair<string, string>(x.Key, x.Value)));

    private static string Csrf(string html)
    {
        Match m = CsrfRe.Match(html);
        Assert.True(m.Success, "antiforgery token not found in page");
        return m.Groups[1].Value;
    }
}
