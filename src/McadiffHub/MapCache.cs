using McaDiff.Repo;

namespace McadiffHub;

/// <summary>
/// Caches a rendered map PNG per backup. Like <see cref="WorldCache"/>, a commit is immutable, so a
/// rendered <c>map/&lt;repo&gt;/&lt;commit&gt;.png</c> is a permanent valid cache — the expensive
/// surface scan runs once per backup. Rendering reads the materialized world the world cache provides.
/// A <see cref="RenderGate"/> coalesces concurrent requests for the same map and caps total render
/// concurrency, so a burst of cold-map requests can't serialize behind one lock or saturate the host.
/// </summary>
public sealed class MapCache(string cacheDir, WorldCache worlds, int maxRenderConcurrency, int maxRenderChunks)
{
    private readonly string _root = Path.GetFullPath(cacheDir);
    private readonly RenderGate _gate = new(maxRenderConcurrency);

    public async Task<byte[]> PngAsync(string repoName, Repository repo, string commit, CancellationToken ct)
    {
        string path = Path.Combine(_root, repoName, commit + ".png");
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path, ct);
        return await _gate.RunAsync(repoName + ":" + commit, async () =>
        {
            if (File.Exists(path)) return await File.ReadAllBytesAsync(path, ct); // double-check inside the gate
            string worldDir = worlds.Materialize(repoName, repo, commit, ct);
            byte[] png = MapRenderer.Render(worldDir, out _, maxRenderChunks, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            await File.WriteAllBytesAsync(tmp, png, ct);
            File.Move(tmp, path, overwrite: true);
            return png;
        }, ct);
    }
}
