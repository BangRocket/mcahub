using McaHub.Rust;

namespace McaHub;

/// <summary>Which dimension's region tree to render (#27).</summary>
public enum MapDimension { Overworld, Nether, End }

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
    private readonly RustEngine _rust;

    public MapCache(string cacheDir, WorldCache worlds, int maxRenderConcurrency, int maxRenderChunks, CacheLimits limits, RustEngine rust)
    {
        _root = Path.GetFullPath(cacheDir);
        _worlds = worlds;
        _gate = new RenderGate(maxRenderConcurrency);
        _maxRenderChunks = maxRenderChunks;
        _quota = new DiskCacheQuota(limits.MapBytes, limits.MapsPerRepo, DeleteFile);
        _rust = rust;
        Seed();
        _quota.Enforce();
    }

    /// <summary>Map a hub dimension to the `mcagit --dim` token (overworld → none).</summary>
    private static string? DimToken(MapDimension dim) =>
        dim switch { MapDimension.Nether => "nether", MapDimension.End => "end", _ => null };

    public async Task<byte[]> PngAsync(string repoName, string repoDir, string commit, CancellationToken ct, MapDimension dim = MapDimension.Overworld)
    {
        string path = PathFor(repoName, commit, dim);
        string key = RenderKey(repoName, commit, dim);
        if (await TryReadAsync(path, key, ct) is { } hit) return hit; // serve cached, tolerating a concurrent eviction (TOCTOU)
        return await _gate.RunAsync(key, async () =>
        {
            if (await TryReadAsync(path, key, ct) is { } hit) return hit; // double-check under the gate
            string worldDir = _worlds.Materialize(repoName, repoDir, commit, ct);
            byte[] png = _rust.Render(worldDir, DimToken(dim), _maxRenderChunks);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            await File.WriteAllBytesAsync(tmp, png, ct);
            File.Move(tmp, path, overwrite: true);
            _quota.Admit(key, repoName, path, png.LongLength); // a refused (huge) PNG just isn't cached; bytes still served
            return png;
        }, ct);
    }

    /// <summary>A cache-only probe: returns the cached PNG for a commit's map, or null if it hasn't been
    /// rendered yet (a cold map). Lets the render queue serve a warm map without touching the worker pool.</summary>
    public Task<byte[]?> TryCachedAsync(string repoName, string commit, MapDimension dim, CancellationToken ct) =>
        TryReadAsync(PathFor(repoName, commit, dim), RenderKey(repoName, commit, dim), ct);

    /// <summary>The cache key (also the render-gate and job key) for a commit's map in a given dimension.</summary>
    public static string RenderKey(string repoName, string commit, MapDimension dim) => repoName + ":" + commit + Suffix(dim);

    // Overworld keeps the bare <commit>.png path (back-compat); the Nether/End get a suffix (#27).
    private static string Suffix(MapDimension dim) => dim == MapDimension.Overworld ? "" : "-" + dim.ToString().ToLowerInvariant();
    private string PathFor(string repoName, string commit, MapDimension dim) => Path.Combine(_root, repoName, commit + Suffix(dim) + ".png");

    // Read a cached PNG, returning null (→ re-render) if it was evicted between the check and the read — the
    // LRU eviction runs under the quota lock, not ours, so File.Exists-then-read had a TOCTOU race. (audit LOW)
    private async Task<byte[]?> TryReadAsync(string path, string key, CancellationToken ct)
    {
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, ct);
            _quota.Touch(key);
            return bytes;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null; // evicted out from under us — fall through to render
        }
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
