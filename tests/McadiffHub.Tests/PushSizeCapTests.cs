using System.Net;

namespace McadiffHub.Tests;

/// <summary>
/// Push body size cap (#2): the transport buffers a push body into memory, so an oversize body must be
/// rejected before it is fully buffered. The cap is configurable (MaxPushBytes, default 256 MiB); these
/// tests drive it with a tiny limit so the boundary is exercised without moving 256 MiB.
/// </summary>
public class PushSizeCapTests
{
    private const string Obj = "/r/big/objects/0000000000000000000000000000000000000000";

    [Fact]
    public async Task Push_body_over_the_cap_is_rejected_with_413()
    {
        using var f = new HubFactory(HubMode.Open, settings: [new("MaxPushBytes", "1024")]);
        using HttpClient c = f.CreateClient();
        using var content = new ByteArrayContent(new byte[4096]); // 4 KiB > 1 KiB cap
        HttpResponseMessage resp = await c.PostAsync(Obj, content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode); // 413
    }

    [Fact]
    public async Task Push_body_under_the_cap_is_not_rejected_for_size()
    {
        using var f = new HubFactory(HubMode.Open, settings: [new("MaxPushBytes", "1048576")]); // 1 MiB
        using HttpClient c = f.CreateClient();
        using var content = new ByteArrayContent(new byte[1024]); // well under the cap
        HttpResponseMessage resp = await c.PostAsync(Obj, content);
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode); // a bad object → 400, never 413
    }
}
