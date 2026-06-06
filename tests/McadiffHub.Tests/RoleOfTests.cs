namespace McadiffHub.Tests;

/// <summary>
/// `HubDb.RoleOf` is the single source of truth for authorization (#20): it folds owner, direct
/// collaborator, and team grants into the strongest role. Transport, pages, and UI all route through it,
/// so its folding rules are a trust boundary.
/// </summary>
public class RoleOfTests
{
    private static HubDb Db(TempDir t)
    {
        var db = new HubDb(Path.Combine(t.Path, "hub.json"));
        db.UpsertUser("alice", "alice", "Alice", "");
        db.UpsertUser("bob", "bob", "Bob", "");
        db.EnsureRepo("w", "alice", isPrivate: true); // alice owns it
        return db;
    }

    [Fact]
    public void Owner_outranks_everything()
    {
        using var t = new TempDir();
        Assert.Equal("owner", Db(t).RoleOf("w", "alice"));
    }

    [Fact]
    public void A_direct_collaborator_grant_applies()
    {
        using var t = new TempDir();
        HubDb db = Db(t);
        db.SetCollab("w", "bob", "write");
        Assert.Equal("write", db.RoleOf("w", "bob"));
    }

    [Fact]
    public void A_team_grant_applies_to_members()
    {
        using var t = new TempDir();
        HubDb db = Db(t);
        db.CreateTeam("builders", "alice");
        db.AddTeamMember("builders", "bob");
        db.SetTeamGrant("w", "builders", "read");
        Assert.Equal("read", db.RoleOf("w", "bob"));
    }

    [Fact]
    public void The_strongest_of_collaborator_and_team_wins()
    {
        using var t = new TempDir();
        HubDb db = Db(t);
        db.SetCollab("w", "bob", "read");                 // direct: read
        db.CreateTeam("builders", "alice");
        db.AddTeamMember("builders", "bob");
        db.SetTeamGrant("w", "builders", "write");        // team: write
        Assert.Equal("write", db.RoleOf("w", "bob"));     // max(read, write)
    }

    [Fact]
    public void A_team_member_with_no_grant_on_the_repo_has_no_role()
    {
        using var t = new TempDir();
        HubDb db = Db(t);
        db.CreateTeam("builders", "alice");
        db.AddTeamMember("builders", "bob"); // team exists but has no grant on "w"
        Assert.Null(db.RoleOf("w", "bob"));
    }

    [Fact]
    public void A_null_user_has_no_role()
    {
        using var t = new TempDir();
        Assert.Null(Db(t).RoleOf("w", null));
    }

    [Fact]
    public void Deleting_a_team_clears_its_grant()
    {
        using var t = new TempDir();
        HubDb db = Db(t);
        db.CreateTeam("builders", "alice");
        db.AddTeamMember("builders", "bob");
        db.SetTeamGrant("w", "builders", "write");
        Assert.Equal("write", db.RoleOf("w", "bob"));

        db.DeleteTeam("builders");
        Assert.Null(db.RoleOf("w", "bob")); // grant gone with the team
    }
}
