using System.Collections.Generic;
using McaDiff.Repo;

namespace McadiffHub.Tests;

/// <summary>
/// The non-quota guards from #5: the manifest entry-count (inode-exhaustion guard) and HubDb.Save
/// surfacing a write failure cleanly (full-disk resilience) instead of leaving a stray temp file.
/// </summary>
public class CacheGuardTests
{
    [Fact]
    public void Manifest_entry_count_sums_files_and_dirs()
    {
        var m = new Manifest();
        m.Regions["r.0.0.mca"] = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["0.0"] = "h1", ["0.1"] = "h2" };
        m.Nbt["level.dat"] = "h3";
        m.Blobs["icon.png"] = "h4";
        m.EmptyDirs.Add("data");
        // counts the filesystem entries created (region files, nbt, blobs, empty dirs) — not inner chunks
        Assert.Equal(4, WorldCache.ManifestEntryCount(m));
    }

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
