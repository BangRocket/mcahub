using System.Net;

namespace McadiffHub;

/// <summary>
/// Fail-closed checks for the two riskiest operational defaults (#9): open mode (anonymous
/// read+write+create) and dev-login (passwordless sign-in) must not be reachable on a non-loopback
/// interface — open mode only behind an explicit override, dev-login never. A pure function of the
/// resolved auth mode and the configured bind URLs, so it's deterministic and testable.
/// </summary>
public static class StartupGuard
{
    /// <summary>Returns a refusal reason if the configured bind would expose open mode or dev-login off
    /// loopback, or null if it's safe to start.</summary>
    public static string? PublicExposureViolation(bool accounts, string? masterToken, bool devLogin, string bindUrls, bool allowPublicOpen)
    {
        if (AllLoopback(bindUrls)) return null; // loopback-only bind: nothing is exposed

        if (devLogin)
            return $"MCAHUB_DEV_LOGIN (passwordless sign-in) must never be reachable off-host, but the bind '{bindUrls}' is not loopback.";

        bool openMode = !accounts && string.IsNullOrEmpty(masterToken);
        if (openMode && !allowPublicOpen)
            return $"open mode (anonymous read+write+create) on a non-loopback bind '{bindUrls}'. Set MCAHUB_TOKEN, configure OAuth, bind to loopback, or set MCAHUB_I_KNOW_OPEN_MODE_IS_PUBLIC=1 if that's intended.";

        return null;
    }

    /// <summary>True only if every bind URL targets the loopback interface.</summary>
    public static bool AllLoopback(string bindUrls) =>
        bindUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 0
        && bindUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(IsLoopback);

    private static bool IsLoopback(string url)
    {
        string host = HostOf(url);
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host is "*" or "+" or "0.0.0.0" or "::") return false;       // wildcard binds = all interfaces = public
        return IPAddress.TryParse(host, out IPAddress? ip) && IPAddress.IsLoopback(ip); // a hostname (not localhost) → treat as public
    }

    private static string HostOf(string url)
    {
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        string rest = scheme >= 0 ? url[(scheme + 3)..] : url;
        int slash = rest.IndexOf('/');
        if (slash >= 0) rest = rest[..slash];
        if (rest.StartsWith('['))                  // IPv6 literal: [::1]:port
        {
            int close = rest.IndexOf(']');
            return close > 0 ? rest[1..close] : rest;
        }
        int colon = rest.IndexOf(':');
        return colon >= 0 ? rest[..colon] : rest;
    }
}
