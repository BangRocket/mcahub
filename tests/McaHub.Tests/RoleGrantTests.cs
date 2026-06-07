namespace McaHub.Tests;

/// <summary>
/// audit MED-4: a grantor may only assign a role strictly below their own effective rank. So an admin
/// collaborator can grant up to maintain but cannot mint another admin — only the owner grants admin.
/// Without this, one admin grant becomes durable: an admin could create co-admins the owner must fight to
/// remove. The owner (rank 5) outranks admin (4) > maintain (3) > write (2) > read (1) > none (0).
/// </summary>
public class RoleGrantTests
{
    private static HubDb Db(string dir)
    {
        var db = new HubDb(Path.Combine(dir, "hub.json"));
        db.UpsertUser("alice", "alice", "Alice", "");
        db.EnsureRepo("w", "alice", isPrivate: true);   // alice owns w
        db.UpsertUser("bob", "bob", "Bob", "");
        db.SetCollab("w", "bob", "admin");              // bob: admin collaborator
        db.UpsertUser("carol", "carol", "Carol", "");
        db.SetCollab("w", "carol", "maintain");         // carol: maintain
        return db;
    }

    [Fact]
    public void Owner_can_grant_admin_and_below()
    {
        using var tmp = new TempDir();
        HubDb db = Db(tmp.Path);
        foreach (string r in new[] { "admin", "maintain", "write", "read" })
            Assert.True(Auth.CanGrantRole(db, "w", "alice", r)); // owner outranks every role
    }

    [Fact]
    public void Admin_cannot_grant_admin_but_can_grant_below()
    {
        using var tmp = new TempDir();
        HubDb db = Db(tmp.Path);
        Assert.False(Auth.CanGrantRole(db, "w", "bob", "admin"));   // no minting a peer admin
        Assert.True(Auth.CanGrantRole(db, "w", "bob", "maintain"));
        Assert.True(Auth.CanGrantRole(db, "w", "bob", "write"));
        Assert.True(Auth.CanGrantRole(db, "w", "bob", "read"));
    }

    [Fact]
    public void Maintain_cannot_grant_at_or_above_itself()
    {
        using var tmp = new TempDir();
        HubDb db = Db(tmp.Path);
        Assert.False(Auth.CanGrantRole(db, "w", "carol", "admin"));
        Assert.False(Auth.CanGrantRole(db, "w", "carol", "maintain"));
        Assert.True(Auth.CanGrantRole(db, "w", "carol", "write"));
    }

    [Fact]
    public void A_stranger_or_anonymous_cannot_grant_anything()
    {
        using var tmp = new TempDir();
        HubDb db = Db(tmp.Path);
        Assert.False(Auth.CanGrantRole(db, "w", "dave", "read")); // no role on w → rank 0
        Assert.False(Auth.CanGrantRole(db, "w", null, "read"));
    }
}
