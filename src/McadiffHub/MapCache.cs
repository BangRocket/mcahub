using McaDiff.Repo;

namespace McadiffHub;

/// <summary>
/// Caches a rendered map PNG per backup. Like <see cref="WorldCache"/>, a commit is immutable, so a
/// rendered <c>map/&lt;repo&gt;/&lt;commit&gt;.png</c> is a permanent valid cache — the expensive
/// surface scan runs once per backup. A <see cref="RenderGate"/> coalesces concurrent requests for the
/// same map and caps total render concurrency; a <see cref="DiskCacheQuota"/> bounds the PNG cache size
/// so it can't fill the disk.
/// </summary>
public sealed class MapCache
{
    private readonly string _root;
    private readonly WorldCache _worlds;
    private readonly RenderGate _gate;
    private readonly int _maxRenderChunks;
    private readonly DiskCacheQuota _quota;

    public MapCache(string cacheDir, WorldCache worlds, int maxRenderConcurrency, int maxRenderChunks, CacheLimits limits)
    {
        _root = Path.GetFullPath(cacheDir);
        _worlds = worlds;
        _gate = new RenderGate(maxRenderConcurrency);
        _maxRenderChunks = maxRenderChunks;
        _quota = new DiskCacheQuota(limits.MapBytes, limits.MapsPerRepo, DeleteFile);
        Seed();
        _quota.Enforce();
    }

    public async Task<byte[]> PngAsync(string repoName, Repository repo, string commit, CancellationToken ct)
    {
        string path = Path.Combine(_root, repoName, commit + ".png");
        string key = repoName + ":" + commit;
        if (File.Exists(path)) { _quota.Touch(key); return await File.ReadAllBytesAsync(path, ct); }
        return await _gate.RunAsync(key, async () =>
        {
            if (File.Exists(path)) { _quota.Touch(key); return await File.ReadAllBytesAsync(path, ct); } // double-check
            string worldDir = _worlds.Materialize(repoName, repo, commit, ct);
            byte[] png = MapRenderer.Render(worldDir, out _, _maxRenderChunks, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            await File.WriteAllBytesAsync(tmp, png, ct);
            File.Move(tmp, path, overwrite: true);
            _quota.Admit(key, repoName, path, png.LongLength); // a refused (huge) PNG just isn't cached; bytes still served
            return png;
        }, ct);
    }

    /// <summary>Delete a repo's whole rendered-map cache (on world/account deletion).</summary>
    public void Drop(string repoName)
    {
        string dir = Path.Combine(_root, repoName);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        _quota.Forget(repoName);
    }

    private void Seed()
    {
        if (!Directory.Exists(_root)) return;
        foreach (string repoDir in Directory.EnumerateDirectories(_root))
        {
            string repo = Path.GetFileName(repoDir);
            foreach (string png in Directory.EnumerateFiles(repoDir, "*.png"))
                _quota.Seed(repo + ":" + Path.GetFileNameWithoutExtension(png), repo, png, new FileInfo(png).Length);
        }
    }

    private static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort eviction */ }
    }
}
