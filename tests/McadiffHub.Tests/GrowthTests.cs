using System.Net;
using McaDiff.Query;

namespace McadiffHub.Tests;

/// <summary>
/// Growth features (#25): the Discord webhook URL is validated to a real discord.com webhook (an SSRF
/// guard on the push path), the embed payload carries the grief counts, OpenGraph tags point at the map,
/// and the /embed route hides a world you can't see.
/// </summary>
public class GrowthTests
{
    [Theory]
    [InlineData("https://discord.com/api/webhooks/123/abc", true)]
    [InlineData("https://discordapp.com/api/webhooks/123/abc", true)]
    [InlineData("http://discord.com/api/webhooks/123/abc", false)]      // not https
    [InlineData("https://evil.com/api/webhooks/123", false)]
    [InlineData("https://discord.com.evil.com/api/webhooks/x", false)]  // lookalike host
    [InlineData("http://169.254.169.254/latest/meta-data/", false)]     // SSRF target
    [InlineData("", false)]
    public void A_webhook_url_must_be_a_discord_webhook(string url, bool ok) =>
        Assert.Equal(ok, DiscordWebhook.IsValidWebhookUrl(url));

    [Fact]
    public void The_payload_carries_the_grief_counts()
    {
        var grief = new GriefSummary(847, 12, 3, null, null, null, [], []);
        string p = DiscordWebhook.BuildPayload("myworld", "https://h/r/myworld", grief, "abcd123456", "alice");
        Assert.Contains("847", p);
        Assert.Contains("myworld", p);
        Assert.Contains("abcd123456", p);
        Assert.Contains("alice", p);
    }

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
