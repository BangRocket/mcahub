using System.Collections.Concurrent;
using McaDiff.Repo;

namespace McadiffHub;

/// <summary>
/// Materializes a backup's world to disk so the dir-based queries (<c>WorldQuery</c>) can read it.
/// A commit is immutable, so once materialized under <c>cache/&lt;repo&gt;/&lt;commit&gt;</c> it's a
/// permanent valid cache — the expensive checkout happens once per backup, not per page view. Locking
/// is per repo+commit (not one global lock), so materializing distinct worlds runs in parallel.
/// </summary>
public sealed class WorldCache(string cacheDir)
{
    private readonly string _root = Path.GetFullPath(cacheDir);
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);

    public string Materialize(string repoName, Repository repo, string commit, CancellationToken ct = default)
    {
        string dir = Path.Combine(_root, repoName, commit);
        if (Ready(dir)) return dir;
        object gate = _locks.GetOrAdd(repoName + "/" + commit, _ => new object());
        lock (gate)
        {
            if (Ready(dir)) return dir;
            ct.ThrowIfCancellationRequested();
            string tmp = dir + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(commit).Tree), tmp, prune: false);
                ct.ThrowIfCancellationRequested();
                Directory.Move(tmp, dir); // atomic-ish publish so a half-materialize is never seen as ready
            }
            catch
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { /* best-effort cleanup */ }
                throw;
            }
            return dir;
        }
    }

    private static bool Ready(string dir) => Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
}
