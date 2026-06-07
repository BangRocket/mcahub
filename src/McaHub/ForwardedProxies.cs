using System.Net;
using Microsoft.AspNetCore.Builder;

namespace McaHub;

/// <summary>
/// Configures which upstreams may set <c>X-Forwarded-*</c> (#7). The framework default of
/// <em>clearing</em> KnownProxies/KnownIPNetworks trusts forwarded headers from <b>any</b> source — so
/// if the app port is reachable directly, an attacker spoofs <c>X-Forwarded-Host</c>/<c>-Proto</c> (OAuth
/// redirect interception, phishing clone URLs, cookie-Secure confusion). Instead we trust only the
/// configured proxy IP(s)/CIDR(s), defaulting to loopback.
/// </summary>
public static class ForwardedProxies
{
    public static void Apply(ForwardedHeadersOptions options, string? trusted)
    {
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        string spec = string.IsNullOrWhiteSpace(trusted) ? "127.0.0.1, ::1" : trusted;
        foreach (string token in spec.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Contains('/'))
            {
                if (IPNetwork.TryParse(token, out IPNetwork net)) options.KnownIPNetworks.Add(net);
            }
            else if (IPAddress.TryParse(token, out IPAddress? ip))
            {
                options.KnownProxies.Add(ip);
            }
        }
    }
}
