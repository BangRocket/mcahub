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

var store = new RepoStore(dataDir);
var cache = new WorldCache(cacheDir);
var maps = new MapCache(mapDir, cache, renderConcurrency, maxRenderChunks);
var db = new HubDb(dbPath);
Auth.Config auth = Auth.Read(builder.Configuration);

Auth.AddAuth(builder, auth, db);             // registers cookie + OAuth schemes (only in accounts mode)

WebApplication app = builder.Build();

// Behind a TLS-terminating reverse proxy, trust X-Forwarded-Proto/Host so the OAuth redirect_uri the
// handler builds matches the https callback registered with the provider. Must run before auth.
if ((app.Configuration["BehindProxy"] ?? Environment.GetEnvironmentVariable("MCAHUB_BEHIND_PROXY")) is "1" or "true")
{
    var fwd = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost };
    fwd.KnownIPNetworks.Clear();             // the hub is only reachable via the proxy, so trust its headers
    fwd.KnownProxies.Clear();
    app.UseForwardedHeaders(fwd);
}

app.UseStaticFiles();                         // wwwroot/style.css
app.UseRequestTimeouts();                      // enforces the per-endpoint render deadline (Pages.RenderTimeoutPolicy)
if (auth.Accounts)
    app.UseAuthentication();                  // populates ctx.User from the session cookie (access checks are our own)

Auth.MapAuth(app, auth, db);                  // /auth/login · /auth/callback · /auth/logout · /auth/dev
Transport.MapTransport(app, store, db, auth, maxPushBytes); // mcadiff clone/fetch/push under /r/{repo}/…
Pages.MapPages(app, store, cache, maps, db, auth); // the web UI (browse + compare + world-state + map + account)

string mode = auth.Accounts ? (auth.Oauth ? $"accounts ({auth.Provider} OAuth)" : "accounts (dev login)")
    : auth.MasterToken is null ? "open push" : "token-gated push";
app.Logger.LogInformation("mcadiff-hub serving worlds from {DataDir} · auth: {Mode}", Path.GetFullPath(dataDir), mode);
app.Run();

// Parse a positive byte count from config/env; null (use the default) for missing or invalid input.
static long? ParsePositiveLong(string? s) => long.TryParse(s, out long v) && v > 0 ? v : null;

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
