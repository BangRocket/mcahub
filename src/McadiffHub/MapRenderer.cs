using System.IO.Compression;
using fNbt;
using McaDiff.Anvil;
using McaDiff.Diff;

namespace McadiffHub;

public sealed record MapInfo(int Width, int Height, int Chunks, bool Truncated, int RegionsRead = 0);

/// <summary>
/// Renders a top-down surface map of a materialized world (the <c>region/*.mca</c> files) to a PNG —
/// the visual companion to the semantic diff. For each block column it scans sections top-down for the
/// first non-air block, maps it to a color, then applies Minecraft-style north-facing height shading so
/// terrain relief reads. Targets the modern (1.18+) chunk layout (root <c>sections</c> +
/// <c>block_states</c>); chunks it can't decode are simply left blank. Self-contained PNG writer, no
/// image dependency.
/// </summary>
public static class MapRenderer
{
    private const int MaxSideChunks = 160; // cap the rendered span at 160×160 chunks (2560px); bigger worlds truncate

    public static byte[] Render(string worldDir, out MapInfo info, int maxChunks = 10_000, CancellationToken ct = default)
    {
        string regionDir = Path.Combine(worldDir, "region");
        var surfaces = new Dictionary<ChunkPos, Surface>();
        int minCX = int.MaxValue, minCZ = int.MaxValue, maxCX = int.MinValue, maxCZ = int.MinValue;

        bool capHit = false;
        int regionsRead = 0;
        if (Directory.Exists(regionDir))
            foreach (string file in Directory.EnumerateFiles(regionDir, "r.*.mca").OrderBy(f => f, StringComparer.Ordinal))
            {
                if (capHit) break;                  // (#14) once full, stop opening — don't read every remaining .mca
                ct.ThrowIfCancellationRequested();  // a client disconnect / request timeout aborts the render
                RegionFile region;
                try { region = RegionFile.Open(file); }
                catch { continue; } // skip an unreadable region rather than fail the whole map
                regionsRead++;
                foreach (RawChunk raw in region.Chunks)
                {
                    if (surfaces.Count >= maxChunks) { capHit = true; break; }
                    ct.ThrowIfCancellationRequested();
                    Surface? s = SurfaceOf(raw);
                    if (s is null) continue;
                    surfaces[raw.Pos] = s;
                    minCX = Math.Min(minCX, raw.Pos.X); maxCX = Math.Max(maxCX, raw.Pos.X);
                    minCZ = Math.Min(minCZ, raw.Pos.Z); maxCZ = Math.Max(maxCZ, raw.Pos.Z);
                }
            }

        if (surfaces.Count == 0) { info = new MapInfo(0, 0, 0, false, regionsRead); return EmptyPng(); }

        // Clamp a runaway span to a window centered on the populated area. A hit chunk-cap also truncates.
        // Chunk coords come from attacker-controlled region filenames (r.<X>.<Z>.mca) and span the full int
        // range, so the span and midpoint are computed in LONG: `max - min + 1` in int would overflow and
        // wrap past the `> MaxSideChunks` test, bypassing the clamp into a huge/negative allocation. After
        // clamping, each span is provably ≤ MaxSideChunks, so the int math below can't overflow (w,h ≤ 2560).
        bool truncated = capHit;
        if ((long)maxCX - minCX + 1 > MaxSideChunks) { long c = ((long)minCX + maxCX) / 2; minCX = (int)(c - MaxSideChunks / 2); maxCX = minCX + MaxSideChunks - 1; truncated = true; }
        if ((long)maxCZ - minCZ + 1 > MaxSideChunks) { long c = ((long)minCZ + maxCZ) / 2; minCZ = (int)(c - MaxSideChunks / 2); maxCZ = minCZ + MaxSideChunks - 1; truncated = true; }

        int w = (maxCX - minCX + 1) * 16, h = (maxCZ - minCZ + 1) * 16;
        byte[] rgb = new byte[w * h * 3];
        int[] height = new int[w * h];
        Array.Fill(height, int.MinValue);
        FillBackground(rgb, w, h);

        int placed = 0;
        foreach ((ChunkPos pos, Surface s) in surfaces)
        {
            ct.ThrowIfCancellationRequested();
            int baseCol = (pos.X - minCX) * 16, baseRow = (pos.Z - minCZ) * 16;
            if (baseCol < 0 || baseRow < 0 || baseCol + 16 > w || baseRow + 16 > h) continue; // outside the window
            for (int lz = 0; lz < 16; lz++)
                for (int lx = 0; lx < 16; lx++)
                {
                    int cell = lz * 16 + lx;
                    if (s.Y[cell] == int.MinValue) continue;
                    int p = (baseRow + lz) * w + (baseCol + lx);
                    rgb[p * 3] = s.R[cell]; rgb[p * 3 + 1] = s.G[cell]; rgb[p * 3 + 2] = s.B[cell];
                    height[p] = s.Y[cell];
                }
            placed++;
        }

        NorthShade(rgb, height, w, h);
        info = new MapInfo(w, h, placed, truncated, regionsRead);
        return EncodePng(w, h, rgb);
    }

    // ---- per-chunk surface scan ----

    private sealed class Surface { public byte[] R = new byte[256]; public byte[] G = new byte[256]; public byte[] B = new byte[256]; public int[] Y = new int[256]; }

    private static Surface? SurfaceOf(RawChunk raw)
    {
        NbtCompound root;
        try { root = ChunkCodec.Decode(raw); }
        catch { return null; } // LZ4-custom / corrupt → no surface
        if (root.Get<NbtList>("sections") is not { Count: > 0 } sections) return null;

        // Decode each section's block grid once, tallest first.
        var decoded = new List<(int Y, string[] Cells)>();
        foreach (NbtTag tag in sections)
        {
            if (tag is not NbtCompound sec) continue;
            if (sec.Get<NbtByte>("Y") is not { } yTag) continue;
            sbyte secY = (sbyte)yTag.Value;
            if (secY < -4 || secY > 19) continue; // (#15) ignore sections outside the 1.18+ range; a crafted
                                                  // Y=-128 otherwise poisons height[] and corrupts shading
            if (sec.Get<NbtCompound>("block_states") is not { } bs) continue;
            if (BlockStateDecoder.Decode(bs, 4096, BlockStateDecoder.BlockMinBits) is { } cells)
                decoded.Add((secY, cells));
        }
        if (decoded.Count == 0) return null;
        decoded.Sort((a, b) => b.Y.CompareTo(a.Y));

        var s = new Surface();
        Array.Fill(s.Y, int.MinValue);
        for (int lz = 0; lz < 16; lz++)
            for (int lx = 0; lx < 16; lx++)
            {
                int outCell = lz * 16 + lx;
                foreach ((int secY, string[] cells) in decoded)
                {
                    bool found = false;
                    for (int ly = 15; ly >= 0; ly--)
                    {
                        string name = Strip(cells[(ly * 16 + lz) * 16 + lx]); // i = (y*16 + z)*16 + x
                        if (IsAir(name)) continue;
                        (byte r, byte g, byte b) = ColorOf(name);
                        s.R[outCell] = r; s.G[outCell] = g; s.B[outCell] = b; s.Y[outCell] = secY * 16 + ly;
                        found = true;
                        break;
                    }
                    if (found) break;
                }
            }
        return s;
    }

    // ---- shading ----

    private static void NorthShade(byte[] rgb, int[] height, int w, int h)
    {
        // Compare each pixel's surface height to the pixel to its north; brighten if higher, darken if lower.
        for (int row = h - 1; row >= 1; row--)
            for (int col = 0; col < w; col++)
            {
                int p = row * w + col, n = (row - 1) * w + col;
                if (height[p] == int.MinValue || height[n] == int.MinValue) continue;
                int d = height[p] - height[n];
                if (d == 0) continue;
                double f = d > 0 ? 1.12 : 0.86;
                rgb[p * 3] = Clamp(rgb[p * 3] * f);
                rgb[p * 3 + 1] = Clamp(rgb[p * 3 + 1] * f);
                rgb[p * 3 + 2] = Clamp(rgb[p * 3 + 2] * f);
            }
    }

    private static byte Clamp(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    private static void FillBackground(byte[] rgb, int w, int h)
    {
        for (int i = 0; i < w * h; i++) { rgb[i * 3] = 17; rgb[i * 3 + 1] = 22; rgb[i * 3 + 2] = 28; } // slate, matches UI bg
    }

    // ---- block → color ----

    private static string Strip(string key)
    {
        int b = key.IndexOf('[');
        if (b >= 0) key = key[..b];
        return key.StartsWith("minecraft:", StringComparison.Ordinal) ? key["minecraft:".Length..] : key;
    }

    private static bool IsAir(string n) => n is "air" or "cave_air" or "void_air";

    private static (byte, byte, byte) ColorOf(string n)
    {
        if (n.Contains("water")) return (63, 118, 228);
        if (n.Contains("lava")) return (224, 108, 29);
        if (n is "grass_block" or "grass" or "tall_grass" or "fern" or "large_fern" || n.Contains("moss")) return (98, 160, 75);
        if (n.Contains("leaves")) return (62, 124, 49);
        if (n.EndsWith("log", StringComparison.Ordinal) || n.Contains("planks") || n.Contains("wood") || n.Contains("stem")) return (120, 90, 55);
        if (n.Contains("sandstone")) return (199, 182, 130);
        if (n.Contains("sand")) return (219, 205, 158);
        if (n.Contains("podzol") || n.Contains("mud") || n is "coarse_dirt" or "rooted_dirt" || n.Contains("dirt") || n.Contains("farmland") || n.Contains("path")) return (134, 96, 67);
        if (n == "powder_snow" || n.Contains("snow")) return (245, 247, 250);
        if (n.Contains("packed_ice") || n.Contains("ice")) return (150, 190, 240);
        if (n.Contains("clay")) return (160, 166, 179);
        if (n.Contains("gravel")) return (130, 127, 124);
        if (n.Contains("deepslate")) return (70, 70, 75);
        if (n.Contains("blackstone")) return (42, 40, 46);
        if (n.Contains("basalt")) return (78, 78, 84);
        if (n.Contains("obsidian")) return (24, 20, 38);
        if (n.Contains("netherrack") || n.Contains("nether_wart")) return (97, 38, 38);
        if (n.Contains("bedrock")) return (40, 40, 40);
        if (n.Contains("cobblestone") || n.Contains("stone") || n.Contains("andesite") || n.Contains("granite") || n.Contains("diorite") || n == "tuff") return (122, 122, 122);
        if (n.Contains("gold")) return (232, 206, 99);
        if (n.Contains("diamond")) return (110, 221, 213);
        if (n.Contains("coal")) return (54, 54, 58);
        if (n.Contains("glass")) return (200, 225, 235);
        if (n.Contains("brick")) return (150, 90, 80);
        return ColorFromName(n); // wool/concrete/terracotta/unknown → stable muted color
    }

    private static (byte, byte, byte) ColorFromName(string n)
    {
        int h = 0;
        foreach (char c in n) h = h * 31 + c;
        uint x = (uint)h;
        return ((byte)(80 + (x & 0x4F)), (byte)(80 + ((x >> 6) & 0x4F)), (byte)(80 + ((x >> 12) & 0x4F)));
    }

    // ---- minimal PNG writer (RGB, no filter) ----

    private static readonly byte[] Sig = [137, 80, 78, 71, 13, 10, 26, 10];

    private static byte[] EncodePng(int w, int h, byte[] rgb)
    {
        using var ms = new MemoryStream();
        ms.Write(Sig);

        byte[] ihdr = new byte[13];
        WriteBE(ihdr, 0, w); WriteBE(ihdr, 4, h);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // color type: truecolor RGB
        WriteChunk(ms, "IHDR", ihdr);

        byte[] raw = new byte[h * (1 + w * 3)];
        for (int y = 0; y < h; y++)
        {
            raw[y * (1 + w * 3)] = 0; // filter: None
            Array.Copy(rgb, y * w * 3, raw, y * (1 + w * 3) + 1, w * 3);
        }
        byte[] comp;
        using (var cs = new MemoryStream())
        {
            using (var z = new ZLibStream(cs, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
            comp = cs.ToArray();
        }
        WriteChunk(ms, "IDAT", comp);
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] EmptyPng()
    {
        byte[] one = [17, 22, 28];
        return EncodePng(1, 1, one);
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        byte[] len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len);
        byte[] t = [(byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]];
        s.Write(t);
        s.Write(data);
        uint crc = Crc32(t, data);
        byte[] c = new byte[4]; WriteBE(c, 0, (int)crc); s.Write(c);
    }

    private static void WriteBE(byte[] buf, int off, int v)
    {
        buf[off] = (byte)(v >> 24); buf[off + 1] = (byte)(v >> 16); buf[off + 2] = (byte)(v >> 8); buf[off + 3] = (byte)v;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (byte x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
