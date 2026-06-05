using McadiffHub;

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
