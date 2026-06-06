namespace McadiffHub.Tests;

/// <summary>
/// Renderer robustness on degenerate worlds (#20), complementing the section-Y clamp (RenderSectionYTests)
/// and the chunk-cap bounds (RenderBoundsTests): an empty world and a single-block-palette world both
/// render to a valid PNG instead of throwing.
/// </summary>
public class RendererHostileTests
{
    [Fact]
    public void An_empty_world_renders_a_valid_png_without_crashing()
    {
        using var tmp = new TempDir();
        string world = Worlds.Write(tmp.Path, []); // region dir exists, no chunks

        byte[] png = MapRenderer.Render(world, out MapInfo info, maxChunks: 10_000);

        Assert.Equal(0, info.Chunks);
        Assert.False(info.Truncated);
        Assert.True(png.Length > 0); // a blank-but-valid PNG
    }

    [Fact]
    public void A_single_palette_chunk_renders()
    {
        using var tmp = new TempDir();
        string world = Worlds.Write(tmp.Path, [Worlds.StoneChunk(0, 0)]); // one block state in the palette

        byte[] png = MapRenderer.Render(world, out MapInfo info, maxChunks: 10_000);

        Assert.Equal(1, info.Chunks);
        Assert.True(png.Length > 0);
    }

    [Fact]
    public void Extreme_chunk_coords_dont_overflow_the_dimension_clamp()
    {
        // Two chunks ~4 billion apart in X (a hostile manifest can name region files at any int coord).
        // The span exceeds int range: `maxCX - minCX + 1` in int wraps and would bypass the 160-chunk clamp
        // into a huge/negative allocation. The clamp must hold and bound the canvas to ≤ 2560px per side.
        using var tmp = new TempDir();
        string world = Worlds.Write(tmp.Path,
        [
            Worlds.StoneChunk(2_000_000_000, 0),
            Worlds.StoneChunk(-2_000_000_000, 0),
        ]);

        // Pre-fix this threw OverflowException/OutOfMemoryException from `new byte[w*h*3]` (wrapped span).
        byte[] png = MapRenderer.Render(world, out MapInfo info, maxChunks: 10_000);

        Assert.True(info.Truncated);                       // the runaway span was clamped, not bypassed
        Assert.InRange(info.Width, 1, 2560);               // bounded canvas, not gigabytes / negative
        Assert.InRange(info.Height, 1, 2560);
        Assert.True(png.Length > 0);                       // a valid (if mostly-empty) PNG, no crash
    }
}
