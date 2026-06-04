using System.Security.Cryptography;
using System.Text;
using McaDiff.Repo;
using HttpProtocol = McaDiff.Repo.HttpProtocol; // disambiguate from Microsoft.AspNetCore.Http.HttpProtocol

namespace McadiffHub;

/// <summary>
/// Serves the mcadiff network protocol for every hosted repo under <c>/r/{repo}/…</c>, so
/// <c>mcadiff clone/fetch/push http://hub/r/&lt;name&gt;</c> works. Each request opens a per-repo
/// <see cref="RemoteService"/> (the same handler the standalone <c>mcadiff serve</c> uses). Reads are
/// anonymous; writes need the push token when one is configured.
/// </summary>
public static class Transport
{
    public static void MapTransport(WebApplication app, RepoStore store, string? pushToken)
    {
        // Advertise refs. A valid-but-not-yet-created name advertises an empty remote, so a first
        // `push` to it succeeds and auto-creates the world (hub convenience).
        app.MapGet("/r/{repo}/info/refs", (string repo) =>
        {
            if (Svc(store, repo, write: false) is { } s) return Results.Json(s.ListRefs(), HttpProtocol.Json);
            return RepoStore.IsValidName(repo)
                ? Results.Json(new RefAdvertisement([], [], null), HttpProtocol.Json)
                : Results.NotFound();
        });

        // Negotiate: which of these object hashes is the remote missing?
        app.MapPost("/r/{repo}/have", async (string repo, HttpRequest req) =>
        {
            var want = await req.ReadFromJsonAsync<List<string>>(HttpProtocol.Json) ?? [];
            if (Svc(store, repo, write: false) is { } s) return Results.Json(s.Missing(want), HttpProtocol.Json);
            return RepoStore.IsValidName(repo) ? Results.Json(want, HttpProtocol.Json) : Results.NotFound(); // empty remote → all missing
        });

        // Download one object (compressed).
        app.MapGet("/r/{repo}/objects/{hash}", (string repo, string hash) =>
        {
            if (Svc(store, repo, write: false) is not { } s) return Results.NotFound();
            try { return Results.Bytes(s.GetObject(hash), "application/octet-stream"); }
            catch (Exception e) when (e is IOException or InvalidDataException) { return Results.NotFound(); }
        });

        // Upload one object (single-object fallback).
        app.MapPost("/r/{repo}/objects/{hash}", async (string repo, string hash, HttpRequest req, HttpContext ctx) =>
            await Write(store, repo, pushToken, ctx, async s => s.PutObject(hash, await Bytes(req))));

        // Upload a whole pack (the common push path).
        app.MapPost("/r/{repo}/pack", async (string repo, HttpRequest req, HttpContext ctx) =>
            await Write(store, repo, pushToken, ctx, async s =>
            {
                (byte[] pack, byte[] idx) = PackTransfer.UnframeBody(await Bytes(req));
                s.PutPack(pack, idx);
            }));

        // Advance a branch (compare-and-swap, fast-forward guarded server-side).
        app.MapPost("/r/{repo}/refs/heads/{branch}", async (string repo, string branch, HttpRequest req, HttpContext ctx) =>
            await Write(store, repo, pushToken, ctx, async s =>
            {
                RefUpdate u = await req.ReadFromJsonAsync<RefUpdate>(HttpProtocol.Json) ?? new RefUpdate();
                s.UpdateRef(branch, u.Old, u.New, u.Force);
            }));
    }

    private static RemoteService? Svc(RepoStore store, string repo, bool write) =>
        store.Exists(repo) ? new RemoteService(store.Open(repo), allowWrite: write) : null;

    private static async Task<IResult> Write(RepoStore store, string repo, string? token, HttpContext ctx,
        Func<RemoteService, Task> body)
    {
        if (!RepoStore.IsValidName(repo)) return Results.NotFound();
        if (!Authorized(ctx, token)) return Results.Text("invalid or missing push token", statusCode: 401);
        if (!store.Exists(repo)) store.Create(repo); // first push auto-creates the world
        try { await body(new RemoteService(store.Open(repo), allowWrite: true)); return Results.Ok(); }
        catch (UnauthorizedAccessException e) { return Results.Text(e.Message, statusCode: 403); }
        catch (Exception e) { return Results.Text(e.Message, statusCode: 400); }
    }

    private static bool Authorized(HttpContext ctx, string? token)
    {
        if (token is null) return true; // open hub
        string? header = ctx.Request.Headers.Authorization;
        if (header is null) return false;
        string sent = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : header.Trim();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(token));
    }

    private static async Task<byte[]> Bytes(HttpRequest req)
    {
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms);
        return ms.ToArray();
    }
}
