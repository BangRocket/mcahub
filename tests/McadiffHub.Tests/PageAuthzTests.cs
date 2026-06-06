using System.Net;

namespace McadiffHub.Tests;

/// <summary>
/// Web-page authorization matrix (#20), the page-side companion to the transport oracle: a private world
/// is **404, not 403** to anyone who can't see it (anonymous or a wrong account), so its existence stays
/// hidden — indistinguishable from a name that was never used.
/// </summary>
public class PageAuthzTests
{
    [Fact]
    public async Task A_private_world_is_404_for_anonymous()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        await Accounts.CreateRepoAsync(f, await Accounts.MintTokenAsync(alice), "secret"); // private by default

        using HttpResponseMessage anon = await f.CreateClient().GetAsync("/r/secret");
        Assert.Equal(HttpStatusCode.NotFound, anon.StatusCode);
    }

    [Fact]
    public async Task A_private_world_is_404_for_a_wrong_account()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        await Accounts.CreateRepoAsync(f, await Accounts.MintTokenAsync(alice), "secret");
        HttpClient bob = await Accounts.SignInAsync(f, "bob");

        using HttpResponseMessage resp = await bob.GetAsync("/r/secret");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode); // not 403 — existence is hidden
    }

    [Fact]
    public async Task An_owner_sees_their_private_world()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        await Accounts.CreateRepoAsync(f, await Accounts.MintTokenAsync(alice), "secret");

        using HttpResponseMessage resp = await alice.GetAsync("/r/secret");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task A_public_world_is_visible_to_anonymous()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        await Accounts.CreateRepoAsync(f, await Accounts.MintTokenAsync(alice), "openworld");
        await Accounts.SetPrivateAsync(alice, "openworld", isPrivate: false);

        using HttpResponseMessage anon = await f.CreateClient().GetAsync("/r/openworld");
        Assert.Equal(HttpStatusCode.OK, anon.StatusCode);
    }

    [Fact]
    public async Task A_nonexistent_world_is_404_too()
    {
        using var f = new HubFactory(HubMode.Accounts);
        using HttpResponseMessage anon = await f.CreateClient().GetAsync("/r/never-existed");
        Assert.Equal(HttpStatusCode.NotFound, anon.StatusCode); // same response as a hidden private world
    }

    [Fact]
    public async Task An_admin_collaborator_cannot_delete_the_owners_world() // audit MED-3
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        await Accounts.CreateRepoAsync(f, await Accounts.MintTokenAsync(alice), "w");
        HttpClient bob = await Accounts.SignInAsync(f, "bob"); // bob must exist before he can be added

        // alice (owner, rank 5) makes bob an admin collaborator (allowed: owner > admin)
        string addCsrf = await Accounts.CsrfTokenAsync(alice, "/r/w");
        using var add = await alice.PostAsync("/r/w/collaborators",
            Form(("__RequestVerificationToken", addCsrf), ("login", "bob"), ("role", "admin")));
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/r/w")).StatusCode); // bob can now see it

        // bob (admin, NOT owner) tries to delete it
        string delCsrf = await Accounts.CsrfTokenAsync(bob, "/r/w");
        using var del = await bob.PostAsync("/r/w/delete", Form(("__RequestVerificationToken", delCsrf)));

        // the world survives — irreversible delete is owner-only; the admin attempt was a no-op
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/r/w")).StatusCode);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(x => new KeyValuePair<string, string>(x.Key, x.Value)));
}
