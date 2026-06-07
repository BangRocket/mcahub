namespace McaHub.Tests;

/// <summary>
/// Multi-dimension maps (#27): each dimension renders from its own region tree (Overworld `region/`,
/// Nether `DIM-1/region/`, End `DIM1/region/`), and the Nether scan skips the bedrock roof so the map
/// shows the playable layer, not the ceiling.
/// </summary>
public class MultiDimensionMapTests
{
    [Fact]
    public void Each_dimension_reads_its_own_region_tree()
    {
        using var tmp = new TempDir();
        Worlds.Write(Path.Combine(tmp.Path, "DIM-1"), [Worlds.StoneChunk(0, 0, sectionY: (sbyte)4)]); // Nether only

        MapRenderer.Render(tmp.Path, MapDimension.Overworld, out MapInfo ow, maxChunks: 1000);
        Assert.Equal(0, ow.Chunks); // the Overworld region/ tree is empty

        MapRenderer.Render(tmp.Path, MapDimension.Nether, out MapInfo nether, maxChunks: 1000);
        Assert.Equal(1, nether.Chunks); // DIM-1/region has the chunk
    }

    [Fact]
    public void Nether_render_skips_the_bedrock_roof()
    {
        // Stone only ABOVE the roof cap (section 8 = y128) → the Nether scan skips it: no surface.
        using var roof = new TempDir();
        Worlds.Write(Path.Combine(roof.Path, "DIM-1"), [Worlds.StoneChunk(0, 0, sectionY: (sbyte)8)]);
        MapRenderer.Render(roof.Path, MapDimension.Nether, out MapInfo above, maxChunks: 1000);
        Assert.Equal(0, above.Chunks);

        // Stone BELOW the roof (section 4 = y64) → the playable layer renders.
        using var play = new TempDir();
        Worlds.Write(Path.Combine(play.Path, "DIM-1"), [Worlds.StoneChunk(0, 0, sectionY: (sbyte)4)]);
        MapRenderer.Render(play.Path, MapDimension.Nether, out MapInfo below, maxChunks: 1000);
        Assert.Equal(1, below.Chunks);
    }
}
