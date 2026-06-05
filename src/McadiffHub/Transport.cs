using McaDiff.Repo;
using HttpProtocol = McaDiff.Repo.HttpProtocol; // disambiguate from Microsoft.AspNetCore.Http.HttpProtocol

namespace McadiffHub;

/// <summary>
/// Serves the mcadiff network protocol for every hosted repo under <c>/r/{repo}/…</c>, so
/// <c>mcadiff clone/fetch/push http://hub/r/&lt;name&gt;</c> works. Each request opens a per-repo
/// <see cref="RemoteService"/> (the same handler the standalone <c>mcadiff serve</c> uses). Identity
/// comes from a Bearer personal-access-token (see <see cref="Auth"/>): in accounts mode reads honor
/// per-repo visibility and writes require the owner; in open/token mode the legacy master-token gate
/// applies.
/// </summary>
public static class Transport
{
    private const string SystemOwner = "__system__"; // owner stamped on master-token pushes so they aren't orphan-claimable

    public static void MapTransport(WebApplication app, RepoStore store, HubDb db, Auth.Config cfg, long maxBody, AuthThrottle throttle, bool adoptUnowned, AuditLog audit, bool defaultPrivate)
    {
        // Advertise refs. A valid-but-not-yet-created name advertises an empty remote, so a first
        // `push` to it succeeds and auto-creates the world (hub convenience).
        app.MapGet("/r/{repo}/info/refs", (string repo, HttpRequest req) =>
        {
            if (!Readable(repo, req, db, cfg, throttle)) return Results.NotFound();
            if (Svc(store, repo, write: false) is { } s) return Results.Json(s.ListRefs(), HttpProtocol.Json);
            return RepoStore.IsValidName(repo)
                ? Results.Json(new RefAdvertisement([], [], null), HttpProtocol.Json)
                : Results.NotFound();
        });

        // Negotiate: which of these object hashes is the remote missing?
        app.MapPost("/r/{repo}/have", async (string repo, HttpRequest req) =>
        {
            if (!Readable(repo, req, db, cfg, throttle)) return Results.NotFound(); // auth before touching the body (#3)
            var want = await req.ReadFromJsonAsync<List<string>>(HttpProtocol.Json) ?? [];
            if (Svc(store, repo, write: false) is { } s) return Results.Json(s.Missing(want), HttpProtocol.Json);
            return RepoStore.IsValidName(repo) ? Results.Json(want, HttpProtocol.Json) : Results.NotFound(); // empty remote → all missing
        });

        // Download one object (compressed).
        app.MapGet("/r/{repo}/objects/{hash}", (string repo, string hash, HttpRequest req) =>
        {
            if (!Readable(repo, req, db, cfg, throttle)) return Results.NotFound();
            if (Svc(store, repo, write: false) is not { } s) return Results.NotFound();
            try { return Results.Bytes(s.GetObject(hash), "application/octet-stream"); }
            catch (Exception e) when (e is IOException or InvalidDataException) { return Results.NotFound(); }
        });

        // Upload one object (single-object fallback).
        app.MapPost("/r/{repo}/objects/{hash}", async (string repo, string hash, HttpRequest req, HttpContext ctx) =>
            await Write(store, db, cfg, repo, ctx, throttle, adoptUnowned, audit, defaultPrivate, async (s, _) => s.PutObject(hash, await Bytes(req, maxBody))));

        // Upload a whole pack (the common push path).
        app.MapPost("/r/{repo}/pack", async (string repo, HttpRequest req, HttpContext ctx) =>
            await Write(store, db, cfg, repo, ctx, throttle, adoptUnowned, audit, defaultPrivate, async (s, _) =>
            {
                (byte[] pack, byte[] idx) = PackTransfer.UnframeBody(await Bytes(req, maxBody));
                s.PutPack(pack, idx);
            }));

        // Advance a branch (compare-and-swap, fast-forward guarded server-side).
        app.MapPost("/r/{repo}/refs/heads/{branch}", async (string repo, string branch, HttpRequest req, HttpContext ctx) =>
            await Write(store, db, cfg, repo, ctx, throttle, adoptUnowned, audit, defaultPrivate, async (s, actor) =>
            {
                RefUpdate u = await req.ReadFromJsonAsync<RefUpdate>(HttpProtocol.Json) ?? new RefUpdate();
                s.UpdateRef(branch, u.Old, u.New, u.Force);
                audit.Append(actor, "ref.update", repo, $"{branch} {Sh(u.Old)}→{Sh(u.New)}", "cli", Ip(ctx));
            }));
    }

    private static string Sh(string? hash) => hash is { Length: > 0 } ? hash[..Math.Min(10, hash.Length)] : "∅";
    private static string? Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    private static RemoteService? Svc(RepoStore store, string repo, bool write) =>
        store.Exists(repo) ? new RemoteService(store.Open(repo), allowWrite: write) : null;

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

    private static async Task<IResult> Write(RepoStore store, HubDb db, Auth.Config cfg, string repo, HttpContext ctx,
        AuthThrottle throttle, bool adoptUnowned, AuditLog audit, bool defaultPrivate, Func<RemoteService, string, Task> body)
    {
        if (!RepoStore.IsValidName(repo)) return Results.NotFound();
        (string? uid, bool admin, string? scope) = Auth.Identify(ctx.Request, cfg, db, out bool badToken);
        RecordToken(ctx.Request, cfg, throttle, badToken);

        if (cfg.Accounts)
        {
            if (!admin && uid is null)
                return Results.Text("authenticate with a personal access token: mcadiff push … --token <PAT>", statusCode: 401);
            if (!admin && scope != "write") // a read-scoped PAT can clone/fetch but not push (#18)
                return Results.Text("this token is read-only — mint a write-scoped token to push", statusCode: 403);
            if (!admin && db.GetRepo(repo) is not null) // an owned repo: enforce the write role
            {
                if (!Auth.CanWrite(cfg, db, repo, uid, admin))
                    // Hide a private repo's existence: if the writer can't even read it, answer 404 (same as a
                    // non-existent name), never 403. A 403 only shows for repos they can already see (public or
                    // collaborator), where existence isn't a secret. (#1 — private-repo existence oracle.)
                    return Auth.CanRead(cfg, db, repo, uid, admin)
                        ? Results.Text("this world belongs to another account", statusCode: 403)
                        : Results.NotFound();
            }
            else if (!admin && store.Exists(repo) && !adoptUnowned)
                // Unowned but already on disk → it predates accounts; a non-admin can't free-claim it
                // (ownership-takeover guard). Only genuinely new names auto-create. (#6)
                return Results.Text("this world has no owner (it predates accounts); an admin must adopt it — set MCAHUB_ADOPT_UNOWNED=1 to allow self-adoption", statusCode: 403);
        }
        else if (cfg.HasMaster && !admin)
            return Results.Text("invalid or missing push token", statusCode: 401);

        string actor = uid ?? (admin ? SystemOwner : "anon");
        bool created = !store.Exists(repo);
        if (created) store.Create(repo);                      // first push auto-creates the world
        if (cfg.Accounts && uid is not null) db.EnsureRepo(repo, uid, isPrivate: defaultPrivate); // claim on first push; new worlds default private (#34)
        else if (cfg.Accounts && admin) db.EnsureRepo(repo, SystemOwner, isPrivate: false);       // master-token push: owned, never orphan-claimable (#6); ops, stays public
        if (created && cfg.Accounts) audit.Append(actor, "ownership.claim", repo, "first push", "cli", Ip(ctx));

        try { await body(new RemoteService(store.Open(repo), allowWrite: true), actor); return Results.Ok(); }
        catch (BadHttpRequestException e) { return Results.Text(e.Message, statusCode: e.StatusCode); } // body too large → 413
        catch (UnauthorizedAccessException) { return Results.NotFound(); }                              // e.g. path-confinement; no detail, no oracle
        catch (InvalidOperationException e) { return Results.Text(e.Message, statusCode: 400); }        // FF/stale-push etc. — safe operational text
        catch (InvalidDataException e) { return Results.Text(e.Message, statusCode: 400); }             // bad object/pack
        catch (Exception e)                                                                            // anything else may carry internal paths → don't echo it
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("McadiffHub.Transport")
                .LogError(e, "transport write failed for {Repo}", repo);
            return Results.Text("internal error", statusCode: 500);
        }
    }

    /// <summary>Buffer a push body, refusing anything past <paramref name="maxBody"/> bytes <em>before</em>
    /// it is fully read — chunked bodies carry no Content-Length, so the cap is enforced as we stream.
    /// Throws a 413 <see cref="BadHttpRequestException"/> (caught in <see cref="Write"/>).</summary>
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
