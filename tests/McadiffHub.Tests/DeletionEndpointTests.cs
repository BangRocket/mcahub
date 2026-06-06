using System.Net;

namespace McadiffHub.Tests;

/// <summary>
/// End-to-end deletion (#35a): an owner can delete a world (it's gone for everyone), and deleting your
/// account erases it + invalidates the session (the next request redirects to login).
/// </summary>
public class DeletionEndpointTests
{
    [Fact]
    public async Task An_owner_can_delete_their_world()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, token, "world1");

        await Accounts.DeleteWorldAsync(alice, "world1");

        using HttpResponseMessage gone = await alice.GetAsync("/r/world1"); // the owner herself
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Deleting_your_account_invalidates_the_session()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, token, "world1");

        await Accounts.DeleteAccountAsync(alice);

        using HttpResponseMessage acct = await alice.GetAsync("/account"); // session no longer valid
        Assert.Equal(HttpStatusCode.Redirect, acct.StatusCode);
        Assert.StartsWith("/auth/login", acct.Headers.Location?.OriginalString);
    }
}
