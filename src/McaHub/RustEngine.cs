using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McaHub.Rust;

/// <summary>
/// Bridge to the Rust <c>mcagit</c> binary — the engine that replaces the in-process
/// .NET mcadiff core (Phase 3 of the Rust-core port). Every operation shells out to
/// <c>mcagit</c> and parses its <c>--json</c> output (or, for transport, the hub
/// protocol served by <c>mcagit serve</c>). Repos on disk are the Rust object format
/// (blake3/zstd); this type never parses world bytes itself.
/// </summary>
public sealed class RustEngine(string binary)
{
    /// <summary>The configured binary, defaulting to <c>mcagit</c> on PATH (override via MCAGIT_BIN).</summary>
    public static RustEngine FromEnv() =>
        new(Environment.GetEnvironmentVariable("MCAGIT_BIN") is { Length: > 0 } b ? b : "mcagit");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ---- repo / transport plumbing ----

    /// <summary>Materialize <paramref name="commit"/> of the bare repo at <paramref name="repoDir"/> into <paramref name="outDir"/>.</summary>
    public void Checkout(string repoDir, string commit, string outDir) =>
        Run(["-C", repoDir, "checkout", commit, outDir], allow: [0]);

    /// <summary>The commit hash a ref resolves to, or null.</summary>
    public string? RevParse(string repoDir, string rev)
    {
        var (code, outp, _) = Run(["-C", repoDir, "rev-parse", rev], allow: [0, 2], throwOnFail: false);
        return code == 0 ? outp.Trim() : null;
    }

    /// <summary>Commit log (newest first) as (hash, message) pairs.</summary>
    public IReadOnlyList<(string Hash, string Message)> Log(string repoDir)
    {
        var (code, outp, _) = Run(["-C", repoDir, "log", "--oneline"], allow: [0, 2], throwOnFail: false);
        if (code != 0) return [];
        var list = new List<(string, string)>();
        foreach (string line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int sp = line.IndexOf(' ');
            if (sp > 0) list.Add((line[..sp], line[(sp + 1)..]));
        }
        return list;
    }

    // ---- render data (the web UI) ----

    /// <summary>Semantic diff of two materialized worlds (exit 1 = "differs" is normal).</summary>
    public DiffResult Diff(string worldA, string worldB)
    {
        var (_, outp, _) = Run(["diff", worldA, worldB, "--json"], allow: [0, 1]);
        return JsonSerializer.Deserialize<DiffResult>(outp, Json) ?? new DiffResult([]);
    }

    /// <summary>Coordinate-level block changes turning <paramref name="oldWorld"/> into <paramref name="newWorld"/>.</summary>
    public IReadOnlyList<BlockChange> WhereChanged(string oldWorld, string newWorld, string? dim = null)
    {
        string[] args = dim is null
            ? ["where-changed", oldWorld, newWorld, "--json"]
            : ["where-changed", oldWorld, newWorld, "--dim", dim, "--json"];
        var (_, outp, _) = Run(args, allow: [0, 1]);
        return JsonSerializer.Deserialize<List<BlockChange>>(outp, Json) ?? [];
    }

    /// <summary>Grief summary (destroyed / placed / replaced + bounding box + top blocks) of a backup transition.</summary>
    public GriefSummary Grief(string oldWorld, string newWorld, string? dim = null) =>
        GriefSummary.From(WhereChanged(oldWorld, newWorld, dim));

    public IReadOnlyList<PlayerHit> Players(string world)
    {
        var (_, outp, _) = Run(["players", "--world", world, "--json"], allow: [0]);
        return JsonSerializer.Deserialize<List<PlayerHit>>(outp, Json) ?? [];
    }

    public IReadOnlyList<EntityHit> Find(string world, string kind, string? id = null, string? dim = null)
    {
        var args = new List<string> { "find", kind };
        if (id is not null) args.Add(id);
        args.AddRange(["--world", world, "--json"]);
        if (dim is not null) args.AddRange(["--dim", dim]);
        var (_, outp, _) = Run([.. args], allow: [0]);
        return JsonSerializer.Deserialize<List<EntityHit>>(outp, Json) ?? [];
    }

    /// <summary>Render a top-down map PNG of a materialized world.</summary>
    public byte[] Render(string world, string? dim = null, int maxChunks = 10_000)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"mcamap-{Guid.NewGuid():N}.png");
        try
        {
            var args = new List<string> { "render", world, "-o", tmp, "--max-chunks", maxChunks.ToString() };
            if (dim is not null) args.AddRange(["--dim", dim]);
            Run([.. args], allow: [0]);
            return File.ReadAllBytes(tmp);
        }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    // ---- process runner ----

    private (int Code, string Out, string Err) Run(string[] args, int[] allow, bool throwOnFail = true)
    {
        var psi = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to launch {binary}");
        string outp = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (throwOnFail && Array.IndexOf(allow, p.ExitCode) < 0)
            throw new InvalidOperationException($"mcagit {string.Join(' ', args)} exited {p.ExitCode}: {err.Trim()}");
        return (p.ExitCode, outp, err);
    }
}

// ---- DTOs matching `mcagit … --json` ----

public sealed record DiffResult([property: JsonPropertyName("files")] IReadOnlyList<FileDiff> Files)
{
    public bool HasDifferences => Files.Count > 0;
}

public sealed record FileDiff(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("chunks")] IReadOnlyList<ChunkDiff> Chunks,
    [property: JsonPropertyName("nodeChanges")] IReadOnlyList<NodeChange> NodeChanges);

public sealed record ChunkDiff(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("z")] int Z,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("changes")] int Changes,
    [property: JsonPropertyName("blockEdits")] IReadOnlyList<BlockChange> BlockEdits);

public sealed record NodeChange(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("kind")] string Kind);

public sealed record BlockChange(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("z")] int Z,
    [property: JsonPropertyName("old")] string? Old,
    [property: JsonPropertyName("new")] string? New);

public sealed record PlayerHit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("pos")] double[]? Pos,
    [property: JsonPropertyName("dimension")] string? Dimension,
    [property: JsonPropertyName("health")] float? Health,
    [property: JsonPropertyName("xp_level")] int? XpLevel);

public sealed record EntityHit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("x")] int? X,
    [property: JsonPropertyName("y")] int? Y,
    [property: JsonPropertyName("z")] int? Z,
    [property: JsonPropertyName("pos")] double[]? Pos,
    [property: JsonPropertyName("text")] IReadOnlyList<string>? Text);

/// <summary>Grief forensics derived from coordinate-level block changes.</summary>
public sealed record GriefSummary(
    int Destroyed,
    int Placed,
    int Replaced,
    BoundingBox? Box,
    IReadOnlyList<(string Block, int Count)> TopDestroyed)
{
    public bool Any => Destroyed + Placed + Replaced > 0;

    public static GriefSummary From(IReadOnlyList<BlockChange> changes)
    {
        int destroyed = 0, placed = 0, replaced = 0;
        var destroyedBy = new Dictionary<string, int>();
        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
        bool any = false;
        foreach (BlockChange c in changes)
        {
            bool oldAir = IsAir(c.Old), newAir = IsAir(c.New);
            if (oldAir && newAir) continue;
            if (!oldAir && newAir)
            {
                destroyed++;
                string b = c.Old ?? "?";
                destroyedBy[b] = destroyedBy.GetValueOrDefault(b) + 1;
            }
            else if (oldAir) placed++;
            else replaced++;
            any = true;
            minX = Math.Min(minX, c.X); maxX = Math.Max(maxX, c.X);
            minY = Math.Min(minY, c.Y); maxY = Math.Max(maxY, c.Y);
            minZ = Math.Min(minZ, c.Z); maxZ = Math.Max(maxZ, c.Z);
        }
        BoundingBox? box = any ? new BoundingBox(minX, minY, minZ, maxX, maxY, maxZ) : null;
        var top = destroyedBy.OrderByDescending(kv => kv.Value).Take(8)
            .Select(kv => (kv.Key, kv.Value)).ToList();
        return new GriefSummary(destroyed, placed, replaced, box, top);
    }

    private static bool IsAir(string? n) => n is null or "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air";
}

public sealed record BoundingBox(int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ)
{
    public int Volume => Math.Max(0, MaxX - MinX + 1) * Math.Max(0, MaxY - MinY + 1) * Math.Max(0, MaxZ - MinZ + 1);
    public override string ToString() => $"({MinX},{MinY},{MinZ})–({MaxX},{MaxY},{MaxZ})";
}
