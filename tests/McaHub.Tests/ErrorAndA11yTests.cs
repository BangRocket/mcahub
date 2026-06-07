using System.Net;

namespace McaHub.Tests;

/// <summary>
/// UX pass (#28): a missing/forbidden world shows a friendly, deliberately-ambiguous 404 (still hides a
/// private world's existence), and every page carries a skip-to-content link for keyboard users.
/// </summary>
public class ErrorAndA11yTests
{
    [Fact]
    public async Task A_missing_world_is_a_friendly_ambiguous_404()
    {
        using var f = new HubFactory(HubMode.Accounts);
        using var c = f.CreateClient();

        HttpResponseMessage resp = await c.GetAsync("/r/nope");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("doesn't exist, or you don't have access", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Every_page_has_a_skip_to_content_link()
    {
        using var f = new HubFactory(HubMode.Open);
        using var c = f.CreateClient();

        string html = await (await c.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("Skip to content", html);
        Assert.Contains("id=\"main\"", html);
    }
}
