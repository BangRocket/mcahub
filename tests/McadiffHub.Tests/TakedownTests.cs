using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;

namespace McadiffHub.Tests;

/// <summary>
/// Takedown + suspension (#35b): a suspended user is locked out of read/write (a non-destructive penalty),
/// the operator can remove any world with the master token, and a configured report address surfaces a
/// "report this world" link to non-owners.
/// </summary>
public class TakedownTests
{
    private static Auth.Config AccountsCfg() =>
        Auth.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["OAuthClientId"] = "", ["OAuthClientSecret"] = "", ["DevLogin"] = "1" }).Build());

    [Fact]
    public void A_suspended_user_cannot_read_or_write()
    {
        using var tmp = new TempDir();
        var db = new HubDb(Path.Combine(tmp.Path, "hub.json"));
        db.UpsertUser("alice", "alice", "Alice", "");
        db.EnsureRepo("w", "alice", isPrivate: false);
        Auth.Config cfg = AccountsCfg();

        Assert.True(Auth.CanRead(cfg, db, "w", "alice", false));
        Assert.True(Auth.CanWrite(cfg, db, "w", "alice", false));

        db.SetSuspended("alice", true);

        Assert.False(Auth.CanRead(cfg, db, "w", "alice", false));  // locked out, even from her own public world
        Assert.False(Auth.CanWrite(cfg, db, "w", "alice", false));

        db.SetSuspended("alice", false); // lifting it restores access (non-destructive)
        Assert.True(Auth.CanRead(cfg, db, "w", "alice", false));
    }

    [Fact]
    public void Suspending_revokes_tokens_bumps_epoch_and_blocks_management() // audit LOW: suspension takes effect now
    {
        using var tmp = new TempDir();
        var db = new HubDb(Path.Combine(tmp.Path, "hub.json"));
        db.UpsertUser("alice", "alice", "Alice", "");
        db.EnsureRepo("w", "alice", isPrivate: true); // alice owns w
        string token = db.CreateToken("alice", "laptop", "write");
        int epoch0 = db.GetUser("alice")!.Epoch;

        Assert.NotNull(db.ResolveToken(token));                  // valid PAT before
        Assert.True(Auth.CanManagePeople(db, "w", "alice"));     // owner can manage before

        db.SetSuspended("alice", true);

        Assert.Null(db.ResolveToken(token));                     // PAT revoked immediately (CLI is dead)
        Assert.True(db.GetUser("alice")!.Epoch > epoch0);        // epoch bumped → live web sessions invalidated
        Assert.False(Auth.CanManagePeople(db, "w", "alice"));    // suspended → can't manage people…
        Assert.False(Auth.CanManageSettings(db, "w", "alice"));  // …or settings…
        Assert.False(Auth.CanGrantRole(db, "w", "alice", "read")); // …or grant any role, even with the owner role
    }

    [Fact]
    public async Task The_master_token_can_remove_any_world()
    {
        using var f = new HubFactory(HubMode.Accounts, settings: [new("PushToken", "test-master-token")]);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, token, "badworld");

        using var c = f.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/admin/repos/badworld/remove");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-master-token");
        using HttpResponseMessage resp = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using HttpResponseMessage gone = await alice.GetAsync("/r/badworld");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task A_regular_token_cannot_use_the_admin_remove()
    {
        using var f = new HubFactory(HubMode.Accounts, settings: [new("PushToken", "test-master-token")]);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, token, "myworld");

        using var c = f.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/admin/repos/myworld/remove");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); // a regular PAT, not the master
        using HttpResponseMessage resp = await c.SendAsync(req);
        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);

        using HttpResponseMessage still = await alice.GetAsync("/r/myworld"); // owner can still see it
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    [Fact]
    public async Task A_viewable_world_shows_a_report_link_to_non_owners_when_configured()
    {
        using var f = new HubFactory(HubMode.Accounts, settings: [new("ReportEmail", "abuse@example.com")]);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, token, "world1");
        await Accounts.SetPrivateAsync(alice, "world1", isPrivate: false); // public

        using var anon = f.CreateClient();
        string html = await (await anon.GetAsync("/r/world1")).Content.ReadAsStringAsync();
        Assert.Contains("mailto:abuse@example.com", html);
    }
}
