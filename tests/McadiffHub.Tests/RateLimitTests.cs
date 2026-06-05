using System.Net;
using System.Net.Http.Headers;

namespace McadiffHub.Tests;

/// <summary>
/// End-to-end checks for #10: a per-IP, per-surface rate limit returns 429 with Retry-After once the
/// window is exhausted, and repeated bad-token attempts lock the IP out (brute-force defense).
/// </summary>
public class RateLimitTests
{
    [Fact]
    public async Task Read_surface_returns_429_with_retry_after_past_the_limit()
    {
        using var f = new HubFactory(HubMode.Open, settings: [new("RateLimitRead", "2")]);
        using HttpClient c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/")).StatusCode);
        HttpResponseMessage third = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);
    }

    [Fact]
    public async Task Repeated_bad_tokens_lock_the_ip_out()
    {
        using var f = new HubFactory(HubMode.Token, masterToken: "right", settings: [new("AuthMaxFailures", "2")]);
        async Task<HttpStatusCode> BadAttempt()
        {
            using HttpClient c = f.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, "/r/anything/objects/0000000000000000000000000000000000000000");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
            return (await c.SendAsync(req)).StatusCode;
        }

        await BadAttempt(); // failure 1
        await BadAttempt(); // failure 2 → locks out
        Assert.Equal(HttpStatusCode.TooManyRequests, await BadAttempt()); // now rejected early
    }
}
