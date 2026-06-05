namespace McadiffHub.Tests;

/// <summary>
/// Section-Y clamp (#15 item 1): section Y is read as <c>sbyte</c>, so a crafted chunk with e.g.
/// Y=-128 yields block-Y -2033, which poisons the height array and makes shading paint solid rings
/// around legitimate blocks. Sections outside the valid 1.18+ range [-4, 19] must be ignored.
/// </summary>
public class RenderSectionYTests
{
    [Theory]
    [InlineData(-128)]
    [InlineData(-5)]
    [InlineData(20)]
    [InlineData(127)]
    public void Out_of_range_section_Y_is_skipped(int y)
    {
        using var tmp = new TempDir();
        string world = Worlds.Write(tmp.Path, [Worlds.StoneChunk(0, 0, (sbyte)y)]);
        MapRenderer.Render(world, out MapInfo info, maxChunks: 10_000);
        Assert.Equal(0, info.Chunks); // the poison section is ignored → nothing to render
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(0)]
    [InlineData(19)]
    public void In_range_section_Y_renders(int y)
    {
        using var tmp = new TempDir();
        string world = Worlds.Write(tmp.Path, [Worlds.StoneChunk(0, 0, (sbyte)y)]);
        MapRenderer.Render(world, out MapInfo info, maxChunks: 10_000);
        Assert.Equal(1, info.Chunks);
    }
}
