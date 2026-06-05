using System.Collections.Concurrent;
using McaDiff.Repo;

namespace McadiffHub;

/// <summary>
/// Materializes a backup's world to disk so the dir-based queries (<c>WorldQuery</c>) can read it.
/// A commit is immutable, so once materialized under <c>cache/&lt;repo&gt;/&lt;commit&gt;</c> it's a
/// permanent valid cache — the expensive checkout happens once per backup, not per page view. Locking
/// is per repo+commit (not one global lock), so distinct worlds materialize in parallel. A
/// <see cref="DiskCacheQuota"/> bounds total cache size + count so it can't fill the disk, and a hostile
/// manifest with an absurd entry count is refused before it can exhaust inodes.
/// </summary>
public sealed class WorldCache
{
    private readonly string _root;
    private readonly int _manifestCap;
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);
    private readonly DiskCacheQuota _quota;

    public WorldCache(string cacheDir, CacheLimits limits)
    {
        _root = Path.GetFullPath(cacheDir);
        _manifestCap = limits.ManifestEntries;
        _quota = new DiskCacheQuota(limits.WorldBytes, limits.WorldsPerRepo, DeleteDir);
        Seed();
        _quota.Enforce();
    }

    /// <summary>Filesystem entries a manifest will create — the inode/dir-count exhaustion surface.</summary>
    public static int ManifestEntryCount(Manifest m) =>
        m.Regions.Count + m.Nbt.Count + m.Blobs.Count + m.EmptyDirs.Count;

    public string Materialize(string repoName, Repository repo, string commit, CancellationToken ct = default)
    {
        string dir = Path.Combine(_root, repoName, commit);
        string key = repoName + "/" + commit;
        if (Ready(dir)) { _quota.Touch(key); return dir; }
        object gate = _locks.GetOrAdd(key, _ => new object());
        lock (gate)
        {
            if (Ready(dir)) { _quota.Touch(key); return dir; }
            ct.ThrowIfCancellationRequested();

            Manifest manifest = repo.ReadManifest(repo.ReadCommit(commit).Tree);
            int entries = ManifestEntryCount(manifest);
            if (entries > _manifestCap)
                throw new InvalidOperationException(
                    $"world manifest has {entries} entries (cap {_manifestCap}) — refusing to materialize");

            string tmp = dir + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                Checkout.Materialize(repo, manifest, tmp, prune: false);
                ct.ThrowIfCancellationRequested();
                Directory.Move(tmp, dir); // atomic-ish publish so a half-materialize is never seen as ready
            }
            catch
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
                throw;
            }

            if (!_quota.Admit(key, repoName, dir, DirSize(dir))) // Admit deletes the dir if it's over the ceiling
                throw new InvalidOperationException("materialized world exceeds the cache size ceiling — refusing to cache");
            return dir;
        }
    }

    private void Seed()
    {
        if (!Directory.Exists(_root)) return;
        foreach (string repoDir in Directory.EnumerateDirectories(_root))
        {
            string repo = Path.GetFileName(repoDir);
            foreach (string commitDir in Directory.EnumerateDirectories(repoDir))
            {
                string commit = Path.GetFileName(commitDir);
                if (commit.Contains(".tmp-")) continue; // orphaned half-materialize from a crash
                _quota.Seed(repo + "/" + commit, repo, commitDir, DirSize(commitDir));
            }
        }
    }

    private static long DirSize(string dir)
    {
        long total = 0;
        try { foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) total += new FileInfo(f).Length; }
        catch { /* a file vanishing mid-walk just under-counts; harmless */ }
        return total;
    }

    private static void DeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best-effort eviction */ }
    }

    private static bool Ready(string dir) => Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
}
