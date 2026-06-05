namespace McadiffHub.Tests;

/// <summary>
/// Renderer resource bounds (#14): the chunk cap must bound the work regardless of world size, and the
/// renderer must stop opening region files once the cap is hit (rather than reading every remaining
/// <c>.mca</c>). A capped render truncates instead of running to completion.
/// </summary>
public class RenderBoundsTests
{
    [Fact]
    public void Renders_every_chunk_of_a_small_world_under_the_cap()
    {
        using var tmp = new TempDir();
        string world = Worlds.Write(tmp.Path, Enumerable.Range(0, 4).Select(i => Worlds.StoneChunk(i, 0)));
        byte[] png = MapRenderer.Render(world, out MapInfo info, maxChunks: 10_000);
        Assert.Equal(4, info.Chunks);
        Assert.False(info.Truncated);
        Assert.True(png.Length > 0);
    }

    [Fact]
    public void Chunk_cap_bounds_placement_and_marks_truncated()
    {
        using var tmp = new TempDir();
        string world = Worlds.Write(tmp.Path, Enumerable.Range(0, 8).Select(i => Worlds.StoneChunk(i, 0)));
        MapRenderer.Render(world, out MapInfo info, maxChunks: 3);
        Assert.Equal(3, info.Chunks);
        Assert.True(info.Truncated);
    }

    [Fact]
    public void Stops_reading_region_files_once_the_cap_is_hit()
    {
        using var tmp = new TempDir();
        // region r.0.0 (x=0..7) fills a cap of 3; region r.1.0 (x=32..35) must not be opened.
        var chunks = Enumerable.Range(0, 8).Select(i => Worlds.StoneChunk(i, 0))
            .Concat(Enumerable.Range(32, 4).Select(i => Worlds.StoneChunk(i, 0)));
        string world = Worlds.Write(tmp.Path, chunks);
        MapRenderer.Render(world, out MapInfo info, maxChunks: 3);
        Assert.Equal(3, info.Chunks);
        Assert.Equal(1, info.RegionsRead); // only the first region file was read
    }
}
