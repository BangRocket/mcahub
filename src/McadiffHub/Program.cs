using System.Threading.RateLimiting;
using McadiffHub;
using Microsoft.AspNetCore.HttpOverrides;

LoadDotEnv(Path.Combine(Directory.GetCurrentDirectory(), ".env")); // pull MCAHUB_* / OAuth creds out of a local .env

// Standalone utility: render a world directory's top-down map to a PNG without running the server.
if (args is ["render", var worldDir, var outPath])
{
    byte[] png = MapRenderer.Render(worldDir, out MapInfo mi);
    File.WriteAllBytes(outPath, png);
    Console.WriteLine($"rendered {mi.Width}x{mi.Height} from {mi.Chunks} chunks{(mi.Truncated ? " (truncated)" : "")} -> {outPath}");
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Default to a friendly local port unless the host overrides ASPNETCORE_URLS.
if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null && !builder.Configuration.GetSection("urls").Exists())
    builder.WebHost.UseUrls("http://localhost:5080");

string dataDir = builder.Configuration["DataDir"] ?? Environment.GetEnvironmentVariable("MCAHUB_DATA") ?? "data/repos";
string sibling = Path.GetDirectoryName(Path.GetFullPath(dataDir))!;
string cacheDir = builder.Configuration["CacheDir"] ?? Environment.GetEnvironmentVariable("MCAHUB_CACHE") ?? Path.Combine(sibling, "cache");
string mapDir = builder.Configuration["MapDir"] ?? Environment.GetEnvironmentVariable("MCAHUB_MAPS") ?? Path.Combine(sibling, "maps");
string dbPath = builder.Configuration["DbPath"] ?? Environment.GetEnvironmentVariable("MCAHUB_DB") ?? Path.Combine(sibling, "hub.json");
string auditPath = builder.Configuration["AuditPath"] ?? Environment.GetEnvironmentVariable("MCAHUB_AUDIT") ?? Path.Combine(sibling, "audit.jsonl");

// Cap how much a single push may buffer. Kestrel's default is 30 MB; worlds can be larger, so we raise
// it to 256 MiB (matching the mcadiff core's RepoServer) and enforce the same ceiling while streaming.
long maxPushBytes = ParsePositiveLong(builder.Configuration["MaxPushBytes"] ?? Environment.GetEnvironmentVariable("MCAHUB_MAX_PUSH_BYTES")) ?? 256L * 1024 * 1024;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxPushBytes);

// Bound cold map rendering: cap concurrent renders, and give each a hard server-side deadline so a slow
// (or hostile) world can't tie up a request thread indefinitely. Client disconnects cancel too.
int renderConcurrency = (int)(ParsePositiveLong(builder.Configuration["MaxRenderConcurrency"] ?? Environment.GetEnvironmentVariable("MCAHUB_MAX_RENDER_CONCURRENCY")) ?? 3);
int maxRenderChunks = (int)(ParsePositiveLong(builder.Configuration["MaxRenderChunks"] ?? Environment.GetEnvironmentVariable("MCAHUB_MAX_RENDER_CHUNKS")) ?? 10_000);
TimeSpan renderTimeout = TimeSpan.FromSeconds(ParsePositiveLong(builder.Configuration["RenderTimeoutSeconds"] ?? Environment.GetEnvironmentVariable("MCAHUB_RENDER_TIMEOUT_SECONDS")) ?? 30);
builder.Services.AddRequestTimeouts(o => o.AddPolicy(Pages.RenderTimeoutPolicy, renderTimeout));
// Graceful drain on SIGTERM (#41): stop accepting connections and let in-flight requests finish — a deploy
// drains instead of guillotining a render. Give it a hair over the render deadline so a render can complete.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = renderTimeout + TimeSpan.FromSeconds(5));
builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); o.IncludeSubDomains = true; }); // emitted only on HTTPS

// Per-IP rate limits per surface (fixed 1-minute windows). Behind a proxy, RemoteIpAddress is accurate
// only once forwarded-headers handling sets it (see the proxy issue) — the limiter picks it up then.
int RateLimit(string key, string env, int def) => (int)(ParsePositiveLong(builder.Configuration[key] ?? Environment.GetEnvironmentVariable(env)) ?? def);
int rlAuth = RateLimit("RateLimitAuth", "MCAHUB_RATELIMIT_AUTH", 20);
int rlWrite = RateLimit("RateLimitWrite", "MCAHUB_RATELIMIT_WRITE", 60);
int rlRender = RateLimit("RateLimitRender", "MCAHUB_RATELIMIT_RENDER", 30);
int rlRead = RateLimit("RateLimitRead", "MCAHUB_RATELIMIT_READ", 300);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, _) => { ctx.HttpContext.Response.Headers.RetryAfter = "60"; return ValueTask.CompletedTask; };
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        (string cat, int limit) = RateCategory(ctx.Request, rlAuth, rlWrite, rlRender, rlRead);
        return RateLimitPartition.GetFixedWindowLimiter($"{cat}:{ip}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        });
    });
});

// Failure-specific bad-token lockout (brute-force defense), wired to the transport's badToken signal.
var authThrottle = new AuthThrottle(
    maxFailures: RateLimit("AuthMaxFailures", "MCAHUB_AUTH_MAX_FAILURES", 5),
    baseCooldown: TimeSpan.FromSeconds(RateLimit("AuthLockoutSeconds", "MCAHUB_AUTH_LOCKOUT_SECONDS", 30)));

// Bound the on-disk caches so a hostile or just-busy pusher can't fill the disk (which would also break
// the account store's atomic write): a global byte ceiling + per-repo count cap for each, plus a cap on
// how many filesystem entries one world manifest may materialize (inode-exhaustion guard).
long CacheGb(string key, string env, long def) => (ParsePositiveLong(builder.Configuration[key] ?? Environment.GetEnvironmentVariable(env)) ?? def) * 1024L * 1024 * 1024;
int CacheInt(string key, string env, int def) => (int)(ParsePositiveLong(builder.Configuration[key] ?? Environment.GetEnvironmentVariable(env)) ?? def);
var cacheLimits = new CacheLimits(
    WorldBytes: CacheGb("CacheMaxGb", "MCAHUB_CACHE_MAX_GB", 10),
    WorldsPerRepo: CacheInt("MaxWorldsPerRepo", "MCAHUB_MAX_WORLDS_PER_REPO", 10),
    MapBytes: CacheGb("MapCacheMaxGb", "MCAHUB_MAP_CACHE_MAX_GB", 2),
    MapsPerRepo: CacheInt("MaxMapsPerRepo", "MCAHUB_MAX_MAPS_PER_REPO", 100),
    ManifestEntries: CacheInt("MaxManifestEntries", "MCAHUB_MAX_MANIFEST_ENTRIES", 100_000));

var store = new RepoStore(dataDir);
var cache = new WorldCache(cacheDir, cacheLimits);
var maps = new MapCache(mapDir, cache, renderConcurrency, maxRenderChunks, cacheLimits);
var db = new HubDb(dbPath);
var audit = new AuditLog(auditPath);
Auth.Config auth = Auth.Read(builder.Configuration);

// Fail closed (#9): never serve open mode (anonymous read+write+create) without an explicit override, or
// dev-login (passwordless) at all, on a non-loopback interface — the red team's #1 way a public hub is owned.
string bindUrls = builder.Configuration["urls"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5080";
bool allowPublicOpen = (builder.Configuration["IKnowOpenModeIsPublic"] ?? Environment.GetEnvironmentVariable("MCAHUB_I_KNOW_OPEN_MODE_IS_PUBLIC")) is "1" or "true";
if (StartupGuard.PublicExposureViolation(auth.Accounts, auth.HasMaster ? "set" : null, auth.DevLogin, bindUrls, allowPublicOpen) is { } refusal)
    throw new InvalidOperationException($"mcadiff-hub refusing to start: {refusal}");

Auth.AddAuth(builder, auth, db);             // registers cookie + OAuth schemes (only in accounts mode)

WebApplication app = builder.Build();

// Behind a TLS-terminating reverse proxy, trust X-Forwarded-Proto/Host so the OAuth redirect_uri the
// handler builds matches the https callback registered with the provider. Must run before auth.
if ((app.Configuration["BehindProxy"] ?? Environment.GetEnvironmentVariable("MCAHUB_BEHIND_PROXY")) is "1" or "true")
{
    var fwd = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
    };
    // Trust X-Forwarded-* only from the configured proxy (default loopback) — never from any client (#7).
    // X-Forwarded-For also gives the per-IP rate limiter (#10) the real client IP behind the proxy.
    ForwardedProxies.Apply(fwd, app.Configuration["TrustedProxy"] ?? Environment.GetEnvironmentVariable("MCAHUB_TRUSTED_PROXY"));
    app.UseForwardedHeaders(fwd);
}

// Security response headers on everything (incl. static files). The strict CSP works because all client
// JS lives in /app.js and the time-machine data rides in a JSON data-island — no inline executable script.
app.Use(async (ctx, next) =>
{
    IHeaderDictionary h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "SAMEORIGIN";
    h["Referrer-Policy"] = "same-origin";
    h["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data: https:; style-src 'self'; script-src 'self'; frame-ancestors 'self'; base-uri 'self'; form-action 'self'";
    await next(ctx);
});
app.Use(async (ctx, next) =>                    // a save that can't reach disk → 507, not a 500 crash (#32)
{
    try { await next(ctx); }
    catch (HubDbSaveException) when (!ctx.Response.HasStarted)
    {
        ctx.Response.Clear();
        ctx.Response.StatusCode = StatusCodes.Status507InsufficientStorage;
        await ctx.Response.WriteAsync("the server is out of disk space — your change was not saved");
    }
});
app.UseHsts();                                 // Strict-Transport-Security (only emitted over HTTPS)
app.UseStaticFiles();                         // wwwroot/style.css, /app.js
app.UseRequestTimeouts();                      // enforces the per-endpoint render deadline (Pages.RenderTimeoutPolicy)
app.UseRateLimiter();                          // per-IP, per-surface rate limits (429 + Retry-After)
app.Use(async (ctx, next) =>                   // bad-token lockout: refuse a locked-out IP's bearer requests early
{
    if (ctx.Request.Headers.ContainsKey("Authorization")
        && authThrottle.IsLockedOut(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", out TimeSpan retry))
    {
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString();
        return;
    }
    await next(ctx);
});
if (auth.Accounts)
    app.UseAuthentication();                  // populates ctx.User from the session cookie (access checks are our own)

bool ageGate = (app.Configuration["MinAgeGate"] ?? Environment.GetEnvironmentVariable("MCAHUB_MIN_AGE_GATE")) is "1" or "true"; // COPPA age gate (#35); default off
if (auth.Accounts && ageGate) AgeGate.Map(app, db, audit); // bounce un-confirmed users to /auth/age-gate

Auth.MapAuth(app, auth, db);                  // /auth/login · /auth/callback · /auth/logout · /auth/dev
bool adoptUnowned = (app.Configuration["AdoptUnowned"] ?? Environment.GetEnvironmentVariable("MCAHUB_ADOPT_UNOWNED")) is "1" or "true"; // claim-on-first-push of pre-existing unowned repos (#6); default off
bool defaultPrivate = (app.Configuration["DefaultPrivate"] ?? Environment.GetEnvironmentVariable("MCAHUB_DEFAULT_PRIVATE")) is not ("0" or "false"); // new worlds private until published (#34); default on
int maxWorldsPerUser = int.TryParse(app.Configuration["MaxWorldsPerUser"] ?? Environment.GetEnvironmentVariable("MCAHUB_MAX_WORLDS_PER_USER"), out int mw) ? mw : 0; // per-user world cap (#35); 0 = unlimited
string? discordWebhook = app.Configuration["DiscordWebhook"] ?? Environment.GetEnvironmentVariable("MCAHUB_DISCORD_WEBHOOK"); // grief alerts on push (#25)
Transport.MapTransport(app, store, db, auth, maxPushBytes, authThrottle, adoptUnowned, audit, defaultPrivate, maxWorldsPerUser, discordWebhook); // mcadiff clone/fetch/push under /r/{repo}/…
string? reportEmail = app.Configuration["ReportEmail"] ?? Environment.GetEnvironmentVariable("MCAHUB_REPORT_EMAIL"); // abuse-report address (#35)
Pages.MapPages(app, store, cache, maps, db, auth, audit, reportEmail); // the web UI (browse + compare + world-state + map + account)

// Liveness probe for proxies/orchestrators — intentionally unauthenticated + rate-limit exempt (#32).
// Document that it must not be blocked at the proxy.
app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow })).DisableRateLimiting();

string mode = auth.Accounts ? (auth.Oauth ? $"accounts ({string.Join("/", auth.Providers.Select(p => p.Name))} OAuth)" : "accounts (dev login)")
    : !auth.HasMaster ? "open push" : "token-gated push";
app.Logger.LogInformation("mcadiff-hub serving worlds from {DataDir} · auth: {Mode}", Path.GetFullPath(dataDir), mode);

// In accounts mode, warn about on-disk worlds with no owner record — they're the claim-on-first-push
// takeover surface during an "enable accounts" migration (#6).
if (auth.Accounts)
{
    List<string> unowned = store.List().Where(r => db.GetRepo(r.Name) is null).Select(r => r.Name).ToList();
    if (unowned.Count > 0)
        app.Logger.LogWarning("{Count} world(s) have no owner and are {State}: {Repos}",
            unowned.Count,
            adoptUnowned ? "self-adoptable on first push (MCAHUB_ADOPT_UNOWNED=1)" : "read-only to non-admins until an admin adopts them",
            string.Join(", ", unowned));
}

app.Run();

// Parse a positive byte count from config/env; null (use the default) for missing or invalid input.
static long? ParsePositiveLong(string? s) => long.TryParse(s, out long v) && v > 0 ? v : null;

// Classify a request into a rate-limit surface (auth / write transport / cold render / read).
static (string Category, int Limit) RateCategory(HttpRequest r, int auth, int write, int render, int read)
{
    string p = r.Path.Value ?? "";
    if (p.StartsWith("/auth", StringComparison.Ordinal)
        || (HttpMethods.IsPost(r.Method) && p.Equals("/account/tokens", StringComparison.Ordinal)))
        return ("auth", auth);
    if (HttpMethods.IsGet(r.Method) && p.StartsWith("/r/", StringComparison.Ordinal)
        && p.Contains("/map/", StringComparison.Ordinal) && p.EndsWith(".png", StringComparison.Ordinal))
        return ("render", render);
    if (HttpMethods.IsPost(r.Method) && p.StartsWith("/r/", StringComparison.Ordinal)
        && (p.EndsWith("/pack", StringComparison.Ordinal) || p.Contains("/objects/", StringComparison.Ordinal)
            || p.Contains("/refs/heads/", StringComparison.Ordinal) || p.EndsWith("/have", StringComparison.Ordinal)))
        return ("write", write);
    return ("read", read);
}

// A minimal dotenv reader: KEY=VALUE lines, '#' comments, optional quotes. Never overrides a real
// environment variable, so the shell still wins over the file.
static void LoadDotEnv(string path)
{
    if (!File.Exists(path)) return;
    foreach (string raw in File.ReadAllLines(path))
    {
        string line = raw.Trim();
        if (line.Length == 0 || line[0] == '#') continue;
        int eq = line.IndexOf('=');
        if (eq <= 0) continue;
        string key = line[..eq].Trim();
        string val = line[(eq + 1)..].Trim();
        if (val.Length >= 2 && ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
            val = val[1..^1];
        if (Environment.GetEnvironmentVariable(key) is null)
            Environment.SetEnvironmentVariable(key, val);
    }
}

// Exposed so WebApplicationFactory<Program> can boot the app in integration tests.
public partial class Program;
