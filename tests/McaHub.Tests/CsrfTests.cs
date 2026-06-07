using System.Net;

namespace McaHub.Tests;

/// <summary>
/// CSRF protection on cookie-authenticated mutations (#20): a state-changing POST needs a valid
/// antiforgery token for *this* session, the check fires before any mutation, and Bearer transport POSTs
/// are intentionally outside this path (covered elsewhere).
/// </summary>
public class CsrfTests
{
    private static FormUrlEncodedContent Form(params (string, string)[] f) =>
        new(f.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)));

    [Fact]
    public async Task A_post_without_a_token_is_rejected_and_mutates_nothing()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");

        using HttpResponseMessage resp = await alice.PostAsync("/account/tokens", Form(("label", "x"), ("scope", "write")));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string acct = await (await alice.GetAsync("/account")).Content.ReadAsStringAsync();
        Assert.Contains("No tokens yet", acct); // the mutation never happened
    }

    [Fact]
    public async Task A_token_from_another_session_is_rejected()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        HttpClient bob = await Accounts.SignInAsync(f, "bob");
        string bobToken = await Accounts.CsrfTokenAsync(bob, "/account");

        using HttpResponseMessage resp = await alice.PostAsync("/account/tokens",
            Form(("__RequestVerificationToken", bobToken), ("label", "x"), ("scope", "write")));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode); // bob's token doesn't match alice's session
    }

    [Fact]
    public async Task A_valid_token_succeeds()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");

        string token = await Accounts.MintTokenAsync(alice); // posts with a valid same-session token
        Assert.StartsWith("mcahub_", token);
    }
}
