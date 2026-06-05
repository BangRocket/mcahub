using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public sealed record Config(
        bool Accounts, bool Oauth, bool DevLogin, string Provider,
        string? ClientId, string? ClientSecret,
        string AuthUrl, string TokenUrl, string UserUrl, string Scope,
        string? MasterToken);

    public static Config Read(IConfiguration c)
    {
        string? V(string key, string env) => c[key] ?? Environment.GetEnvironmentVariable(env);
        bool Flag(string env) => Environment.GetEnvironmentVariable(env) is "1" or "true" or "TRUE";

        string? clientId = V("OAuthClientId", "MCAHUB_OAUTH_CLIENT_ID");
        string? clientSecret = V("OAuthClientSecret", "MCAHUB_OAUTH_CLIENT_SECRET");
        bool oauth = clientId is { Length: > 0 } && clientSecret is { Length: > 0 };
        bool dev = Flag("MCAHUB_DEV_LOGIN");
        return new Config(
            Accounts: oauth || dev,
            Oauth: oauth,
            DevLogin: dev,
            Provider: V("OAuthProvider", "MCAHUB_OAUTH_PROVIDER") ?? "github",
            ClientId: clientId,
            ClientSecret: clientSecret,
            AuthUrl: V("OAuthAuthUrl", "MCAHUB_OAUTH_AUTH_URL") ?? "https://github.com/login/oauth/authorize",
            TokenUrl: V("OAuthTokenUrl", "MCAHUB_OAUTH_TOKEN_URL") ?? "https://github.com/login/oauth/access_token",
            UserUrl: V("OAuthUserUrl", "MCAHUB_OAUTH_USER_URL") ?? "https://api.github.com/user",
            Scope: V("OAuthScope", "MCAHUB_OAUTH_SCOPE") ?? "read:user",
            MasterToken: V("PushToken", "MCAHUB_TOKEN"));
    }

    // ---- service wiring ----

    public static void AddAuth(WebApplicationBuilder builder, Config cfg, HubDb db)
    {
        if (!cfg.Accounts) return;
        AuthenticationBuilder ab = builder.Services
            .AddAuthentication(o => o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "mcahub_session";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax; // sends on top-level nav, withheld on cross-site POST → CSRF cover
                o.LoginPath = "/auth/login";
                o.LogoutPath = "/auth/logout";
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;
            });

        if (cfg.Oauth)
            ab.AddOAuth("oauth", o =>
            {
                o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.ClientId = cfg.ClientId!;
                o.ClientSecret = cfg.ClientSecret!;
                o.CallbackPath = "/auth/callback";
                o.AuthorizationEndpoint = cfg.AuthUrl;
                o.TokenEndpoint = cfg.TokenUrl;
                o.UserInformationEndpoint = cfg.UserUrl;
                o.UsePkce = true;
                foreach (string s in cfg.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)) o.Scope.Add(s);
                o.Events.OnCreatingTicket = async ctx =>
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, o.UserInformationEndpoint);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
                    req.Headers.UserAgent.ParseAdd("mcadiff-hub");
                    req.Headers.Accept.ParseAdd("application/json");
                    using HttpResponseMessage resp = await ctx.Backchannel.SendAsync(req, ctx.HttpContext.RequestAborted);
                    resp.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ctx.HttpContext.RequestAborted));
                    JsonElement root = doc.RootElement;

                    string sub = root.TryGetProperty("id", out JsonElement idEl)
                        ? (idEl.ValueKind == JsonValueKind.Number ? idEl.GetRawText() : idEl.GetString()!)
                        : root.GetProperty("sub").GetString()!;
                    string login = Str(root, "login") ?? Str(root, "preferred_username") ?? Str(root, "email") ?? sub;
                    HubUser u = db.UpsertUser(
                        $"{cfg.Provider}:{sub}", login,
                        Str(root, "name") ?? login,
                        Str(root, "avatar_url") ?? Str(root, "picture") ?? "");
                    ctx.Identity!.AddClaims(Claims(u));
                };
            });
    }

    // ---- routes ----

    public static void MapAuth(WebApplication app, Config cfg, HubDb db)
    {
        if (!cfg.Accounts) return;

        app.MapGet("/auth/login", (string? returnUrl, HttpContext ctx) =>
        {
            if (cfg.Oauth)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = Local(returnUrl) }, ["oauth"]);
            return cfg.DevLogin ? Results.Redirect("/auth/dev") : Results.Redirect("/");
        });

        app.MapGet("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/");
        });

        if (cfg.DevLogin)
        {
            app.MapGet("/auth/dev", () => Html.Page("Dev sign-in", """
                <h1>Dev sign-in</h1>
                <p class="empty">⚠ Insecure local login (<code>MCAHUB_DEV_LOGIN</code>) — for evaluating accounts without
                an OAuth app. Never enable this on a public host.</p>
                <form class="find" method="post" action="/auth/dev">
                  <input name="user" placeholder="username" autofocus>
                  <button>Sign in</button>
                </form>
                """));

            app.MapPost("/auth/dev", async (HttpContext ctx) =>
            {
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
        return $"""<a class="navlink" href="/teams">Teams</a><a class="me" href="/account">{av}{Html.E(u.Login)}</a><a class="navlink" href="/auth/logout">Sign out</a>""";
    }

    // ---- CLI identity (bearer token) ----

    /// <summary>Resolve a request's Bearer token. <c>admin</c> = the configured master token;
    /// otherwise a per-user PAT. Returns null userId+admin=false for anonymous; sets <paramref name="badToken"/>
    /// when a token was presented but matched nothing.</summary>
    public static (string? userId, bool admin) Identify(HttpRequest req, Config cfg, HubDb db, out bool badToken)
    {
        badToken = false;
        string? token = Bearer(req);
        if (token is null) return (null, false);
        if (cfg.MasterToken is not null &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(cfg.MasterToken)))
            return (null, admin: true);
        string? uid = cfg.Accounts ? db.ResolveToken(token) : null;
        if (uid is null) badToken = true;
        return (uid, false);
    }

    // ---- access control (shared by web + transport) ----

    public static bool CanRead(Config cfg, HubDb db, string repo, string? viewerId, bool admin)
    {
        if (!cfg.Accounts) return true;                    // open / shared-token modes: everything public
        HubRepoMeta? m = db.GetRepo(repo);
        if (m is null || !m.Private) return true;          // public, or legacy/unowned
        return admin || db.RoleOf(repo, viewerId) is not null; // owner, or read/write collaborator
    }

    public static bool CanWrite(Config cfg, HubDb db, string repo, string? writerId, bool admin)
    {
        if (admin) return true;
        if (!cfg.Accounts) return true;                    // transport's master-token gate covers token mode
        if (writerId is null) return false;                // accounts mode requires an authenticated PAT
        HubRepoMeta? m = db.GetRepo(repo);
        if (m is null) return true;                        // unowned → claimable on push
        return db.RoleOf(repo, writerId) is "owner" or "write"; // owner or write collaborator
    }

    // ---- helpers ----

    private static Claim[] Claims(HubUser u) =>
    [
        new(ClaimTypes.NameIdentifier, u.Id),
        new(ClaimTypes.Name, u.Login),
        new("avatar", u.Avatar),
    ];

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string? Bearer(HttpRequest req)
    {
        string? h = req.Headers.Authorization;
        if (string.IsNullOrEmpty(h)) return null;
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h["Bearer ".Length..].Trim() : h.Trim();
    }

    /// <summary>Only permit same-site relative redirect targets — never an absolute URL (open-redirect guard).</summary>
    private static string Local(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && (url.Length == 1 || url[1] != '/') ? url : "/account";
}
