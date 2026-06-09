namespace McaHub.Tests;

/// <summary>
/// Account + world deletion (#35a, GDPR/CCPA erasure): a user can be fully removed (identity, tokens,
/// collaborator grants, owned teams) with their owned worlds reported for on-disk deletion; a single
/// world can be deleted (meta + grants); and the repo store can remove the bare repo from disk.
/// </summary>
public class DeletionTests
{
    private static HubDb Db(TempDir t) => new(Path.Combine(t.Path, "hub.json"));

    [Fact]
    public void DeleteUser_erases_identity_tokens_grants_and_returns_owned_repos()
    {
        using var tmp = new TempDir();
        HubDb db = Db(tmp);
        db.UpsertUser("alice", "alice", "Alice", "");
        db.UpsertUser("bob", "bob", "Bob", "");
        string tok = db.CreateToken("alice", "laptop", "write", null);
        db.EnsureRepo("alices-world", "alice", isPrivate: true);
        db.EnsureRepo("bobs-world", "bob", isPrivate: false);
        db.SetCollab("bobs-world", "alice", "read"); // alice collaborates on bob's world
        db.CreateTeam("builders", "alice");          // alice owns a team

        IReadOnlyList<string> owned = db.DeleteUser("alice");

        Assert.Equal(new[] { "alices-world" }, owned);     // her owned worlds, for on-disk deletion
        Assert.Null(db.GetUser("alice"));                   // identity gone
        Assert.Null(db.ResolveToken(tok));                  // tokens gone
        Assert.Null(db.GetRepo("alices-world"));            // owned-repo meta gone
        Assert.Null(db.RoleOf("bobs-world", "alice"));     // her collaborator grant gone
        Assert.Null(db.GetTeam("builders"));               // her owned team gone
        Assert.NotNull(db.GetRepo("bobs-world"));          // someone else's world is untouched
    }

    [Fact]
    public void DeleteRepo_removes_the_meta_and_its_grants()
    {
        using var tmp = new TempDir();
        HubDb db = Db(tmp);
        db.UpsertUser("alice", "alice", "", "");
        db.EnsureRepo("w", "alice", isPrivate: false);
        db.SetCollab("w", "bob", "write");

        db.DeleteRepo("w");

        Assert.Null(db.GetRepo("w"));
        Assert.Empty(db.CollabsOf("w"));
    }

    [Fact]
    public void RepoStore_delete_removes_the_repo_from_disk()
    {
        using var tmp = new TempDir();
        var store = new RepoStore(Path.Combine(tmp.Path, "repos"), McaHub.Rust.RustEngine.FromEnv());
        Directory.CreateDirectory(Path.Combine(store.PathOf("w"), "objects")); // a bare mcagit repo on disk
        Assert.True(store.Exists("w"));
        Assert.True(store.Delete("w"));
        Assert.False(store.Exists("w"));
        Assert.False(store.Delete("w")); // already gone → no-op
    }
}
