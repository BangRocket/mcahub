using System.Net;
using Microsoft.AspNetCore.Builder;

namespace McaHub.Tests;

/// <summary>
/// Trusted-proxy config (#7): behind a proxy, forwarded headers must be honored only from the configured
/// proxy (default loopback), never from any client — so `ForwardedProxies.Apply` populates KnownProxies /
/// KnownIPNetworks from MCAHUB_TRUSTED_PROXY instead of clearing them.
/// </summary>
public class ForwardedProxiesTests
{
    private static ForwardedHeadersOptions Apply(string? trusted)
    {
        var o = new ForwardedHeadersOptions();
        ForwardedProxies.Apply(o, trusted);
        return o;
    }

    [Fact]
    public void Default_trusts_only_loopback()
    {
        ForwardedHeadersOptions o = Apply(null);
        Assert.Contains(IPAddress.Parse("127.0.0.1"), o.KnownProxies);
        Assert.Contains(IPAddress.IPv6Loopback, o.KnownProxies);
        Assert.Empty(o.KnownIPNetworks);
    }

    [Fact]
    public void A_single_proxy_ip_is_trusted()
    {
        ForwardedHeadersOptions o = Apply("10.0.0.5");
        Assert.Contains(IPAddress.Parse("10.0.0.5"), o.KnownProxies);
    }

    [Fact]
    public void A_cidr_is_added_as_a_network()
    {
        ForwardedHeadersOptions o = Apply("10.0.0.0/8");
        Assert.Single(o.KnownIPNetworks);
        Assert.Empty(o.KnownProxies);
    }

    [Fact]
    public void Mixed_ips_and_cidrs_are_split()
    {
        ForwardedHeadersOptions o = Apply("127.0.0.1, 10.0.0.0/8 192.168.1.1");
        Assert.Equal(2, o.KnownProxies.Count);  // 127.0.0.1 + 192.168.1.1
        Assert.Single(o.KnownIPNetworks);       // 10.0.0.0/8
    }
}
