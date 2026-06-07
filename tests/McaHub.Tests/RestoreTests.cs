using McaDiff.Repo;

namespace McaHub.Tests;

/// <summary>
/// One-click restore (#24): rolling a world back to a backup adds a NEW backup with that backup's content
/// (so the latest state matches it) parented on the pre-restore tip — reversible, never a destructive
/// overwrite — and is a no-op when the world is already at that state.
/// </summary>
public class RestoreTests
{
    private static string Commit(Repository repo, string worldDir, string? parent, string msg)
    {
        string tree = repo.WriteManifest(Snapshotter.Snapshot(repo, worldDir));
        string c = repo.CreateCommit(tree, parent is null ? [] : [parent], msg, "tester");
        repo.WriteBranch("main", c);
        return c;
    }

    [Fact]
    public void Restore_adds_a_reversible_backup_with_the_old_content()
    {
        using var tmp = new TempDir();
        var repo = Repository.Init(Path.Combine(tmp.Path, "w.mcagit"));
        string c1 = Commit(repo, Worlds.Write(Path.Combine(tmp.Path, "v1"), [Worlds.StoneChunk(0, 0)]), null, "b1");
        string c2 = Commit(repo, Worlds.Write(Path.Combine(tmp.Path, "v2"), [Worlds.StoneChunk(0, 0), Worlds.StoneChunk(1, 0)]), c1, "b2 (grief)");

        string? restored = Pages.RestoreCommit(repo, c1, "alice");

        Assert.NotNull(restored);
        Assert.Equal(repo.ReadCommit(c1).Tree, repo.ReadCommit(restored!).Tree); // content == backup 1
        Assert.Equal(restored, repo.ReadBranch("main"));                          // it's the latest
        Assert.Equal(c2, repo.ReadCommit(restored!).Parents[0]);                  // parented on the griefed tip → reversible
    }

    [Fact]
    public void Restoring_the_current_state_is_a_no_op()
    {
        using var tmp = new TempDir();
        var repo = Repository.Init(Path.Combine(tmp.Path, "w.mcagit"));
        string c1 = Commit(repo, Worlds.Write(Path.Combine(tmp.Path, "v1"), [Worlds.StoneChunk(0, 0)]), null, "b1");

        Assert.Null(Pages.RestoreCommit(repo, c1, "alice")); // already at c1's content → nothing added
    }
}
