using McadiffHub;

var builder = WebApplication.CreateBuilder(args);

// Default to a friendly local port unless the host overrides ASPNETCORE_URLS.
if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null && !builder.Configuration.GetSection("urls").Exists())
    builder.WebHost.UseUrls("http://localhost:5080");

WebApplication app = builder.Build();

string dataDir = builder.Configuration["DataDir"] ?? Environment.GetEnvironmentVariable("MCAHUB_DATA") ?? "data/repos";
string? pushToken = builder.Configuration["PushToken"] ?? Environment.GetEnvironmentVariable("MCAHUB_TOKEN");
var store = new RepoStore(dataDir);

app.UseStaticFiles();                       // wwwroot/style.css
Transport.MapTransport(app, store, pushToken); // mcadiff clone/fetch/push under /r/{repo}/…
Pages.MapPages(app, store);                 // the web UI

app.Logger.LogInformation("mcadiff-hub serving worlds from {DataDir} ({Auth})",
    Path.GetFullPath(dataDir), pushToken is null ? "open push" : "token-gated push");
app.Run();
