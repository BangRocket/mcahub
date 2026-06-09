using System.Net;

namespace McaHub.Tests;

/// <summary>
/// Growth features (#25): OpenGraph tags point at the map, and the /embed route hides a world you can't
/// see. (The Discord grief-alert + its URL/payload validation move to a Rust-grief follow-up.)
/// </summary>
public class GrowthTests
{
    [Fact]
    public void Og_tags_point_at_the_map_image()
    {
        string og = Html.OgTags("myworld", "847 destroyed", "https://h/r/myworld/map/abc.png");
        Assert.Contains("og:image", og);
        Assert.Contains("https://h/r/myworld/map/abc.png", og);
        Assert.Contains("twitter:card", og);
    }

    [Fact]
    public async Task Embed_404s_for_a_world_you_cannot_see()
    {
        using var f = new HubFactory(HubMode.Accounts);
        using var c = f.CreateClient();
        using HttpResponseMessage resp = await c.GetAsync("/r/never-existed/embed");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
