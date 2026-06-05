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
    public static void MapTransport(WebApplication app, RepoStore store, HubDb db, Auth.Config cfg, long maxBody)
    {
        // Advertise refs. A valid-but-not-yet-created name advertises an empty remote, so a first
        // `push` to it succeeds and auto-creates the world (hub convenience).
        app.MapGet("/r/{repo}/info/refs", (string repo, HttpRequest req) =>
        {
            if (!Readable(repo, req, db, cfg)) return Results.NotFound();
            if (Svc(store, repo, write: false) is { } s) return Results.Json(s.ListRefs(), HttpProtocol.Json);
            return RepoStore.IsValidName(repo)
                ? Results.Json(new RefAdvertisement([], [], null), HttpProtocol.Json)
                : Results.NotFound();
        });

        // Negotiate: which of these object hashes is the remote missing?
        app.MapPost("/r/{repo}/have", async (string repo, HttpRequest req) =>
        {
            if (!Readable(repo, req, db, cfg)) return Results.NotFound(); // auth before touching the body (#3)
            var want = await req.ReadFromJsonAsync<List<string>>(HttpProtocol.Json) ?? [];
            if (Svc(store, repo, write: false) is { } s) return Results.Json(s.Missing(want), HttpProtocol.Json);
            return RepoStore.IsValidName(repo) ? Results.Json(want, HttpProtocol.Json) : Results.NotFound(); // empty remote → all missing
        });

        // Download one object (compressed).
        app.MapGet("/r/{repo}/objects/{hash}", (string repo, string hash, HttpRequest req) =>
        {
            if (!Readable(repo, req, db, cfg)) return Results.NotFound();
            if (Svc(store, repo, write: false) is not { } s) return Results.NotFound();
            try { return Results.Bytes(s.GetObject(hash), "application/octet-stream"); }
            catch (Exception e) when (e is IOException or InvalidDataException) { return Results.NotFound(); }
        });

        // Upload one object (single-object fallback).
        app.MapPost("/r/{repo}/objects/{hash}", async (string repo, string hash, HttpRequest req, HttpContext ctx) =>
            await Write(store, db, cfg, repo, ctx, async s => s.PutObject(hash, await Bytes(req, maxBody))));

        // Upload a whole pack (the common push path).
        app.MapPost("/r/{repo}/pack", async (string repo, HttpRequest req, HttpContext ctx) =>
            await Write(store, db, cfg, repo, ctx, async s =>
            {
                (byte[] pack, byte[] idx) = PackTransfer.UnframeBody(await Bytes(req, maxBody));
                s.PutPack(pack, idx);
            }));

        // Advance a branch (compare-and-swap, fast-forward guarded server-side).
        app.MapPost("/r/{repo}/refs/heads/{branch}", async (string repo, string branch, HttpRequest req, HttpContext ctx) =>
            await Write(store, db, cfg, repo, ctx, async s =>
            {
                RefUpdate u = await req.ReadFromJsonAsync<RefUpdate>(HttpProtocol.Json) ?? new RefUpdate();
                s.UpdateRef(branch, u.Old, u.New, u.Force);
            }));
    }

    private static RemoteService? Svc(RepoStore store, string repo, bool write) =>
        store.Exists(repo) ? new RemoteService(store.Open(repo), allowWrite: write) : null;

    private static bool Readable(string repo, HttpRequest req, HubDb db, Auth.Config cfg)
    {
        (string? uid, bool admin) = Auth.Identify(req, cfg, db, out _);
        return Auth.CanRead(cfg, db, repo, uid, admin);
    }

    private static async Task<IResult> Write(RepoStore store, HubDb db, Auth.Config cfg, string repo, HttpContext ctx,
        Func<RemoteService, Task> body)
    {
        if (!RepoStore.IsValidName(repo)) return Results.NotFound();
        (string? uid, bool admin) = Auth.Identify(ctx.Request, cfg, db, out _);

        if (cfg.Accounts)
        {
            if (!admin && uid is null)
                return Results.Text("authenticate with a personal access token: mcadiff push … --token <PAT>", statusCode: 401);
            if (!Auth.CanWrite(cfg, db, repo, uid, admin))
                // Hide a private repo's existence: if the writer can't even read it, answer 404 (same as a
                // non-existent name), never 403. A 403 only shows for repos they can already see (public or
                // collaborator), where existence isn't a secret. (#1 — private-repo existence oracle.)
                return Auth.CanRead(cfg, db, repo, uid, admin)
                    ? Results.Text("this world belongs to another account", statusCode: 403)
                    : Results.NotFound();
        }
        else if (cfg.MasterToken is not null && !admin)
            return Results.Text("invalid or missing push token", statusCode: 401);

        if (!store.Exists(repo)) store.Create(repo);          // first push auto-creates the world
        if (cfg.Accounts && uid is not null) db.EnsureRepo(repo, uid, isPrivate: false); // claim ownership on first push

        try { await body(new RemoteService(store.Open(repo), allowWrite: true)); return Results.Ok(); }
        catch (BadHttpRequestException e) { return Results.Text(e.Message, statusCode: e.StatusCode); } // body too large → 413
        catch (UnauthorizedAccessException e) { return Results.Text(e.Message, statusCode: 403); }
        catch (Exception e) { return Results.Text(e.Message, statusCode: 400); }
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
