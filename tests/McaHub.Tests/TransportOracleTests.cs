using System.Net;

namespace McaHub.Tests;

/// <summary>
/// The private-repo existence oracle (#1): on the write path, a private repo owned by another account
/// must be indistinguishable from one that does not exist — 404, never 403. A 403 would confirm the
/// world exists, violating the SECURITY.md "private repos return 404, never 403" invariant.
/// </summary>
public class TransportOracleTests
{
    [Fact]
    public async Task Push_to_private_repo_owned_by_another_returns_404_not_403()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string aliceToken = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, aliceToken, "secret");
        await Accounts.SetPrivateAsync(alice, "secret", isPrivate: true);

        HttpClient bob = await Accounts.SignInAsync(f, "bob");
        string bobToken = await Accounts.MintTokenAsync(bob);

        using HttpResponseMessage resp = await Accounts.PushAsync(f, bobToken, "secret");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Push_to_public_repo_owned_by_another_stays_403()
    {
        // Public repos are listed and readable, so their existence is not secret: a 403 here leaks
        // nothing, and the helpful "belongs to another account" message is preferable to a 404.
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string aliceToken = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, aliceToken, "openworld");
        await Accounts.SetPrivateAsync(alice, "openworld", isPrivate: false); // explicitly public (new worlds default private now, #34)

        HttpClient bob = await Accounts.SignInAsync(f, "bob");
        string bobToken = await Accounts.MintTokenAsync(bob);

        using HttpResponseMessage resp = await Accounts.PushAsync(f, bobToken, "openworld");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
