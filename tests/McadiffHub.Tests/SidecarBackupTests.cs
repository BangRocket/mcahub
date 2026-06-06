using McaDiff.Repo;
using McadiffHub.Sidecar;

namespace McadiffHub.Tests;

/// <summary>
/// The sidecar's snapshot step (#23): it commits a changed world and skips an unchanged one (so an idle
/// server doesn't pile up empty backups), and each real change advances the branch tip.
/// </summary>
public class SidecarBackupTests
{
    [Fact]
    public void Snapshot_commits_a_change_skips_a_no_op_and_advances_on_the_next_change()
    {
        using var tmp = new TempDir();
        string world = Worlds.Write(Path.Combine(tmp.Path, "world"), [Worlds.StoneChunk(0, 0)]);
        var repo = Repository.Init(Path.Combine(tmp.Path, "backup.mcagit"));

        string? first = Backup.Snapshot(repo, world, "main", "tester", "auto: startup");
        Assert.NotNull(first);
        Assert.Equal(first, repo.ReadBranch("main"));

        string? unchanged = Backup.Snapshot(repo, world, "main", "tester", "auto: interval");
        Assert.Null(unchanged); // world didn't change → no empty backup

        Worlds.Write(world, [Worlds.StoneChunk(0, 0), Worlds.StoneChunk(1, 0)]); // a real change
        string? next = Backup.Snapshot(repo, world, "main", "tester", "auto: change");
        Assert.NotNull(next);
        Assert.NotEqual(first, next);
        Assert.Equal(next, repo.ReadBranch("main")); // tip advanced
    }
}
