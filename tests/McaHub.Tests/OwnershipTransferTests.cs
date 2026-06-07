using System.Net;

namespace McaHub.Tests;

/// <summary>
/// Repo ownership transfer (#17): an owner can hand a world to another existing user; the ex-owner stays
/// on as an admin (keeps access); self/nonexistent transfers are refused; capabilities re-resolve via RoleOf.
/// </summary>
public class OwnershipTransferTests
{
    private static HubDb Seed(TempDir t)
    {
        var db = new HubDb(Path.Combine(t.Path, "hub.json"));
        db.UpsertUser("alice", "alice", "Alice", "");
        db.UpsertUser("bob", "bob", "Bob", "");
        db.EnsureRepo("w", "alice", isPrivate: true);
        return db;
    }

    [Fact]
    public void Owner_can_transfer_to_another_user_and_is_kept_as_admin()
    {
        using var t = new TempDir();
        HubDb db = Seed(t);

        Assert.True(db.TransferOwnership("w", "bob"));

        Assert.Equal("bob", db.GetRepo("w")!.OwnerId);
        Assert.Equal("owner", db.RoleOf("w", "bob"));   // new owner
        Assert.Equal("admin", db.RoleOf("w", "alice")); // demoted, still has access
    }

    [Fact]
    public void Transfer_to_self_is_refused()
    {
        using var t = new TempDir();
        Assert.False(Seed(t).TransferOwnership("w", "alice"));
    }

    [Fact]
    public void Transfer_to_a_nonexistent_user_is_refused()
    {
        using var t = new TempDir();
        Assert.False(Seed(t).TransferOwnership("w", "carol"));
    }

    [Fact]
    public void Existing_grants_survive_a_transfer()
    {
        using var t = new TempDir();
        HubDb db = Seed(t);
        db.UpsertUser("carol", "carol", "Carol", "");
        db.SetCollab("w", "carol", "write");

        db.TransferOwnership("w", "bob");

        Assert.Equal("write", db.RoleOf("w", "carol")); // unrelated grant untouched
    }

    [Fact]
    public async Task An_owner_transfers_over_http_and_the_new_owner_takes_over()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        HttpClient bob = await Accounts.SignInAsync(f, "bob"); // bob must exist as a user
        await Accounts.CreateRepoAsync(f, await Accounts.MintTokenAsync(alice), "world1");

        await Accounts.TransferAsync(alice, "world1", "bob");

        using HttpResponseMessage bobSees = await bob.GetAsync("/r/world1"); // bob now owns it (private world)
        Assert.Equal(HttpStatusCode.OK, bobSees.StatusCode);
    }
}
