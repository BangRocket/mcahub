using System.Net;

namespace McadiffHub.Tests;

/// <summary>
/// End-to-end audit (#16): a security-relevant change (here a visibility flip) is recorded and shows up
/// in the per-repo audit view for the owner, but is hidden from a non-owner.
/// </summary>
public class AuditViewTests
{
    [Fact]
    public async Task A_visibility_change_is_audited_and_visible_to_the_owner()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, token, "world1");
        await Accounts.SetPrivateAsync(alice, "world1", isPrivate: true); // emits a "visibility" audit entry

        HttpResponseMessage resp = await alice.GetAsync("/r/world1/audit");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("visibility", body);
        Assert.Contains("alice", body);            // the actor
        Assert.Contains("ownership.claim", body);  // the first-push claim was logged too
    }

    [Fact]
    public async Task The_audit_view_is_hidden_from_non_owners()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, token, "world1"); // public, owned by alice

        HttpClient bob = await Accounts.SignInAsync(f, "bob");
        HttpResponseMessage resp = await bob.GetAsync("/r/world1/audit");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode); // not an owner/admin
    }
}
