using McaDiff.Repo;

namespace McadiffHub;

/// <summary>
/// Caches a rendered map PNG per backup. Like <see cref="WorldCache"/>, a commit is immutable, so a
/// rendered <c>map/&lt;repo&gt;/&lt;commit&gt;.png</c> is a permanent valid cache — the expensive
/// surface scan runs once per backup. Rendering reads the materialized world the world cache provides.
/// </summary>
public sealed class MapCache(string cacheDir, WorldCache worlds)
{
    private readonly string _root = Path.GetFullPath(cacheDir);
    private readonly object _lock = new();

    public byte[] Png(string repoName, Repository repo, string commit)
    {
        string path = Path.Combine(_root, repoName, commit + ".png");
        if (File.Exists(path)) return File.ReadAllBytes(path);
        lock (_lock)
        {
            if (File.Exists(path)) return File.ReadAllBytes(path);
            string worldDir = worlds.Materialize(repoName, repo, commit);
            byte[] png = MapRenderer.Render(worldDir, out _);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllBytes(tmp, png);
            File.Move(tmp, path, overwrite: true);
            return png;
        }
    }
}
