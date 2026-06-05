using McadiffHub;
using Microsoft.AspNetCore.HttpOverrides;

LoadDotEnv(Path.Combine(Directory.GetCurrentDirectory(), ".env")); // pull MCAHUB_* / OAuth creds out of a local .env

var builder = WebApplication.CreateBuilder(args);

// Default to a friendly local port unless the host overrides ASPNETCORE_URLS.
if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null && !builder.Configuration.GetSection("urls").Exists())
    builder.WebHost.UseUrls("http://localhost:5080");

string dataDir = builder.Configuration["DataDir"] ?? Environment.GetEnvironmentVariable("MCAHUB_DATA") ?? "data/repos";
string sibling = Path.GetDirectoryName(Path.GetFullPath(dataDir))!;
string cacheDir = builder.Configuration["CacheDir"] ?? Environment.GetEnvironmentVariable("MCAHUB_CACHE") ?? Path.Combine(sibling, "cache");
string dbPath = builder.Configuration["DbPath"] ?? Environment.GetEnvironmentVariable("MCAHUB_DB") ?? Path.Combine(sibling, "hub.json");

var store = new RepoStore(dataDir);
var cache = new WorldCache(cacheDir);
var db = new HubDb(dbPath);
Auth.Config auth = Auth.Read(builder.Configuration);

Auth.AddAuth(builder, auth, db);             // registers cookie + OAuth schemes (only in accounts mode)

WebApplication app = builder.Build();

// Behind a TLS-terminating reverse proxy, trust X-Forwarded-Proto/Host so the OAuth redirect_uri the
// handler builds matches the https callback registered with the provider. Must run before auth.
if (Environment.GetEnvironmentVariable("MCAHUB_BEHIND_PROXY") is "1" or "true")
{
    var fwd = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost };
    fwd.KnownNetworks.Clear();               // the hub is only reachable via the proxy, so trust its headers
    fwd.KnownProxies.Clear();
    app.UseForwardedHeaders(fwd);
}

app.UseStaticFiles();                         // wwwroot/style.css
if (auth.Accounts)
    app.UseAuthentication();                  // populates ctx.User from the session cookie (access checks are our own)

Auth.MapAuth(app, auth, db);                  // /auth/login · /auth/callback · /auth/logout · /auth/dev
Transport.MapTransport(app, store, db, auth); // mcadiff clone/fetch/push under /r/{repo}/…
Pages.MapPages(app, store, cache, db, auth);  // the web UI (browse + compare + world-state + account)

string mode = auth.Accounts ? (auth.Oauth ? $"accounts ({auth.Provider} OAuth)" : "accounts (dev login)")
    : auth.MasterToken is null ? "open push" : "token-gated push";
app.Logger.LogInformation("mcadiff-hub serving worlds from {DataDir} · auth: {Mode}", Path.GetFullPath(dataDir), mode);
app.Run();

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
