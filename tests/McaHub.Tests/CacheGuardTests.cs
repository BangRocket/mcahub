namespace McaHub.Tests;

/// <summary>
/// HubDb.Save surfaces a write failure cleanly (full-disk resilience) instead of leaving a stray temp
/// file. (The old manifest entry-count guard moved into the Rust checkout path — WorldCache caps a
/// materialized world's entry count after checkout.)
/// </summary>
public class CacheGuardTests
{

    [Fact]
    public void HubDb_Save_failure_surfaces_a_clear_error_and_leaves_no_temp_file()
    {
        using var tmp = new TempDir();
        // Point the "db file" at a path that is actually a directory, so the atomic rename can't overwrite it.
        string dbPath = Path.Combine(tmp.Path, "db");
        Directory.CreateDirectory(dbPath);

        var db = new HubDb(dbPath);
        HubDbSaveException ex = Assert.Throws<HubDbSaveException>(() => db.CreateToken("dev:alice", "laptop")); // recognizable → 507 (#32)
        Assert.Contains("account database", ex.Message);
        Assert.Empty(Directory.GetFiles(tmp.Path, "db.tmp-*")); // the stray temp file was cleaned up
    }
}
