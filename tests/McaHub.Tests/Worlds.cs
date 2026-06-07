using fNbt;
using McaDiff.Anvil;

namespace McaHub.Tests;

/// <summary>
/// Builds minimal but real materialized worlds (a <c>region/r.X.Z.mca</c> tree) for renderer tests,
/// using the mcadiff core's region writer + chunk codec. The simplest renderable chunk has a single
/// section with a one-entry "stone" palette (a single-entry palette omits the packed data array), which
/// the renderer decodes to a solid surface.
/// </summary>
internal static class Worlds
{
    /// <summary>A renderable chunk: one section at <paramref name="sectionY"/>, solid stone.</summary>
    public static RawChunk StoneChunk(int x, int z, sbyte sectionY = 0)
    {
        var paletteEntry = new NbtCompound { new NbtString("Name", "minecraft:stone") }; // unnamed (list element)
        var blockStates = new NbtCompound("block_states")
        {
            new NbtList("palette", NbtTagType.Compound) { paletteEntry },
        };
        var section = new NbtCompound { new NbtByte("Y", (byte)sectionY), blockStates }; // unnamed (list element)
        var root = new NbtCompound("")
        {
            new NbtList("sections", NbtTagType.Compound) { section },
        };
        byte[] payload = ChunkCodec.Encode(root, ChunkCompression.ZLib);
        return new RawChunk(new ChunkPos(x, z), ChunkCompression.ZLib, payload, external: false, timestamp: 0);
    }

    /// <summary>Write the chunks into the correct <c>region/r.X.Z.mca</c> files under a fresh world dir.</summary>
    public static string Write(string root, IEnumerable<RawChunk> chunks)
    {
        string regionDir = Path.Combine(root, "region");
        Directory.CreateDirectory(regionDir);
        foreach (IGrouping<(int RegionX, int RegionZ), RawChunk> g in chunks.GroupBy(c => c.Pos.Region))
            RegionWriter.Write(Path.Combine(regionDir, $"r.{g.Key.RegionX}.{g.Key.RegionZ}.mca"), g);
        return root;
    }
}

/// <summary>A throwaway temp directory, removed on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcahub-world-" + Guid.NewGuid().ToString("N")[..12]);
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ } }
}
