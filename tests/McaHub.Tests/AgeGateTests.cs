using System.Net;

namespace McaHub.Tests;

/// <summary>
/// COPPA-style age gate (#35c): when enabled, a newly signed-in user must confirm they're 13+ (or have
/// parental consent) before any page works; off by default so a school/LAN self-host isn't bothered.
/// </summary>
public class AgeGateTests
{
    [Fact]
    public async Task First_sign_in_is_gated_until_age_is_confirmed()
    {
        using var f = new HubFactory(HubMode.Accounts, settings: [new("MinAgeGate", "1")]);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");

        using HttpResponseMessage before = await alice.GetAsync("/account"); // un-acknowledged → bounced to the gate
        Assert.Equal(HttpStatusCode.Redirect, before.StatusCode);
        Assert.StartsWith("/auth/age-gate", before.Headers.Location?.OriginalString);

        await Accounts.ConfirmAgeAsync(alice);

        using HttpResponseMessage after = await alice.GetAsync("/account"); // acknowledged → through
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Without_the_gate_sign_in_goes_straight_through()
    {
        using var f = new HubFactory(HubMode.Accounts); // gate off by default
        HttpClient alice = await Accounts.SignInAsync(f, "alice");

        using HttpResponseMessage acct = await alice.GetAsync("/account");
        Assert.Equal(HttpStatusCode.OK, acct.StatusCode);
    }
}
