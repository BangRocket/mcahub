using System.Text.Json;

namespace McaHub;

/// <summary>
/// Serves the mcagit network protocol for every hosted repo under <c>/r/{repo}/…</c> by
/// reverse-proxying to a co-located <c>mcagit serve</c> sidecar (the Rust transport engine), with
/// mcahub's auth / accounts / throttle gate in front. Reads honor per-repo visibility; writes require
/// the owner (accounts mode) or the master token (open/token mode). A first push to a new name
/// auto-creates the world (the sidecar does it), so the transport no longer touches RepoStore.Create.
/// </summary>
public static class Transport
{
    private const string SystemOwner = "__system__"; // owner stamped on master-token pushes

    public static void MapTransport(WebApplication app, RepoStore store, HubDb db, Auth.Config cfg, long maxBody,
        AuthThrottle throttle, bool adoptUnowned, AuditLog audit, bool defaultPrivate, int maxWorldsPerUser,
        HttpClient sidecar, string sidecarBase, string? discordWebhook = null)
    {
        // Reads (advertise refs, negotiate, download an object).
        app.MapGet("/r/{repo}/info/refs", (string repo, HttpContext ctx) =>
            Read(ctx, repo, "info/refs", db, cfg, throttle, maxBody, sidecar, sidecarBase));
        app.MapPost("/r/{repo}/have", (string repo, HttpContext ctx) =>
            Read(ctx, repo, "have", db, cfg, throttle, maxBody, sidecar, sidecarBase));
        app.MapGet("/r/{repo}/objects/{hash}", (string repo, string hash, HttpContext ctx) =>
            Read(ctx, repo, $"objects/{hash}", db, cfg, throttle, maxBody, sidecar, sidecarBase));

        // Writes (upload an object / pack, advance a branch).
        app.MapPost("/r/{repo}/objects/{hash}", (string repo, string hash, HttpContext ctx) =>
            Write(ctx, repo, $"objects/{hash}", store, db, cfg, maxBody, throttle, adoptUnowned, audit, defaultPrivate, maxWorldsPerUser, sidecar, sidecarBase));
        app.MapPost("/r/{repo}/pack", (string repo, HttpContext ctx) =>
            Write(ctx, repo, "pack", store, db, cfg, maxBody, throttle, adoptUnowned, audit, defaultPrivate, maxWorldsPerUser, sidecar, sidecarBase));
        app.MapPost("/r/{repo}/refs/heads/{branch}", (string repo, string branch, HttpContext ctx) =>
            Write(ctx, repo, $"refs/heads/{branch}", store, db, cfg, maxBody, throttle, adoptUnowned, audit, defaultPrivate, maxWorldsPerUser, sidecar, sidecarBase));
    }

    private static async Task<IResult> Read(HttpContext ctx, string repo, string action, HubDb db, Auth.Config cfg,
        AuthThrottle throttle, long maxBody, HttpClient sidecar, string sidecarBase)
    {
        if (!RepoStore.IsValidName(repo)) return Results.NotFound();
        if (!Readable(repo, ctx.Request, db, cfg, throttle)) return Results.NotFound(); // auth before touching the body
        byte[]? body = HttpMethods.IsPost(ctx.Request.Method) ? await Bytes(ctx.Request, maxBody) : null;
        return await Proxy(ctx, sidecar, sidecarBase, repo, action, body);
    }

    private static async Task<IResult> Write(HttpContext ctx, string repo, string action, RepoStore store, HubDb db,
        Auth.Config cfg, long maxBody, AuthThrottle throttle, bool adoptUnowned, AuditLog audit, bool defaultPrivate,
        int maxWorldsPerUser, HttpClient sidecar, string sidecarBase)
    {
        if (!RepoStore.IsValidName(repo)) return Results.NotFound();
        (string? uid, bool admin, string? scope) = Auth.Identify(ctx.Request, cfg, db, out bool badToken);
        RecordToken(ctx.Request, cfg, throttle, badToken);

        if (cfg.Accounts)
        {
            if (!admin && uid is null)
                return Results.Text("authenticate with a personal access token: mcagit push … --token <PAT>", statusCode: 401);
            if (!admin && scope != "write") // a read-scoped PAT can clone/fetch but not push (#18)
                return Results.Text("this token is read-only — mint a write-scoped token to push", statusCode: 403);
            if (!admin && db.GetRepo(repo) is not null) // an owned repo: enforce the write role
            {
                if (!Auth.CanWrite(cfg, db, repo, uid, admin))
                    // Hide a private repo's existence: a non-reader gets 404 (same as a missing name), never 403.
                    return Auth.CanRead(cfg, db, repo, uid, admin)
                        ? Results.Text("this world belongs to another account", statusCode: 403)
                        : Results.NotFound();
            }
            else if (!admin && store.Exists(repo) && !adoptUnowned)
                // Unowned but already on disk → predates accounts; a non-admin can't free-claim it (#6).
                return Results.Text("this world has no owner (it predates accounts); an admin must adopt it — set MCAHUB_ADOPT_UNOWNED=1 to allow self-adoption", statusCode: 403);
        }
        else if (cfg.HasMaster && !admin)
            return Results.Text("invalid or missing push token", statusCode: 401);

        string actor = uid ?? (admin ? SystemOwner : "anon");
        bool created = !store.Exists(repo);
        if (created && cfg.Accounts && !admin && uid is not null && maxWorldsPerUser > 0 && db.OwnedRepoCount(uid) >= maxWorldsPerUser)
            return Results.Text($"you've reached the limit of {maxWorldsPerUser} worlds — delete one before creating another", statusCode: 403);
        // The sidecar auto-creates the repo on first push; mcahub only records ownership.
        if (cfg.Accounts && uid is not null) db.EnsureRepo(repo, uid, isPrivate: defaultPrivate);
        else if (cfg.Accounts && admin) db.EnsureRepo(repo, SystemOwner, isPrivate: false);
        if (created && cfg.Accounts) audit.Append(actor, "ownership.claim", repo, "first push", "cli", Ip(ctx));

        byte[] body;
        try { body = await Bytes(ctx.Request, maxBody); }
        catch (BadHttpRequestException e) { return Results.Text(e.Message, statusCode: e.StatusCode); } // 413

        IResult result = await Proxy(ctx, sidecar, sidecarBase, repo, action, body);

        // Audit a branch advance (the grief Discord alert is reinstated once grief runs on the Rust core).
        if (action.StartsWith("refs/heads/", StringComparison.Ordinal) && ctx.Response.StatusCode < 300)
        {
            string branch = action["refs/heads/".Length..];
            (string? oldH, string? newH) = RefUpdate(body);
            audit.Append(actor, "ref.update", repo, $"{branch} {Sh(oldH)}→{Sh(newH)}", "cli", Ip(ctx));
        }
        return result;
    }

    private static (string? Old, string? New) RefUpdate(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            string? Get(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Get("old"), Get("new"));
        }
        catch { return (null, null); }
    }

    /// <summary>Forward the current request (with the already-read <paramref name="body"/>) to the
    /// sidecar and relay its status + body.</summary>
    private static async Task<IResult> Proxy(HttpContext ctx, HttpClient sidecar, string sidecarBase, string repo, string action, byte[]? body)
    {
        string url = $"{sidecarBase}/r/{repo}/{action}";
        using var msg = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), url);
        if (ctx.Request.Headers.Authorization.Count > 0)
            msg.Headers.TryAddWithoutValidation("Authorization", ctx.Request.Headers.Authorization.ToString());
        if (body is not null) msg.Content = new ByteArrayContent(body);
        using var resp = await sidecar.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        ctx.Response.StatusCode = (int)resp.StatusCode;
        ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        return Results.Empty;
    }

    private static string Sh(string? hash) => hash is { Length: > 0 } ? hash[..Math.Min(10, hash.Length)] : "∅";
    private static string? Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    private static bool Readable(string repo, HttpRequest req, HubDb db, Auth.Config cfg, AuthThrottle throttle)
    {
        (string? uid, bool admin, _) = Auth.Identify(req, cfg, db, out bool badToken); // any valid token may read
        RecordToken(req, cfg, throttle, badToken);
        return Auth.CanRead(cfg, db, repo, uid, admin);
    }

    /// <summary>Feed the bad-token signal to the lockout throttle — but only when there's actually a
    /// token to guess (accounts or master-token mode), so an open-mode client can't lock itself out.</summary>
    private static void RecordToken(HttpRequest req, Auth.Config cfg, AuthThrottle throttle, bool badToken)
    {
        if (cfg.Accounts || cfg.HasMaster)
            throttle.OnResult(req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", badToken);
    }

    /// <summary>Buffer a body, refusing anything past <paramref name="maxBody"/> bytes as it streams
    /// (chunked bodies carry no Content-Length). Throws a 413 (caught in <see cref="Write"/>).</summary>
    private static async Task<byte[]> Bytes(HttpRequest req, long maxBody)
    {
        using var ms = new MemoryStream();
        byte[] buf = new byte[81920];
        long total = 0;
        int r;
        while ((r = await req.Body.ReadAsync(buf)) > 0)
        {
            total += r;
            if (total > maxBody)
                throw new BadHttpRequestException("request body too large", StatusCodes.Status413PayloadTooLarge);
            ms.Write(buf, 0, r);
        }
        return ms.ToArray();
    }
}
