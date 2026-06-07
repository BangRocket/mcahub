using System.Net;
using Microsoft.Extensions.Configuration;

namespace McaHub.Tests;

/// <summary>
/// Privacy defaults (#34): new worlds are private until explicitly published, and the player-data gate
/// shows coordinates/health/signs only to a world's collaborators (everyone, on a trusted LAN).
/// </summary>
public class PrivacyTests
{
    private static Auth.Config Cfg(bool accounts) =>
        Auth.Read(new ConfigurationBuilder().AddInMemoryCollection(
            new[] { ("OAuthClientId", ""), ("OAuthClientSecret", ""), ("DevLogin", accounts ? "1" : "0") }
                .Select(s => new KeyValuePair<string, string?>(s.Item1, s.Item2))).Build());

    [Fact]
    public async Task New_pushes_are_private_by_default()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, token, "secret");

        using HttpResponseMessage anon = await f.CreateClient().GetAsync("/r/secret");
        Assert.Equal(HttpStatusCode.NotFound, anon.StatusCode); // private → hidden from strangers
    }

    [Fact]
    public async Task Default_private_can_be_disabled_for_a_trusted_lan()
    {
        using var f = new HubFactory(HubMode.Accounts, settings: [new("DefaultPrivate", "0")]);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, token, "openworld");

        using HttpResponseMessage anon = await f.CreateClient().GetAsync("/r/openworld");
        Assert.Equal(HttpStatusCode.OK, anon.StatusCode); // public by config
    }

    [Fact]
    public void Player_data_gates_to_collaborators_in_accounts_mode()
    {
        using var tmp = new TempDir();
        var db = new HubDb(Path.Combine(tmp.Path, "hub.json"));
        db.UpsertUser("alice", "alice", "Alice", "");
        db.UpsertUser("bob", "bob", "Bob", "");
        db.EnsureRepo("w", "alice", isPrivate: false); // public, owned by alice
        db.SetCollab("w", "bob", "read");

        Assert.True(Auth.CanSeePlayerData(Cfg(false), db, "w", null, false));     // open mode: trusted LAN, everyone
        Assert.True(Auth.CanSeePlayerData(Cfg(true), db, "w", "alice", false));   // owner
        Assert.True(Auth.CanSeePlayerData(Cfg(true), db, "w", "bob", false));     // read collaborator
        Assert.False(Auth.CanSeePlayerData(Cfg(true), db, "w", "carol", false));  // stranger
        Assert.False(Auth.CanSeePlayerData(Cfg(true), db, "w", null, false));     // anonymous
    }
}
