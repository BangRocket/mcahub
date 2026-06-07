using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace McaHub;

/// <summary>Raised when the Minecraft sign-in chain fails for a known reason (banned, no Xbox profile,
/// child account, doesn't own Java) — the message is safe to show the user.</summary>
public sealed class MinecraftAuthException(string message) : Exception(message);

/// <summary>
/// The Minecraft (Java) sign-in chain (#37): a Microsoft OAuth access token → Xbox Live → XSTS →
/// Minecraft Services → the verified Java UUID + username. Mojang is no longer an IdP, so the only way to
/// a Minecraft identity is this launcher-auth chain on top of a Microsoft token. None of the intermediate
/// Xbox/XSTS/MC tokens are persisted — only the resulting <c>minecraft:&lt;uuid&gt;</c> identity.
/// </summary>
public static class MinecraftAuth
{
    public static async Task<(string Uuid, string Username)> ResolveAsync(string msAccessToken, HttpClient http, CancellationToken ct)
    {
        // Step 2 — Xbox Live: trade the Microsoft token for an XBL token + user hash.
        (string xblToken, _) = await XboxAuth(http, "https://user.auth.xboxlive.com/user/authenticate", XboxBody(msAccessToken), allow401: false, ct);

        // Step 3 — XSTS: authorize the XBL token for the Minecraft relying party (401 carries an XErr reason).
        (string xstsToken, string uhs) = await XboxAuth(http, "https://xsts.auth.xboxlive.com/xsts/authorize", XstsBody(xblToken), allow401: true, ct);

        // Step 4 — Minecraft: exchange the XSTS token for a Minecraft access token.
        string mcToken;
        using (HttpResponseMessage r = await Post(http, "https://api.minecraftservices.com/authentication/login_with_xbox",
            $$"""{"identityToken":"XBL3.0 x={{uhs}};{{xstsToken}}"}""", ct))
        {
            r.EnsureSuccessStatusCode();
            using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            mcToken = d.RootElement.GetProperty("access_token").GetString()!;
        }

        // Step 5 — Profile: the Java entitlement; 404 = this account doesn't own Java.
        using var preq = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        preq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
        using HttpResponseMessage profile = await http.SendAsync(preq, ct);
        if (profile.StatusCode == HttpStatusCode.NotFound)
            throw new MinecraftAuthException("this Microsoft account doesn't own Minecraft: Java Edition — use \"Sign in with Microsoft\" instead.");
        profile.EnsureSuccessStatusCode();
        using var pd = JsonDocument.Parse(await profile.Content.ReadAsStringAsync(ct));
        return (pd.RootElement.GetProperty("id").GetString()!, pd.RootElement.GetProperty("name").GetString()!);
    }

    // ---- pure helpers (unit-tested) ----

    public static string XboxBody(string msAccessToken) =>
        $$"""{"Properties":{"AuthMethod":"RPS","SiteName":"user.auth.xboxlive.com","RpsTicket":"d={{msAccessToken}}"},"RelyingParty":"http://auth.xboxlive.com","TokenType":"JWT"}""";

    public static string XstsBody(string xblToken) =>
        $$"""{"Properties":{"SandboxId":"RETAIL","UserTokens":["{{xblToken}}"]},"RelyingParty":"rp://api.minecraftservices.com/","TokenType":"JWT"}""";

    /// <summary>Map an XSTS 401 body's <c>XErr</c> code to a user-facing reason.</summary>
    public static string XstsErrorMessage(string body)
    {
        long xerr = 0;
        try { if (JsonDocument.Parse(body).RootElement.TryGetProperty("XErr", out JsonElement x)) xerr = x.GetInt64(); }
        catch { /* unparseable → generic */ }
        return xerr switch
        {
            2148916227 => "this Xbox account is banned from Xbox Live.",
            2148916233 => "no Xbox account is linked to this Microsoft account — create an Xbox profile first.",
            2148916235 => "Xbox Live isn't available in this account's country.",
            2148916236 or 2148916237 => "this account needs adult verification before it can use Xbox Live.",
            2148916238 => "this is a child account — it must be added to a Microsoft Family before it can sign in.",
            _ => "Xbox sign-in was rejected (XSTS).",
        };
    }

    private static async Task<(string Token, string Uhs)> XboxAuth(HttpClient http, string url, string body, bool allow401, CancellationToken ct)
    {
        using HttpResponseMessage r = await Post(http, url, body, ct);
        if (allow401 && r.StatusCode == HttpStatusCode.Unauthorized)
            throw new MinecraftAuthException(XstsErrorMessage(await r.Content.ReadAsStringAsync(ct)));
        r.EnsureSuccessStatusCode();
        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        JsonElement root = d.RootElement;
        return (root.GetProperty("Token").GetString()!, root.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!);
    }

    private static Task<HttpResponseMessage> Post(HttpClient http, string url, string json, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Accept.ParseAdd("application/json");
        return http.SendAsync(req, ct);
    }
}
