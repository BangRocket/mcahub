using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace McadiffHub;

/// <summary>
/// Identity for the hub. Two worlds meet here: the <em>web</em> UI authenticates interactively
/// (OAuth → cookie), while the <em>CLI</em> (<c>mcadiff push</c>, which can't run a browser redirect)
/// authenticates with a personal access token minted in the web UI and sent as a Bearer header — the
/// same split GitHub uses for <c>git push</c>. OAuth defaults to GitHub but every endpoint is
/// env-overridable, so a self-hoster can point it at GitLab, Gitea, or any OAuth2 provider.
/// </summary>
public static class Auth
{
    /// <summary>How a provider's identity is mapped. Generic/GitHub/Microsoft share the OAuth-userinfo
    /// mapping; Discord needs a small variant; Minecraft runs the Xbox/XSTS chain instead of a userinfo GET.</summary>
    public enum ProviderKind { Generic, GitHub, Microsoft, Discord, Minecraft }

    /// <summary>One enabled passwordless sign-in provider (a named OAuth scheme).</summary>
    public sealed record Provider(string Name, string Label, ProviderKind Kind, string CallbackPath,
        string ClientId, string ClientSecret, string AuthUrl, string TokenUrl, string UserUrl, string Scope)
    {
        // Never echo the client secret — the default record ToString would dump every field. (audit)
        public override string ToString() => $"Provider {{ Name = {Name}, Kind = {Kind}, ClientId = {ClientId}, ClientSecret = *** }}";
    }

    public sealed record Config(bool DevLogin, IReadOnlyList<Provider> Providers,
        string? MasterPlaintext, IReadOnlyList<string> MasterHashes)
    {
        public bool Accounts => Providers.Count > 0 || DevLogin;
        public bool Oauth => Providers.Count > 0;
        /// <summary>Whether a master/admin token is configured at all (plaintext or hashed).</summary>
        public bool HasMaster => MasterPlaintext is not null || MasterHashes.Count > 0;
        // Redacted so logging or interpolating the config never echoes the master token or a provider secret. (audit)
        public override string ToString() => $"Config {{ DevLogin = {DevLogin}, Providers = {Providers.Count}, Master = {(HasMaster ? "set" : "none")} }}";
    }

    public static Config Read(IConfiguration c)
    {
        string? V(string key, string env) => c[key] ?? Environment.GetEnvironmentVariable(env);
        // config-first then env (matches V), so tests can drive the mode via IConfiguration
        bool Flag(string key, string env) => V(key, env) is "1" or "true" or "TRUE";

        var providers = new List<Provider>();

        // Legacy single generic provider (back-compat): MCAHUB_OAUTH_CLIENT_ID/_SECRET keeps the old
        // /auth/callback path so existing GitHub/GitLab/Gitea setups don't have to re-register.
        if (V("OAuthClientId", "MCAHUB_OAUTH_CLIENT_ID") is { Length: > 0 } gid
            && V("OAuthClientSecret", "MCAHUB_OAUTH_CLIENT_SECRET") is { Length: > 0 } gsecret)
        {
            string name = V("OAuthProvider", "MCAHUB_OAUTH_PROVIDER") ?? "github";
            providers.Add(new Provider(name, Title(name), ProviderKind.Generic, "/auth/callback", gid, gsecret,
                V("OAuthAuthUrl", "MCAHUB_OAUTH_AUTH_URL") ?? "https://github.com/login/oauth/authorize",
                V("OAuthTokenUrl", "MCAHUB_OAUTH_TOKEN_URL") ?? "https://github.com/login/oauth/access_token",
                V("OAuthUserUrl", "MCAHUB_OAUTH_USER_URL") ?? "https://api.github.com/user",
                V("OAuthScope", "MCAHUB_OAUTH_SCOPE") ?? "read:user"));
        }

        // First-class providers, each enabled iff its id+secret are set (same gate as the generic one).
        void Add(string name, string label, ProviderKind kind, string auth, string token, string user, string scope)
        {
            string up = name.ToUpperInvariant();
            if (V($"OAuth{label}ClientId", $"MCAHUB_OAUTH_{up}_CLIENT_ID") is { Length: > 0 } id
                && V($"OAuth{label}ClientSecret", $"MCAHUB_OAUTH_{up}_CLIENT_SECRET") is { Length: > 0 } sec)
                providers.Add(new Provider(name, label, kind, $"/auth/callback/{name}", id, sec, auth, token, user, scope));
        }

        Add("github", "GitHub", ProviderKind.GitHub,
            "https://github.com/login/oauth/authorize", "https://github.com/login/oauth/access_token", "https://api.github.com/user", "read:user");
        string tenant = V("OAuthMicrosoftTenant", "MCAHUB_OAUTH_MICROSOFT_TENANT") ?? "common";
        Add("microsoft", "Microsoft", ProviderKind.Microsoft,
            $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize", $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token", "https://graph.microsoft.com/oidc/userinfo", "openid profile email");
        Add("discord", "Discord", ProviderKind.Discord,
            "https://discord.com/api/oauth2/authorize", "https://discord.com/api/oauth2/token", "https://discord.com/api/users/@me", "identify email");
        Add("minecraft", "Minecraft", ProviderKind.Minecraft, // tenant must be 'consumers' for Minecraft (Xbox) sign-in
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize", "https://login.microsoftonline.com/consumers/oauth2/v2.0/token", "", "XboxLive.signin offline_access");

        string? master = V("PushToken", "MCAHUB_TOKEN");
        string? hashesRaw = V("TokenSha256", "MCAHUB_TOKEN_SHA256");
        string[] hashes = hashesRaw is { Length: > 0 }
            ? hashesRaw.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return new Config(Flag("DevLogin", "MCAHUB_DEV_LOGIN"), providers,
            master is { Length: > 0 } ? master : null, hashes);
    }

    private static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ---- service wiring ----

    public static void AddAuth(WebApplicationBuilder builder, Config cfg, HubDb db)
    {
        if (!cfg.Accounts) return;
        builder.Services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "mcahub_csrf";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // Secure over HTTPS, still works over a local http dev server
        });
        AuthenticationBuilder ab = builder.Services
            .AddAuthentication(o => o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "mcahub_session";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax; // sends on top-level nav, withheld on cross-site POST → CSRF cover
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // Secure over HTTPS
                o.LoginPath = "/auth/login";
                o.LogoutPath = "/auth/logout";
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;
                // Reject a session whose user was deleted (#35) or whose epoch is behind the current one
                // ("sign out everywhere", #18).
                o.Events.OnValidatePrincipal = async context =>
                {
                    if (context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) is { } uid)
                    {
                        HubUser? user = db.GetUser(uid);
                        if (user is null || (context.Principal.FindFirstValue("epoch") ?? "0") != user.Epoch.ToString())
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        }
                    }
                };
            });

        foreach (Provider p in cfg.Providers)
            ab.AddOAuth(p.Name, o =>
            {
                o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.ClientId = p.ClientId;
                o.ClientSecret = p.ClientSecret;
                o.CallbackPath = p.CallbackPath;
                o.AuthorizationEndpoint = p.AuthUrl;
                o.TokenEndpoint = p.TokenUrl;
                if (p.UserUrl.Length > 0) o.UserInformationEndpoint = p.UserUrl;
                o.UsePkce = true;
                foreach (string s in p.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)) o.Scope.Add(s);
                o.Events.OnCreatingTicket = async ctx =>
                {
                    HubUser u = p.Kind == ProviderKind.Minecraft
                        ? await CreateMinecraftUser(ctx, db)
                        : await CreateOAuthUser(ctx, p, db);
                    ctx.Identity!.AddClaims(Claims(u));
                };
            });
    }

    // ---- provider identity mapping ----

    private static async Task<HubUser> CreateOAuthUser(OAuthCreatingTicketContext ctx, Provider p, HubDb db)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ctx.Options.UserInformationEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
        req.Headers.UserAgent.ParseAdd("mcadiff-hub");
        req.Headers.Accept.ParseAdd("application/json");
        using HttpResponseMessage resp = await ctx.Backchannel.SendAsync(req, ctx.HttpContext.RequestAborted);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ctx.HttpContext.RequestAborted));
        (string sub, string login, string name, string avatar) = MapUser(p.Kind, doc.RootElement);
        return db.UpsertUser($"{p.Name}:{sub}", login, name, avatar);
    }

    private static async Task<HubUser> CreateMinecraftUser(OAuthCreatingTicketContext ctx, HubDb db)
    {
        (string uuid, string username) = await MinecraftAuth.ResolveAsync(ctx.AccessToken!, ctx.Backchannel, ctx.HttpContext.RequestAborted);
        HubUser u = db.UpsertUser($"minecraft:{uuid}", username, username, "");
        db.SetMinecraftIdentity(u.Id, uuid, username); // verified Java UUID — the attribution primitive
        return u;
    }

    /// <summary>Map a provider's userinfo JSON to (id, login, name, avatar). Pure — unit-tested per kind.</summary>
    public static (string Sub, string Login, string Name, string Avatar) MapUser(ProviderKind kind, JsonElement root) =>
        kind == ProviderKind.Discord ? MapDiscord(root) : MapGeneric(root);

    private static (string, string, string, string) MapGeneric(JsonElement root)
    {
        string sub = root.TryGetProperty("id", out JsonElement idEl)
            ? (idEl.ValueKind == JsonValueKind.Number ? idEl.GetRawText() : idEl.GetString()!)
            : root.GetProperty("sub").GetString()!;
        string login = Str(root, "login") ?? Str(root, "preferred_username") ?? Str(root, "email") ?? sub;
        return (sub, login, Str(root, "name") ?? login, Str(root, "avatar_url") ?? Str(root, "picture") ?? "");
    }

    private static (string, string, string, string) MapDiscord(JsonElement root)
    {
        string id = root.GetProperty("id").GetString()!;
        string username = Str(root, "username") ?? id;
        string? avatarHash = Str(root, "avatar");
        string avatar = avatarHash is null ? "" : $"https://cdn.discordapp.com/avatars/{id}/{avatarHash}.png";
        return (id, username, Str(root, "global_name") ?? username, avatar);
    }

    // ---- routes ----

    public static void MapAuth(WebApplication app, Config cfg, HubDb db)
    {
        if (!cfg.Accounts) return;

        app.MapGet("/auth/login", (string? provider, string? returnUrl, HttpContext ctx) =>
        {
            var props = new AuthenticationProperties { RedirectUri = Local(returnUrl) };

            // A specific provider was picked → challenge that scheme.
            if (provider is { Length: > 0 } && cfg.Providers.FirstOrDefault(p => p.Name == provider) is { } chosen)
                return Results.Challenge(props, [chosen.Name]);

            // Exactly one way in → go straight there; otherwise render the picker.
            if (cfg.Providers.Count == 1 && !cfg.DevLogin)
                return Results.Challenge(props, [cfg.Providers[0].Name]);
            if (cfg.Providers.Count == 0)
                return cfg.DevLogin ? Results.Redirect("/auth/dev") : Results.Redirect("/");

            string ret = Local(returnUrl);
            var b = new System.Text.StringBuilder("<h1>Sign in</h1><p class=\"meta\">Choose how you'd like to sign in.</p><div class=\"actions\">");
            foreach (Provider p in cfg.Providers)
                b.Append($"""<a class="navlink" href="/auth/login?provider={Html.E(p.Name)}&returnUrl={Html.E(ret)}">Sign in with {Html.E(p.Label)}</a> """);
            if (cfg.DevLogin) b.Append("""<a class="navlink" href="/auth/dev">Dev sign-in</a>""");
            b.Append("</div>");
            return Html.Page("Sign in", b.ToString());
        });

        app.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            if (!await CsrfOk(ctx)) return Results.Text("Invalid or expired form token — reload and retry.", statusCode: 400);
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/");
        });

        if (cfg.DevLogin)
        {
            app.MapGet("/auth/dev", (HttpContext ctx) => Html.Page("Dev sign-in", $"""
                <h1>Dev sign-in</h1>
                <p class="empty">⚠ Insecure local login (<code>MCAHUB_DEV_LOGIN</code>) — for evaluating accounts without
                an OAuth app. Never enable this on a public host.</p>
                <form class="find" method="post" action="/auth/dev">
                  {CsrfField(ctx)}
                  <input name="user" placeholder="username" autofocus>
                  <button>Sign in</button>
                </form>
                """));

            app.MapPost("/auth/dev", async (HttpContext ctx) =>
            {
                if (!await CsrfOk(ctx)) return Results.Text("Invalid or expired form token — reload and retry.", statusCode: 400);
                IFormCollection form = await ctx.Request.ReadFormAsync();
                string name = (form["user"].ToString() ?? "").Trim();
                if (name.Length is 0 or > 40) return Results.Redirect("/auth/dev");
                HubUser u = db.UpsertUser($"dev:{name}", name, name, "");
                var identity = new ClaimsIdentity(Claims(u), CookieAuthenticationDefaults.AuthenticationScheme);
                await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
                return Results.Redirect("/account");
            });
        }
    }

    // ---- web identity (cookie) ----

    public static HubUser? Current(HttpContext ctx)
    {
        string? id = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is null) return null;
        return new HubUser(id, ctx.User.FindFirstValue(ClaimTypes.Name) ?? id, ctx.User.FindFirstValue(ClaimTypes.Name) ?? id,
            ctx.User.FindFirstValue("avatar") ?? "", "");
    }

    public static string HeaderRight(HttpContext ctx, Config cfg)
    {
        if (!cfg.Accounts) return "";
        HubUser? u = Current(ctx);
        if (u is null) return """<a class="navlink" href="/auth/login">Sign in</a>""";
        string av = u.Avatar is { Length: > 0 } ? $"""<img class="avatar" src="{Html.E(u.Avatar)}" alt="">""" : "";
        return $"""<a class="navlink" href="/teams">Teams</a><a class="me" href="/account">{av}{Html.E(u.Login)}</a><form class="logout" method="post" action="/auth/logout">{CsrfField(ctx)}<button class="navlink" type="submit">Sign out</button></form>""";
    }

    // ---- CLI identity (bearer token) ----

    /// <summary>Resolve a request's Bearer token. <c>admin</c> = the configured master token;
    /// otherwise a per-user PAT. Returns null userId+admin=false for anonymous; sets <paramref name="badToken"/>
    /// when a token was presented but matched nothing.</summary>
    public static (string? userId, bool admin, string? scope) Identify(HttpRequest req, Config cfg, HubDb db, out bool badToken)
    {
        badToken = false;
        string? token = Bearer(req);
        if (token is null) return (null, false, null);
        if (IsMasterToken(cfg, token)) return (null, admin: true, null); // master = full admin, no scope limit
        TokenAuth? auth = cfg.Accounts ? db.ResolveToken(token) : null;
        if (auth is null) { badToken = true; return (null, false, null); } // unknown or expired
        return (auth.UserId, false, auth.Scope);
    }

    /// <summary>Constant-time match of a presented token against the configured master secret — the
    /// plaintext <c>MCAHUB_TOKEN</c> and/or any SHA-256 hex in <c>MCAHUB_TOKEN_SHA256</c>. Listing more
    /// than one hash lets you rotate the secret without downtime (both are valid during the window), and
    /// the hashed form keeps the plaintext out of the environment at rest.</summary>
    public static bool IsMasterToken(Config cfg, string presented)
    {
        // Hash the presented token once and compare hashes throughout. FixedTimeEquals is constant-time only
        // across equal-length inputs; comparing raw plaintext bytes leaked the token length via the fast-path
        // on unequal lengths. Hashing first makes every compare 32 bytes — no length oracle. (audit LOW)
        byte[] presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        bool match = false;
        if (cfg.MasterPlaintext is { } pt
            && CryptographicOperations.FixedTimeEquals(presentedHash, SHA256.HashData(Encoding.UTF8.GetBytes(pt))))
            match = true; // don't early-return: keep timing independent of which secret matched
        foreach (string hex in cfg.MasterHashes)
        {
            byte[]? configured = TryHex(hex);
            if (configured is { Length: 32 } && CryptographicOperations.FixedTimeEquals(presentedHash, configured))
                match = true;
        }
        return match;
    }

    private static byte[]? TryHex(string s)
    {
        try { return Convert.FromHexString(s); } catch { return null; }
    }

    // ---- CSRF (antiforgery) for the cookie-authenticated web forms ----

    /// <summary>A hidden antiforgery field to embed in a state-changing form. Issuing it also sets the
    /// antiforgery cookie on the response, so it must be called while rendering (before the body flushes).</summary>
    public static string CsrfField(HttpContext ctx)
    {
        AntiforgeryTokenSet t = ctx.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(ctx);
        return $"""<input type="hidden" name="{t.FormFieldName}" value="{Html.E(t.RequestToken)}">""";
    }

    /// <summary>Validate the antiforgery token on a POST. We validate manually (rather than the middleware)
    /// because the handlers read the form via HttpContext, and so the transport POSTs — which carry no
    /// token — are never touched.</summary>
    public static async Task<bool> CsrfOk(HttpContext ctx)
    {
        try { await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    // ---- access control (shared by web + transport) ----

    /// <summary>Role ladder: owner &gt; admin &gt; maintain &gt; write &gt; read. Capabilities compare rank.</summary>
    private static int Rank(string? role) => role switch
    {
        "owner" => 5, "admin" => 4, "maintain" => 3, "write" => 2, "read" => 1, _ => 0,
    };

    public static bool CanRead(Config cfg, HubDb db, string repo, string? viewerId, bool admin)
    {
        if (!cfg.Accounts) return true;                    // open / shared-token modes: everything public
        if (IsSuspended(db, viewerId)) return false;       // a suspended user is locked out, even of public worlds (#35)
        HubRepoMeta? m = db.GetRepo(repo);
        if (m is null || !m.Private) return true;          // public, or legacy/unowned
        return admin || db.RoleOf(repo, viewerId) is not null; // owner, or any collaborator
    }

    public static bool CanWrite(Config cfg, HubDb db, string repo, string? writerId, bool admin)
    {
        if (admin) return true;
        if (!cfg.Accounts) return true;                    // transport's master-token gate covers token mode
        if (writerId is null) return false;                // accounts mode requires an authenticated PAT
        if (IsSuspended(db, writerId)) return false;       // suspended (#35)
        HubRepoMeta? m = db.GetRepo(repo);
        if (m is null) return true;                        // unowned → claimable on push
        return Rank(db.RoleOf(repo, writerId)) >= 2;       // write, maintain, admin, owner can push
    }

    private static bool IsSuspended(HubDb db, string? userId) => userId is not null && db.GetUser(userId)?.Suspended == true;

    /// <summary>Whether the viewer may see sensitive explorer data (player coords/health, sign text,
    /// inventory). Open/token mode is a trusted LAN, so everyone may; in accounts mode only the owner +
    /// collaborators may, so a public world doesn't doxx its players' locations to strangers. (#34)</summary>
    public static bool CanSeePlayerData(Config cfg, HubDb db, string repo, string? viewerId, bool admin) =>
        !cfg.Accounts || admin || db.RoleOf(repo, viewerId) is not null;

    /// <summary>Change repo settings (visibility): maintain and up, and not suspended.</summary>
    public static bool CanManageSettings(HubDb db, string repo, string? userId) =>
        !IsSuspended(db, userId) && Rank(db.RoleOf(repo, userId)) >= 3;

    /// <summary>Manage collaborators and team grants: admin and up (the owner is admin's superior), not suspended.</summary>
    public static bool CanManagePeople(HubDb db, string repo, string? userId) =>
        !IsSuspended(db, userId) && Rank(db.RoleOf(repo, userId)) >= 4;

    /// <summary>May the caller grant <paramref name="role"/>? Only a non-suspended user of strictly higher rank —
    /// so an admin can grant up to maintain but cannot mint another admin; only the owner grants admin. (audit MED-4)</summary>
    public static bool CanGrantRole(HubDb db, string repo, string? granterId, string role) =>
        !IsSuspended(db, granterId) && Rank(db.RoleOf(repo, granterId)) > Rank(role);

    // ---- helpers ----

    private static Claim[] Claims(HubUser u) =>
    [
        new(ClaimTypes.NameIdentifier, u.Id),
        new(ClaimTypes.Name, u.Login),
        new("avatar", u.Avatar),
        new("epoch", u.Epoch.ToString()), // bumped by "sign out everywhere"; checked on every request
    ];

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string? Bearer(HttpRequest req)
    {
        string? h = req.Headers.Authorization;
        if (string.IsNullOrEmpty(h)) return null;
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h["Bearer ".Length..].Trim() : h.Trim();
    }

    /// <summary>Only permit same-site relative redirect targets — never an absolute URL (open-redirect guard).
    /// Blocks both <c>//host</c> and <c>/\host</c> (legacy browsers treat <c>/\</c> as <c>//</c>).</summary>
    public static string Local(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\')) ? url : "/account";
}
