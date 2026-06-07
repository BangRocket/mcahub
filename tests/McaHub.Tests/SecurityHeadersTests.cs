namespace McaHub.Tests;

/// <summary>
/// Security response headers (#8): every response carries CSP + X-Frame-Options + X-Content-Type-Options
/// + Referrer-Policy as a browser-enforced backstop against clickjacking, MIME-sniffing, and XSS.
/// </summary>
public class SecurityHeadersTests
{
    [Fact]
    public async Task Responses_carry_the_security_headers()
    {
        using var f = new HubFactory(HubMode.Open);
        using HttpClient c = f.CreateClient();
        HttpResponseMessage resp = await c.GetAsync("/");

        Assert.Equal("nosniff", One(resp, "X-Content-Type-Options"));
        Assert.Equal("SAMEORIGIN", One(resp, "X-Frame-Options"));
        Assert.Equal("same-origin", One(resp, "Referrer-Policy"));
        string csp = One(resp, "Content-Security-Policy");
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self'", csp);     // no 'unsafe-inline'
        Assert.Contains("frame-ancestors 'self'", csp);
    }

    private static string One(HttpResponseMessage resp, string header) => resp.Headers.GetValues(header).Single();
}
