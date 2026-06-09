using System.Text;
using System.Text.Json;
using McaDiff.Diff;
using McaDiff.Query;
using McaDiff.Repo;
using static McaHub.Html;

namespace McaHub;

/// <summary>Server-rendered pages: the repo list, a repo's backup timeline, a backup's view (semantic
/// diff + grief summary), compare-any-two-backups, a world explorer, and the account page (personal
/// access tokens + per-repo visibility). Private worlds are hidden from anyone but their owner.</summary>
public static class Pages
{
    /// <summary>Request-timeout policy name applied to the cold-render map endpoint (registered in Program).</summary>
    public const string RenderTimeoutPolicy = "render";

    public static void MapPages(WebApplication app, RepoStore store, WorldCache cache, MapCache maps, RenderQueue renderQueue, HubDb db, Auth.Config cfg, AuditLog audit, McaHub.Rust.RustEngine rust, string? reportEmail = null)
    {
        app.MapGet("/", (HttpContext ctx) => Home(ctx, store, db, cfg));
        app.MapGet("/aup", (HttpContext ctx) => Aup(ctx, cfg, reportEmail));
        app.MapGet("/r/{repo}/embed", (string repo, HttpContext ctx) => Embed(ctx, store, db, cfg, repo)); // iframe-able map (#25)
        app.MapGet("/upload", (HttpContext ctx) => UploadPage(ctx, cfg));     // drag-drop a world, no CLI (#26)
        // Block body (not `=> await …`): an expression-bodied async lambda binds as a raw RequestDelegate
        // and its IResult is discarded. A block body binds as the rich handler that executes the result.
        app.MapPost("/upload", async (HttpContext ctx) => { return await UploadInspect(ctx, cfg); }); // stateless: extract → render → discard
        app.MapGet("/r/{repo}", (string repo, HttpContext ctx) => Repo(ctx, store, db, cfg, repo, reportEmail));
        app.MapGet("/r/{repo}/commit/{hash}", (string repo, string hash, HttpContext ctx) => Commit(ctx, store, db, cfg, repo, hash));
        app.MapGet("/r/{repo}/compare/{a}/{b}", (string repo, string a, string b, HttpContext ctx) => Compare(ctx, store, db, cfg, repo, a, b));
        app.MapGet("/r/{repo}/world/{reff}", (string repo, string reff, HttpContext ctx) =>
            World(ctx, store, db, cfg, cache, rust, repo, reff, ctx.Request.Query["find"], ctx.Request.Query["q"]));
        app.MapGet("/r/{repo}/map/{reff}.png", (string repo, string reff, HttpContext ctx) => Map(ctx, store, renderQueue, db, cfg, repo, reff))
            .WithRequestTimeout(RenderTimeoutPolicy); // hard server-side deadline on cold renders
        app.MapGet("/r/{repo}/timeline", (string repo, HttpContext ctx) => Scrub(ctx, store, db, cfg, repo));

        if (!cfg.Accounts) return; // account + visibility surfaces only exist when accounts are enabled

        app.MapGet("/account", (HttpContext ctx) => Account(ctx, store, db, cfg));
        app.MapPost("/account/tokens", async (HttpContext ctx) =>
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is not { } me) return Results.Redirect("/auth/login");
            IFormCollection form = await ctx.Request.ReadFormAsync();
            string label = form["label"].ToString();
            string scope = form["scope"].ToString() == "read" ? "read" : "write";
            string? expiresAt = ExpiryFromDays(form["expires"].ToString());
            string secret = db.CreateToken(me.Id, label, scope, expiresAt);
            Log(ctx, audit, "token.create", null, $"{(string.IsNullOrWhiteSpace(label) ? "token" : label)} ({scope}{(expiresAt is null ? "" : ", expires " + expiresAt[..10])})");
            return Account(ctx, store, db, cfg, fresh: secret);
        });
        app.MapPost("/account/tokens/revoke", async (HttpContext ctx) =>
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is { } me)
            {
                string prefix = (await ctx.Request.ReadFormAsync())["prefix"].ToString();
                if (db.RevokeToken(me.Id, prefix)) Log(ctx, audit, "token.revoke", null, prefix);
            }
            return Results.Redirect("/account");
        });
        app.MapPost("/account/tokens/regenerate", async (HttpContext ctx) =>
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is not { } me) return Results.Redirect("/auth/login");
            string prefix = (await ctx.Request.ReadFormAsync())["prefix"].ToString();
            string? fresh = db.RegenerateToken(me.Id, prefix);
            if (fresh is null) return Results.Redirect("/account");
            Log(ctx, audit, "token.regenerate", null, prefix);
            return Account(ctx, store, db, cfg, fresh: fresh);
        });
        app.MapPost("/account/sign-out-everywhere", async (HttpContext ctx) =>
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is { } me)
            {
                int n = db.RevokeAllTokens(me.Id);
                db.BumpEpoch(me.Id); // invalidates every web session (incl. this one) on its next request
                Log(ctx, audit, "session.revoke-all", null, $"revoked {n} token(s) + all sessions");
            }
            return Results.Redirect("/");
        });
        app.MapPost("/account/delete", async (HttpContext ctx) => // GDPR/CCPA erasure (#35)
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is { } me)
            {
                IReadOnlyList<string> owned = db.DeleteUser(me.Id); // identity, tokens, grants, owned teams + repo metas
                foreach (string r in owned) PurgeRepoStorage(r, store, cache, maps);
                Log(ctx, audit, "account.delete", null, $"deleted account + {owned.Count} world(s)");
                audit.ForgetActor(me.Login); // erase the user's login + IP from prior audit entries (incl. the one just logged)
            }
            return Results.Redirect("/"); // the now-deleted user's session is rejected on its next request
        });
        app.MapPost("/r/{repo}/settings", async (string repo, HttpContext ctx) =>
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is { } me && Auth.CanManageSettings(db, repo, me.Id))
            {
                bool priv = (await ctx.Request.ReadFormAsync())["private"].ToString() is "on" or "true";
                db.SetPrivate(repo, priv);
                Log(ctx, audit, "visibility", repo, priv ? "→ private" : "→ public");
            }
            return Results.Redirect($"/r/{repo}");
        });
        app.MapPost("/r/{repo}/collaborators", async (string repo, HttpContext ctx) =>
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is { } me && Auth.CanManagePeople(db, repo, me.Id))
            {
                IFormCollection form = await ctx.Request.ReadFormAsync();
                HubUser? target = db.UserByLogin(form["login"].ToString().Trim());
                if (target is null) return Results.Redirect($"/r/{repo}?err=nouser");
                if (db.GetRepo(repo) is { } m && target.Id != m.OwnerId) // owner outranks any grant
                {
                    string role = Role(form["role"].ToString());
                    if (!Auth.CanGrantRole(db, repo, me.Id, role)) return Results.Redirect($"/r/{repo}?err=rank"); // can't grant ≥ your rank (MED-4)
                    db.SetCollab(repo, target.Id, role);
                    Log(ctx, audit, "collaborator.add", repo, $"{target.Login}={role}");
                }
            }
            return Results.Redirect($"/r/{repo}");
        });
        app.MapPost("/r/{repo}/collaborators/remove", async (string repo, HttpContext ctx) =>
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is { } me && Auth.CanManagePeople(db, repo, me.Id))
            {
                string userId = (await ctx.Request.ReadFormAsync())["userId"].ToString();
                db.RemoveCollab(repo, userId);
                Log(ctx, audit, "collaborator.remove", repo, db.GetUser(userId)?.Login ?? userId);
            }
            return Results.Redirect($"/r/{repo}");
        });
        app.MapPost("/r/{repo}/restore/{hash}", async (string repo, string hash, HttpContext ctx) => // roll a world back to a backup (#24)
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (!store.Exists(repo)) return Results.NotFound();
            if (Auth.Current(ctx) is not { } me || !Auth.CanWrite(cfg, db, repo, me.Id, admin: false))
                return Results.Redirect($"/r/{repo}");
            try
            {
                if (RestoreCommit(store.Open(repo), hash, me.Login) is { } restored)
                    Log(ctx, audit, "world.restore", repo, $"→ {hash[..Math.Min(10, hash.Length)]} as {restored[..10]}");
            }
            catch { return NotFound("backup", Auth.HeaderRight(ctx, cfg)); }
            return Results.Redirect($"/r/{repo}");
        });
        app.MapPost("/r/{repo}/transfer", async (string repo, HttpContext ctx) => // hand the repo to another user (#17)
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is { } me && db.GetRepo(repo)?.OwnerId == me.Id) // owner only
            {
                HubUser? target = db.UserByLogin((await ctx.Request.ReadFormAsync())["login"].ToString().Trim());
                if (target is null) return Results.Redirect($"/r/{repo}?err=nouser");
                if (db.TransferOwnership(repo, target.Id))
                    Log(ctx, audit, "ownership.transfer", repo, $"→ {target.Login}");
            }
            return Results.Redirect($"/r/{repo}");
        });

        // ---- teams ----
        app.MapGet("/teams", (HttpContext ctx) => Teams(ctx, db, cfg));
        app.MapPost("/teams", async (HttpContext ctx) =>
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is not { } me) return Results.Redirect("/auth/login");
            string name = (await ctx.Request.ReadFormAsync())["name"].ToString().Trim();
            if (!RepoStore.IsValidName(name)) return Results.Redirect("/teams?err=name");
            if (db.CreateTeam(name, me.Id) is null) return Results.Redirect("/teams?err=taken");
            return Results.Redirect($"/teams/{name}");
        });
        app.MapGet("/teams/{name}", (string name, HttpContext ctx) => TeamPage(ctx, db, cfg, name));
        app.MapPost("/teams/{name}/members", async (string name, HttpContext ctx) =>
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is { } me && db.GetTeam(name) is { } t && t.OwnerId == me.Id)
            {
                HubUser? target = db.UserByLogin((await ctx.Request.ReadFormAsync())["login"].ToString().Trim());
                if (target is null) return Results.Redirect($"/teams/{name}?err=nouser");
                db.AddTeamMember(name, target.Id);
            }
            return Results.Redirect($"/teams/{name}");
        });
        app.MapPost("/teams/{name}/members/remove", async (string name, HttpContext ctx) =>
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is { } me && db.GetTeam(name) is { } t && t.OwnerId == me.Id)
                db.RemoveTeamMember(name, (await ctx.Request.ReadFormAsync())["userId"].ToString());
            return Results.Redirect($"/teams/{name}");
        });
        app.MapPost("/teams/{name}/delete", async (string name, HttpContext ctx) =>
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is { } me && db.GetTeam(name) is { } t && t.OwnerId == me.Id) db.DeleteTeam(name);
            return Results.Redirect("/teams");
        });
        app.MapPost("/r/{repo}/teams", async (string repo, HttpContext ctx) =>
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is { } me && Auth.CanManagePeople(db, repo, me.Id))
            {
                IFormCollection form = await ctx.Request.ReadFormAsync();
                string team = form["team"].ToString().Trim();
                if (db.GetTeam(team) is null) return Results.Redirect($"/r/{repo}?err=noteam");
                string role = Role(form["role"].ToString());
                if (!Auth.CanGrantRole(db, repo, me.Id, role)) return Results.Redirect($"/r/{repo}?err=rank"); // can't grant ≥ your rank (MED-4)
                db.SetTeamGrant(repo, team, role);
                Log(ctx, audit, "team-grant.add", repo, $"{team}={role}");
            }
            return Results.Redirect($"/r/{repo}");
        });
        app.MapPost("/r/{repo}/teams/remove", async (string repo, HttpContext ctx) =>
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is { } me && Auth.CanManagePeople(db, repo, me.Id))
            {
                string team = (await ctx.Request.ReadFormAsync())["team"].ToString();
                db.RemoveTeamGrant(repo, team);
                Log(ctx, audit, "team-grant.remove", repo, team);
            }
            return Results.Redirect($"/r/{repo}");
        });

        // Per-repo audit history, owners/admins only (#16).
        app.MapGet("/r/{repo}/audit", (string repo, HttpContext ctx) => AuditView(ctx, store, db, cfg, audit, repo));

        app.MapPost("/r/{repo}/delete", async (string repo, HttpContext ctx) => // only the OWNER deletes a world (#35; audit MED-3)
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            // Deletion is irreversible (PurgeRepoStorage); restrict to the owner. Operators use the
            // master-token /admin/repos/{repo}/remove takedown — an admin *collaborator* must not be able
            // to destroy another user's owned world.
            if (Auth.Current(ctx) is { } me && db.GetRepo(repo)?.OwnerId == me.Id)
            {
                Log(ctx, audit, "world.delete", repo, "deleted"); // log before the repo is gone
                db.DeleteRepo(repo);
                PurgeRepoStorage(repo, store, cache, maps);
            }
            return Results.Redirect("/");
        });

        // Operator takedown: remove any world with the master token (a Bearer API, no cookie/CSRF). (#35)
        app.MapPost("/admin/repos/{repo}/remove", (string repo, HttpContext ctx) =>
        {
            (_, bool admin, _) = Auth.Identify(ctx.Request, cfg, db, out _);
            if (!admin) return Results.Text("master token required", statusCode: 403);
            if (!store.Exists(repo)) return Results.NotFound();
            audit.Append("operator", "world.takedown", repo, "removed by operator", "admin", ctx.Connection.RemoteIpAddress?.ToString());
            db.DeleteRepo(repo);
            PurgeRepoStorage(repo, store, cache, maps);
            return Results.Ok();
        });
    }

    /// <summary>Roll a world back to <paramref name="targetRef"/> by committing that backup's tree as a
    /// NEW backup on the current branch — reversible (the pre-restore state stays in history), never an
    /// in-place overwrite (#24). Returns the new commit, or null if the world is already at that state.</summary>
    internal static string? RestoreCommit(Repository repo, string targetRef, string actor)
    {
        string target = repo.ResolveRef(targetRef);
        string tree = repo.ReadCommit(target).Tree;
        string branch = repo.CurrentBranch() ?? "main";
        string? head = repo.ReadBranch(branch);
        if (head is not null && repo.ReadCommit(head).Tree == tree) return null; // already there
        string restored = repo.CreateCommit(tree, head is null ? [] : [head], $"restore to {target[..10]}", actor);
        repo.WriteBranch(branch, restored);
        return restored;
    }

    /// <summary>Remove a repo's on-disk bytes: the bare repo + its materialized-world and map caches.</summary>
    private static void PurgeRepoStorage(string repo, RepoStore store, WorldCache cache, MapCache maps)
    {
        store.Delete(repo);
        cache.Drop(repo);
        maps.Drop(repo);
    }

    /// <summary>The acceptable-use policy — the agreement that gives the operator a basis for takedown (#35).</summary>
    private static IResult Aup(HttpContext ctx, Auth.Config cfg, string? reportEmail)
    {
        string report = reportEmail is { Length: > 0 }
            ? $"""Report abuse to <a href="mailto:{E(reportEmail)}">{E(reportEmail)}</a>."""
            : "Report abuse to the operator of this hub.";
        return Page("Acceptable Use Policy", $$"""
            <h1>Acceptable Use Policy</h1>
            <p class="meta">By signing in and using this hub you agree to the following.</p>
            <h2>You will not</h2>
            <ul class="branches">
              <li>upload content that is illegal, infringing, or that you don't have the right to share;</li>
              <li>harass, threaten, or expose the private information (doxx) of any person;</li>
              <li>upload malware, or use a world's data to attack, locate, or grief other players;</li>
              <li>abuse the service — automated flooding, evading limits or suspensions, or attempting to
                  access worlds that aren't yours.</li>
            </ul>
            <h2>The operator may</h2>
            <ul class="branches">
              <li>remove any world and suspend or delete any account that violates this policy or the law;</li>
              <li>act on abuse reports and preserve an audit trail of administrative actions.</li>
            </ul>
            <h2>Your data</h2>
            <p>You can delete your account and the worlds you own at any time from your account page. The
            service is provided as-is, without warranty.</p>
            <p class="meta">{{report}}</p>
            """, Auth.HeaderRight(ctx, cfg));
    }

    private static IResult BadCsrf() => Results.Text("Invalid or expired form token — go back, reload the page, and retry.", statusCode: 400);
    private static string Role(string s) => HubDb.IsRole(s) ? s : "read";

    /// <summary>Record a web mutation in the audit trail (#16): actor login, action, repo, detail, IP.</summary>
    private static void Log(HttpContext ctx, AuditLog audit, string action, string? repo, string detail) =>
        audit.Append(Auth.Current(ctx)?.Login ?? "?", action, repo, detail, "web", ctx.Connection.RemoteIpAddress?.ToString());

    /// <summary>A token-create "expires in N days" field → an absolute ISO timestamp, or null for no expiry.</summary>
    private static string? ExpiryFromDays(string days) =>
        int.TryParse(days, out int d) && d > 0 ? DateTimeOffset.UtcNow.AddDays(d).ToString("o") : null;

    private static IResult Teams(HttpContext ctx, HubDb db, Auth.Config cfg)
    {
        string chip = Auth.HeaderRight(ctx, cfg);
        if (Auth.Current(ctx) is not { } me) return Results.Redirect("/auth/login");

        var b = new StringBuilder("<h1>Teams</h1>");
        b.Append("""<p class="meta">Group people, then grant a whole team read or write on a world from its page.</p>""");
        if (ctx.Request.Query["err"] == "taken") b.Append("""<p class="empty">That team name is taken.</p>""");
        if (ctx.Request.Query["err"] == "name") b.Append("""<p class="empty">Team names are letters/digits then letters/digits/._- (max 64).</p>""");

        var teams = db.TeamsForUser(me.Id);
        if (teams.Count == 0) b.Append("""<p class="empty">You're not in any teams yet.</p>""");
        else
        {
            b.Append("<ul class=\"repos\">");
            foreach (Team t in teams)
                b.Append($"""<li><a href="/teams/{E(t.Name)}">{E(t.Name)}</a>{(t.OwnerId == me.Id ? """ <span class="role role-owner">owner</span>""" : "")}<span class="meta">{t.Members.Count} member(s)</span></li>""");
            b.Append("</ul>");
        }
        b.Append($"""
            <h2>New team</h2>
            <form class="find" method="post" action="/teams">
              {Auth.CsrfField(ctx)}
              <input name="name" placeholder="team name — builders, admins…">
              <button>Create team</button>
            </form>
            """);
        return Page("Teams", b.ToString(), chip);
    }

    private static IResult TeamPage(HttpContext ctx, HubDb db, Auth.Config cfg, string name)
    {
        string chip = Auth.HeaderRight(ctx, cfg);
        if (Auth.Current(ctx) is not { } me) return Results.Redirect("/auth/login");
        if (db.GetTeam(name) is not { } t) return NotFound("team", chip);
        bool isOwner = t.OwnerId == me.Id;
        if (!isOwner && !t.Members.Contains(me.Id)) return NotFound("team", chip); // teams are visible to their members

        var b = new StringBuilder();
        b.Append("""<p class="back"><a href="/teams">← teams</a></p>""");
        b.Append($"<h1>{E(name)}</h1>");
        b.Append($"""<p class="meta">owned by {E(db.GetUser(t.OwnerId)?.Login ?? "?")}</p>""");
        if (isOwner && ctx.Request.Query["err"] == "nouser")
            b.Append("""<p class="empty">That user hasn't signed in to the hub yet — they need to sign in once before you can add them.</p>""");

        b.Append("<h2>Members</h2><ul class=\"repos\">");
        foreach (string uid in t.Members)
        {
            string remove = isOwner && uid != t.OwnerId
                ? $"""<form class="revoke" method="post" action="/teams/{E(name)}/members/remove">{Auth.CsrfField(ctx)}<input type="hidden" name="userId" value="{E(uid)}"><button>remove</button></form>"""
                : "";
            b.Append($"""<li>{E(db.GetUser(uid)?.Login ?? "?")}{(uid == t.OwnerId ? """ <span class="role role-owner">owner</span>""" : "")}{remove}</li>""");
        }
        b.Append("</ul>");

        if (isOwner)
        {
            b.Append($"""
                <form class="find" method="post" action="/teams/{E(name)}/members">
                  {Auth.CsrfField(ctx)}
                  <input name="login" placeholder="username — they must have signed in once">
                  <button>Add member</button>
                </form>
                <form class="settings" method="post" action="/teams/{E(name)}/delete" data-confirm="Delete team {E(name)}? Its grants are removed.">
                  {Auth.CsrfField(ctx)}
                  <button>Delete team</button>
                </form>
                """);
        }
        return Page(name, b.ToString(), chip);
    }

    /// <summary>A chrome-less, iframe-embeddable map of a world's latest backup (#25). Read-only (no
    /// controls/forms), so relaxing the frame headers for cross-site embedding is clickjacking-safe; a
    /// private world still 404s to a non-viewer.</summary>
    private static IResult Embed(HttpContext ctx, RepoStore store, HubDb db, Auth.Config cfg, string name)
    {
        if (!store.Exists(name) || !CanSee(ctx, db, cfg, name)) return Results.NotFound();
        if (store.Open(name).HeadCommit() is not { } head) return Results.NotFound();
        ctx.Response.Headers.Remove("X-Frame-Options"); // allow framing (set globally by the security-headers middleware)
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: https:; style-src 'self'; frame-ancestors *";
        string url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/r/{E(name)}";
        string html = $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{E(name)}} · mcahub</title><link rel="stylesheet" href="/style.css"></head>
            <body class="embed"><a href="{{url}}" target="_blank" rel="noopener"><img src="/r/{{E(name)}}/map/{{head}}.png" alt="map of {{E(name)}}" style="max-width:100%;display:block"></a>
            <p class="meta" style="padding:6px;margin:0"><a href="{{url}}" target="_blank" rel="noopener">{{E(name)}} on mcahub</a></p></body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    // ---- drag-and-drop upload (#26): stateless — extract, render, discard. No persistence, no token. ----

    private const long UploadMaxUncompressed = 512L * 1024 * 1024; // 512 MiB extracted (zip-bomb ceiling)
    private const int UploadMaxEntries = 200_000;
    private const int UploadRenderChunks = 4096;                   // bounded preview render

    private static IResult UploadPage(HttpContext ctx, Auth.Config cfg) => Page("Inspect a world", """
        <h1>Inspect a world</h1>
        <p class="meta">Drop a Minecraft world (a <code>.zip</code> that contains the <code>region/</code> folder) to see
        its top-down map and players — no CLI, no account. Your upload is rendered and <strong>immediately discarded</strong>.</p>
        <form id="upload" class="upload-drop" method="post" action="/upload" enctype="multipart/form-data">
          <p>Drag a <code>.zip</code> here, or choose one:</p>
          <input type="file" name="world" accept=".zip,application/zip" required>
          <button>Inspect</button>
        </form>
        """, Auth.HeaderRight(ctx, cfg));

    private static async Task<IResult> UploadInspect(HttpContext ctx, Auth.Config cfg)
    {
        string chip = Auth.HeaderRight(ctx, cfg);
        if (!ctx.Request.HasFormContentType) return NotFound("upload", chip);
        IFormCollection form;
        try { form = await ctx.Request.ReadFormAsync(ctx.RequestAborted); }
        catch { return Page("Upload", UploadError("That upload was too large or malformed."), chip); }
        if (form.Files.GetFile("world") is not { } file) return Results.Redirect("/upload");

        string tmp = Path.Combine(Path.GetTempPath(), "mcahub-upload-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            using (Stream s = file.OpenReadStream()) SafeUnzip.Extract(s, tmp, UploadMaxUncompressed, UploadMaxEntries);
            if (FindWorldDir(tmp) is not { } worldDir)
                return Page("Upload", UploadError("Couldn't find a <code>region/</code> folder in that .zip — is it a Minecraft world?"), chip);
            byte[] png = MapRenderer.Render(worldDir, out MapInfo info, UploadRenderChunks, ctx.RequestAborted);
            var players = new WorldQuery(worldDir).Players().Take(50).ToList();
            return Page("Uploaded world", UploadResult(png, info, players), chip);
        }
        catch (UnsafeUploadException e) { return Page("Upload", UploadError(E(e.Message)), chip); }
        catch (OperationCanceledException) { return Page("Upload", UploadError("Upload cancelled."), chip); }
        catch { return Page("Upload", UploadError("Couldn't read that as a Minecraft world."), chip); }
        finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ } }
    }

    private static string UploadError(string msg) =>
        $"""<h1>Couldn't inspect that</h1><p class="empty">{msg}</p><p class="back"><a href="/upload">← try another</a></p>""";

    /// <summary>Locate the world root (the dir holding <c>region/</c>) inside an extracted upload, which may
    /// be at the top level or one folder down.</summary>
    private static string? FindWorldDir(string root)
    {
        if (Directory.Exists(Path.Combine(root, "region"))) return root;
        foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Take(2000))
            if (Directory.Exists(Path.Combine(dir, "region"))) return dir;
        return null;
    }

    private static string UploadResult(byte[] png, MapInfo info, List<PlayerHit> players)
    {
        var sb = new StringBuilder();
        sb.Append("""<p class="back"><a href="/upload">← inspect another</a></p><h1>Uploaded world</h1>""");
        sb.Append($"""<p class="meta">{info.Chunks} chunks rendered{(info.Truncated ? " · truncated" : "")} · not stored</p>""");
        // Inline as a data: URI (CSP allows img-src data:) so the page is self-contained — the temp world is
        // already deleted, so there's nothing to serve a second request from.
        sb.Append($"""<div class="map"><img src="data:image/png;base64,{Convert.ToBase64String(png)}" alt="top-down map of the uploaded world"></div>""");
        sb.Append("<h2>Players</h2>");
        if (players.Count == 0) sb.Append("""<p class="empty">No player data.</p>""");
        else
        {
            sb.Append("<ul class=\"branches\">");
            foreach (PlayerHit p in players)
                sb.Append($"""<li>{E(p.Source)} <span class="meta">({p.X:0.#},{p.Y:0.#},{p.Z:0.#}) [{E(p.Dimension)}]</span></li>""");
            sb.Append("</ul>");
        }
        return sb.ToString();
    }

    private static IResult Home(HttpContext ctx, RepoStore store, HubDb db, Auth.Config cfg)
    {
        string chip = Auth.HeaderRight(ctx, cfg);
        string? meId = Auth.Current(ctx)?.Id;
        var repos = store.List().Where(r => Auth.CanRead(cfg, db, r.Name, meId, admin: false)).ToList();

        var b = new StringBuilder("<h1>Worlds</h1>");
        b.Append("""<p class="actions"><a href="/upload">⬆ Inspect a world</a> — drag in a .zip to see its map, no CLI</p>""");
        if (repos.Count == 0)
            b.Append("""<p class="empty">No worlds yet. Push one: <code>mcadiff push http://&lt;this-host&gt;/r/&lt;name&gt; main</code> (the hub auto-creates it).</p>""");
        else
        {
            b.Append("<ul class=\"repos\">");
            foreach (RepoSummary r in repos)
            {
                HubRepoMeta? m = db.GetRepo(r.Name);
                string badges = (m?.Private == true ? """ <span class="vis vis-private">private</span>""" : "")
                    + (m is not null ? $""" <span class="owner">{E(db.GetUser(m.OwnerId)?.Login ?? "?")}</span>""" : "");
                b.Append($"""<li><a href="/r/{E(r.Name)}">{E(r.Name)}</a>{badges}<span class="meta">{r.Branches} branch(es){(r.LastWhen is null ? "" : $" · last backup {When(r.LastWhen)}")}</span>{(r.LastMessage is null ? "" : $"<span class=\"msg\">{E(Oneline(r.LastMessage))}</span>")}</li>""");
            }
            b.Append("</ul>");
        }
        return Page("Worlds", b.ToString(), chip);
    }

    private static IResult Repo(HttpContext ctx, RepoStore store, HubDb db, Auth.Config cfg, string name, string? reportEmail)
    {
        string chip = Auth.HeaderRight(ctx, cfg);
        if (!store.Exists(name) || !CanSee(ctx, db, cfg, name)) return NotFound("world", chip);
        Repository repo = store.Open(name);
        HubUser? me = Auth.Current(ctx);
        HubRepoMeta? m = db.GetRepo(name);

        var b = new StringBuilder($"<h1>{E(name)}</h1>");
        if (m is not null)
        {
            string vis = m.Private ? """<span class="vis vis-private">private</span>""" : """<span class="vis vis-public">public</span>""";
            b.Append($"""<p class="meta">{vis} · owned by {E(db.GetUser(m.OwnerId)?.Login ?? "?")}</p>""");
            if (me is not null && Auth.CanManageSettings(db, name, me.Id))
                // Exposing a private world is consequential — guard "Make public" with a confirm (#31).
                b.Append($"""
                    <form class="settings" method="post" action="/r/{E(name)}/settings"{(m.Private ? """ data-confirm="Make this world PUBLIC? Anyone will be able to read and clone it — including player locations only after you also share with collaborators." """ : "")}>
                      {Auth.CsrfField(ctx)}
                      <input type="hidden" name="private" value="{(m.Private ? "off" : "on")}">
                      <button>Make {(m.Private ? "public" : "private")}</button>
                    </form>
                    """);
        }
        RenderCollaborators(b, ctx, db, name, m, me);
        string baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        b.Append($"""<p class="clone">Clone: <code>mcadiff clone {E(baseUrl)}/r/{E(name)} {E(name)}.mcagit</code></p>""");
        if (me is not null && Auth.CanManagePeople(db, name, me.Id))
        {
            b.Append($"""<p class="actions"><a href="/r/{E(name)}/audit">📜 audit log</a></p>""");
            if (m is not null && m.OwnerId == me.Id) // owner-only: transfer + the irreversible delete (audit MED-3)
            {
                b.Append($"""<form class="find" method="post" action="/r/{E(name)}/transfer" data-confirm="Transfer “{E(name)}” to another user? You'll be demoted to admin.">{Auth.CsrfField(ctx)}<input name="login" placeholder="new owner's username"><button>Transfer ownership</button></form>""");
                b.Append($"""<form class="settings" method="post" action="/r/{E(name)}/delete" data-confirm="Permanently delete the world “{E(name)}” and all its backups? This cannot be undone.">{Auth.CsrfField(ctx)}<button>Delete world</button></form>""");
            }
        }
        else if (reportEmail is { Length: > 0 }) // a non-owner viewing someone else's world (#35)
            b.Append($"""<p class="actions meta"><a href="mailto:{E(reportEmail)}?subject={E($"Report: {name}")}">⚑ Report this world</a></p>""");

        // A single branch is just noise to a non-dev — only show the section when there's a real choice (#31).
        var branches = repo.Branches().ToList();
        if (branches.Count > 1)
        {
            b.Append("<h2>Branches</h2><ul class=\"branches\">");
            foreach (string br in branches)
                if (repo.ReadBranch(br) is { } tip)
                    b.Append($"""<li><a href="/r/{E(name)}/commit/{tip}">{E(br)}</a> <span class="hash">{tip[..10]}</span></li>""");
            b.Append("</ul>");
        }

        if (repo.HeadCommit() is { } head)
        {
            b.Append($"""<h2>Backups</h2><p class="actions"><a href="/r/{E(name)}/timeline">🕑 time machine — scrub the map across backups</a></p><ol class="timeline">""");
            string? cur = head;
            int i = 0;
            for (; cur is not null && i < 50; i++)
            {
                CommitObject c = repo.ReadCommit(cur);
                string? par = repo.ParentsOf(cur) is [string pp, ..] ? pp : null;
                // The oldest backup has nothing to compare against — say so instead of dropping the link silently (#31).
                string actions = par is null
                    ? $"""<a href="/r/{E(name)}/world/{cur}">explore</a> · <span class="meta">first backup — nothing to compare</span>"""
                    : $"""<a href="/r/{E(name)}/world/{cur}">explore</a> · <a href="/r/{E(name)}/compare/{par}/{cur}">what changed</a>""";
                // A lazy-loaded map thumbnail makes history skimmable visually (#27); loading="lazy" + the
                // render gate + the render rate-limit keep a long timeline from triggering a render storm.
                b.Append($"""<li><a class="thumb" href="/r/{E(name)}/commit/{cur}"><img src="/r/{E(name)}/map/{cur}.png" alt="" loading="lazy"></a><a href="/r/{E(name)}/commit/{cur}">{cur[..10]}</a> <span class="cmsg">{E(Oneline(c.Message))}</span><span class="meta">{E(c.Author)} · {When(c.CommitTime ?? c.Time)}{(c.Parents.Count > 1 ? " · merge" : "")}{(c.Signature is not null ? " · signed" : "")}</span><span class="actions">{actions}</span></li>""");
                cur = par;
            }
            // The list caps at 50 — tell a grief-hunter the older event is still reachable via the time machine (#31).
            if (cur is not null)
                b.Append($"""<li class="empty">… {(i)}+ shown; older backups exist — use the <a href="/r/{E(name)}/timeline">time machine</a> to reach them.</li>""");
            b.Append("</ol>");
        }
        else b.Append("""<p class="empty">No backups yet.</p>""");
        return Page(name, b.ToString(), chip);
    }

    private static IResult Commit(HttpContext ctx, RepoStore store, HubDb db, Auth.Config cfg, string name, string hash)
    {
        string chip = Auth.HeaderRight(ctx, cfg);
        if (!store.Exists(name) || !CanSee(ctx, db, cfg, name)) return NotFound("world", chip);
        Repository repo = store.Open(name);
        string commit;
        try { commit = repo.ResolveRef(hash); } catch { return NotFound("backup", chip); }
        CommitObject c = repo.ReadCommit(commit);

        WorldDiff diff = CommitDiff(repo, commit, expand: true);
        GriefSummary g = GriefReport.Analyze(diff);

        string? parent = c.Parents.Count > 0 ? c.Parents[0] : null;
        var b = new StringBuilder();
        b.Append($"""<p class="back"><a href="/r/{E(name)}">← {E(name)}</a></p>""");
        b.Append($"<h1>Backup {commit[..10]}</h1>");
        b.Append($"""<p class="cmeta">{E(c.Message)}<br><span class="meta">{E(c.Author)} · {When(c.CommitTime ?? c.Time)}{(c.Signature is not null ? " · ✓ signed" : "")}</span></p>""");
        b.Append($"""<p class="actions"><a href="/r/{E(name)}/world/{commit}">explore this world</a>{(parent is null ? "" : $""" · <a href="/r/{E(name)}/compare/{parent}/{commit}">compare with previous</a>""")}</p>""");
        // Roll the world back to this backup — completes the headline promise (#24). The map/explorer above
        // is the preview; the restore itself adds a NEW backup (reversible), never an in-place overwrite.
        if (Auth.Current(ctx) is { } me && Auth.CanWrite(cfg, db, name, me.Id, admin: false))
            b.Append($"""<form class="settings" method="post" action="/r/{E(name)}/restore/{commit}" data-confirm="Restore the world to this backup? It adds a NEW backup with this content (so it's reversible) and becomes the latest state.">{Auth.CsrfField(ctx)}<button>⟲ Restore this backup</button></form>""");
        // Dimension toggle — Overworld / Nether / End (#27). The selected one renders; others link with ?dim.
        string dimSel = ctx.Request.Query["dim"].ToString() is "nether" or "end" ? ctx.Request.Query["dim"].ToString() : "overworld";
        string dimQ = dimSel == "overworld" ? "" : "?dim=" + dimSel;
        b.Append($"""<p class="actions">{DimLink(name, commit, "overworld", dimSel)} · {DimLink(name, commit, "nether", dimSel)} · {DimLink(name, commit, "end", dimSel)}</p>""");
        b.Append($"""<div class="map">{MapBox($"/r/{E(name)}/map/{commit}.png{dimQ}", $"top-down {dimSel} map of this backup")}</div>""");

        RenderGrief(b, g);
        b.Append("<h2>Changes</h2>");
        if (!diff.HasDifferences) b.Append("""<p class="empty">No changes from the previous backup.</p>""");
        bool canSeeData = Auth.CanSeePlayerData(cfg, db, name, Auth.Current(ctx)?.Id, admin: false); // #34
        RenderDiff(b, diff, canSeeData);
        // OpenGraph unfurl (#25): the map as the card image. A private world's map endpoint 404s to a
        // crawler (CanSee), so this never leaks a private image — it only unfurls for those who can see it.
        string desc = g.Destroyed + g.Built + g.Replaced > 0
            ? $"{g.Destroyed:N0} destroyed · {g.Built:N0} placed · {g.Replaced:N0} replaced"
            : Oneline(c.Message);
        string og = OgTags($"{name} · backup {commit[..10]}", desc, $"{ctx.Request.Scheme}://{ctx.Request.Host}/r/{E(name)}/map/{commit}.png");
        return Page($"Backup {commit[..10]}", b.ToString(), chip, og);
    }

    private static IResult Compare(HttpContext ctx, RepoStore store, HubDb db, Auth.Config cfg, string name, string a, string bRef)
    {
        string chip = Auth.HeaderRight(ctx, cfg);
        if (!store.Exists(name) || !CanSee(ctx, db, cfg, name)) return NotFound("world", chip);
        Repository repo = store.Open(name);
        string ca, cb;
        try { ca = repo.ResolveRef(a); cb = repo.ResolveRef(bRef); } catch { return NotFound("backup", chip); }

        WorldDiff diff = RefDiff(repo, ca, cb, expand: true);
        // Resolve each side's message + time so the page isn't two bare hashes — a grief-hunter needs to
        // confirm they're looking at the right window (#31).
        CommitObject commitA = repo.ReadCommit(ca), commitB = repo.ReadCommit(cb);
        var sb = new StringBuilder();
        sb.Append($"""<p class="back"><a href="/r/{E(name)}">← {E(name)}</a></p>""");
        sb.Append($"<h1>{ca[..10]} → {cb[..10]}</h1>");
        sb.Append($"""
            <div class="maps">
              <figure><figcaption>before · {ca[..10]}<br><span class="meta">{E(Oneline(commitA.Message))} · {When(commitA.CommitTime ?? commitA.Time)}</span></figcaption>{MapBox($"/r/{E(name)}/map/{ca}.png", "map before")}</figure>
              <figure><figcaption>after · {cb[..10]}<br><span class="meta">{E(Oneline(commitB.Message))} · {When(commitB.CommitTime ?? commitB.Time)}</span></figcaption>{MapBox($"/r/{E(name)}/map/{cb}.png", "map after")}</figure>
            </div>
            """);
        RenderGrief(sb, GriefReport.Analyze(diff));
        sb.Append("<h2>Changes</h2>");
        if (!diff.HasDifferences) sb.Append("""<p class="empty">No differences between these backups.</p>""");
        bool canSeeData = Auth.CanSeePlayerData(cfg, db, name, Auth.Current(ctx)?.Id, admin: false); // #34
        RenderDiff(sb, diff, canSeeData);
        return Page($"Compare {ca[..10]}…{cb[..10]}", sb.ToString(), chip);
    }

    private static IResult World(HttpContext ctx, RepoStore store, HubDb db, Auth.Config cfg, WorldCache cache,
        McaHub.Rust.RustEngine rust, string name, string refName, string? findKind, string? q)
    {
        string chip = Auth.HeaderRight(ctx, cfg);
        if (!store.Exists(name) || !CanSee(ctx, db, cfg, name)) return NotFound("world", chip);
        if (rust.RevParse(store.PathOf(name), refName) is not { } commit) return NotFound("backup", chip);

        string worldDir = cache.Materialize(name, store.PathOf(name), commit, ctx.RequestAborted); // immutable cache; first view materializes

        var sb = new StringBuilder();
        sb.Append($"""<p class="back"><a href="/r/{E(name)}/commit/{commit}">← backup {commit[..10]}</a></p>""");
        sb.Append($"<h1>World at {commit[..10]}</h1>");
        sb.Append($"""<div class="map">{MapBox($"/r/{E(name)}/map/{commit}.png", "top-down map of this backup")}</div>""");

        // Player coordinates, inventory, and sign text are a doxxing/griefing surface on a public world,
        // so in accounts mode they're shown only to collaborators (#34). Open/LAN mode shows everything.
        bool canSeeData = Auth.CanSeePlayerData(cfg, db, name, Auth.Current(ctx)?.Id, admin: false);

        var players = rust.Players(worldDir);
        sb.Append("<h2>Players</h2>");
        if (players.Count == 0) sb.Append("""<p class="empty">No player data.</p>""");
        else
        {
            sb.Append("<ul class=\"branches\">");
            foreach (var p in players)
            {
                string loc = p.Pos is { Length: >= 3 } ? $"({p.Pos[0]:0.#},{p.Pos[1]:0.#},{p.Pos[2]:0.#})" : "(?)";
                string hp = p.Health is { } hv ? $" · {hv:0.#} hp" : "";
                sb.Append(canSeeData
                    ? $"""<li>{E(p.Source)} <span class="meta">{loc} [{E(p.Dimension ?? "?")}]{hp}</span></li>"""
                    : $"""<li>{E(p.Source)} <span class="meta">(location hidden)</span></li>""");
            }
            sb.Append("</ul>");
        }

        if (!canSeeData)
            sb.Append("""<p class="empty">Player locations, inventory, and sign text are visible only to this world's collaborators.</p>""");
        else
        {
            sb.Append("""<p class="meta">⚠ This page reveals player locations, inventory counts, and sign text. Only this world's collaborators see them — to anyone else (if it's public) they show as “location hidden”.</p>""");
            sb.Append($"""
                <h2>Find</h2>
                <form class="find" method="get" action="/r/{E(name)}/world/{E(refName)}">
                  <select name="find">{Opt("entity", findKind, "creature / mob")}{Opt("block-entity", findKind, "storage (chest, barrel…)")}{Opt("sign", findKind, "signs")}</select>
                  <input name="q" placeholder="id or text — chest, zombie, spawn…" value="{E(q)}">
                  <button>Search</button>
                </form>
                """);
            if (findKind is { Length: > 0 } && q is { Length: > 0 })
            {
                sb.Append("<ul class=\"changes\">");
                int n = 0;
                if (findKind == "entity") foreach (var h in rust.Find(worldDir, "entity", q).Take(300)) { string loc = h.Pos is { Length: >= 3 } ? $"({h.Pos[0]:0.#},{h.Pos[1]:0.#},{h.Pos[2]:0.#})" : $"({h.X},{h.Y},{h.Z})"; sb.Append($"""<li><code>{E(h.Id)}</code> {loc}</li>"""); n++; }
                else if (findKind == "block-entity") foreach (var h in rust.Find(worldDir, "block-entity", q).Take(300)) { sb.Append($"""<li><code>{E(h.Id)}</code> ({h.X},{h.Y},{h.Z})</li>"""); n++; }
                else if (findKind == "sign") foreach (var h in rust.Find(worldDir, "sign").Where(s => s.Text is { } t && string.Join(" ", t).Contains(q, StringComparison.OrdinalIgnoreCase)).Take(300)) { sb.Append($"""<li>({h.X},{h.Y},{h.Z}) "{E(string.Join(" / ", h.Text ?? []))}"</li>"""); n++; }
                if (n == 0) sb.Append("""<li class="empty">No matches.</li>""");
                sb.Append("</ul>");
            }
        }
        return Page($"World {commit[..10]}", sb.ToString(), chip);
    }

    /// <summary>One Overworld/Nether/End toggle link for the backup map (#27); the current one is bold.</summary>
    private static string DimLink(string repo, string commit, string dim, string current)
    {
        string label = dim switch { "nether" => "Nether", "end" => "End", _ => "Overworld" };
        if (dim == current) return $"<strong>{label}</strong>";
        string q = dim == "overworld" ? "" : "?dim=" + dim;
        return $"""<a href="/r/{E(repo)}/commit/{commit}{q}">{label}</a>""";
    }

    /// <summary>A map image wrapped in a loading-aware box (the layout's script reveals it once the PNG
    /// — which can take a few seconds to render cold — finishes loading).</summary>
    private static string MapBox(string url, string alt) => $"""
        <div class="map-box">
          <div class="map-status"><span class="spinner"></span> Generating map…</div>
          <img src="{url}" alt="{E(alt)}" loading="lazy">
        </div>
        """;

    private static async Task<IResult> Map(HttpContext ctx, RepoStore store, RenderQueue renderQueue, HubDb db, Auth.Config cfg, string name, string refName)
    {
        if (!store.Exists(name) || !CanSee(ctx, db, cfg, name)) return Results.NotFound();
        Repository repo = store.Open(name);
        string commit;
        try { commit = repo.ResolveRef(refName); } catch { return Results.NotFound(); }
        MapDimension dim = ctx.Request.Query["dim"].ToString() switch { "nether" => MapDimension.Nether, "end" => MapDimension.End, _ => MapDimension.Overworld };
        // A cold render runs as a background job; a client disconnect won't abort it (it finishes + caches).
        byte[] png = await renderQueue.RequestAsync(name, commit, dim, ctx.RequestAborted);
        ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable"; // a commit's map (per dimension) never changes
        return Results.Bytes(png, "image/png");
    }

    // ---- time-machine scrubber ----

    private sealed record ScrubPoint(string Hash, string Short, string Msg, string Author, string When);

    private static IResult Scrub(HttpContext ctx, RepoStore store, HubDb db, Auth.Config cfg, string name)
    {
        string chip = Auth.HeaderRight(ctx, cfg);
        if (!store.Exists(name) || !CanSee(ctx, db, cfg, name)) return NotFound("world", chip);
        Repository repo = store.Open(name);

        var pts = new List<ScrubPoint>();
        string? cur = repo.HeadCommit();
        for (int i = 0; cur is not null && i < 200; i++)
        {
            CommitObject c = repo.ReadCommit(cur);
            pts.Add(new ScrubPoint(cur, cur[..10], Oneline(c.Message), c.Author, When(c.CommitTime ?? c.Time)));
            cur = repo.ParentsOf(cur) is [string p, ..] ? p : null;
        }
        pts.Reverse(); // oldest → newest, so the slider runs left(past) → right(present)

        if (pts.Count == 0)
            return Page($"Time machine · {name}", $"""<p class="back"><a href="/r/{E(name)}">← {E(name)}</a></p><h1>Time machine · {E(name)}</h1><p class="empty">No backups yet.</p>""", chip);

        int max = pts.Count - 1;
        string body = TimeMachineTemplate
            .Replace("%%NAME%%", E(name))
            .Replace("%%MAX%%", max.ToString())
            .Replace("%%DIS%%", pts.Count < 2 ? "disabled" : "")
            .Replace("%%DATA%%", JsonSerializer.Serialize(pts));
        return Page($"Time machine · {name}", body, chip);
    }

    // Placeholders (not C# interpolation) so the markup stays literal. %%NAME%% is pre-HTML-escaped;
    // %%DATA%% is System.Text.Json output (escapes <,>,& — safe inside the JSON data-island that the
    // static /app.js reads). No inline executable script, so the CSP can stay strict.
    private const string TimeMachineTemplate = """
        <p class="back"><a href="/r/%%NAME%%">← %%NAME%%</a></p>
        <h1>Time machine · %%NAME%%</h1>
        <div class="map"><div class="map-box"><div class="map-status"><span class="spinner"></span> Generating map…</div><img id="tm-map" alt="world map at the selected backup"></div></div>
        <div class="scrubber">
          <button id="tm-play" type="button">▶ play</button>
          <input id="tm-scrub" type="range" min="0" max="%%MAX%%" value="%%MAX%%" %%DIS%%>
          <span class="meta" id="tm-when"></span>
        </div>
        <p class="cmeta" id="tm-cap"></p>
        <script type="application/json" id="tm-data" data-repo="%%NAME%%">%%DATA%%</script>
        """;

    private static IResult AuditView(HttpContext ctx, RepoStore store, HubDb db, Auth.Config cfg, AuditLog audit, string name)
    {
        string chip = Auth.HeaderRight(ctx, cfg);
        if (!store.Exists(name) || !CanSee(ctx, db, cfg, name)) return NotFound("world", chip);
        if (Auth.Current(ctx) is not { } me || !Auth.CanManagePeople(db, name, me.Id)) return NotFound("world", chip); // owners/admins only

        var b = new StringBuilder($"""<p class="back"><a href="/r/{E(name)}">← {E(name)}</a></p><h1>Audit · {E(name)}</h1>""");
        b.Append("""<p class="meta">Role, visibility, ownership, ref, and team-grant changes for this world.</p>""");
        IReadOnlyList<AuditEntry> entries = audit.Recent(name, 200);
        if (entries.Count == 0) b.Append("""<p class="empty">No audit entries yet.</p>""");
        else
        {
            b.Append("<ul class=\"changes\">");
            foreach (AuditEntry e in entries)
                b.Append($"""<li><span class="meta">{E(When(e.At))}</span> <code>{E(e.Action)}</code> by {E(e.Actor)}{(e.Detail is null ? "" : $" — {E(e.Detail)}")} <span class="meta">{E(e.Source)}{(e.Ip is null ? "" : " · " + E(e.Ip))}</span></li>""");
            b.Append("</ul>");
        }
        return Page($"Audit · {name}", b.ToString(), chip);
    }

    // ---- account ----

    private static IResult Account(HttpContext ctx, RepoStore store, HubDb db, Auth.Config cfg, string? fresh = null)
    {
        string chip = Auth.HeaderRight(ctx, cfg);
        if (Auth.Current(ctx) is not { } me) return Results.Redirect("/auth/login");

        var b = new StringBuilder($"<h1>{E(me.Login)}</h1>");

        if (fresh is not null)
            b.Append($"""
                <div class="flash">
                  <strong>New token</strong> — copy it now, it won't be shown again:
                  <div><code class="token">{E(fresh)}</code></div>
                  <div class="g-where">Use it: <code>mcadiff push {E($"{ctx.Request.Scheme}://{ctx.Request.Host}")}/r/&lt;name&gt; main --token {E(fresh)}</code></div>
                </div>
                """);

        b.Append("<h2>Personal access tokens</h2>");
        b.Append("""<p class="meta">The CLI can't do a browser login, so <code>mcadiff push/clone</code> against private worlds (or any push in accounts mode) authenticates with one of these.</p>""");
        var tokens = db.ListTokens(me.Id);
        if (tokens.Count == 0) b.Append("""<p class="empty">No tokens yet.</p>""");
        else
        {
            b.Append("<ul class=\"repos\">");
            foreach (TokenInfo t in tokens)
            {
                bool expired = t.ExpiresAt is { } x && DateTimeOffset.TryParse(x, out var xd) && xd <= DateTimeOffset.UtcNow;
                string expiry = t.ExpiresAt is null ? "" : expired ? " · <span class=\"vis vis-private\">expired</span>" : $" · expires {When(t.ExpiresAt)}";
                b.Append($"""<li><code>{E(t.Prefix)}…</code> <span class="cmsg">{E(t.Label)}</span> <span class="role role-{E(t.Scope)}">{E(t.Scope)}</span><span class="meta">created {When(t.CreatedAt)}{(t.LastUsedAt is null ? " · never used" : $" · last used {When(t.LastUsedAt)}")}{expiry}</span><form class="revoke" method="post" action="/account/tokens/regenerate">{Auth.CsrfField(ctx)}<input type="hidden" name="prefix" value="{E(t.Prefix)}"><button>regenerate</button></form><form class="revoke" method="post" action="/account/tokens/revoke">{Auth.CsrfField(ctx)}<input type="hidden" name="prefix" value="{E(t.Prefix)}"><button>revoke</button></form></li>""");
            }
            b.Append("</ul>");
        }
        b.Append($"""
            <form class="find" method="post" action="/account/tokens">
              {Auth.CsrfField(ctx)}
              <input name="label" placeholder="label — laptop, backup-server…">
              <select name="scope"><option value="write">write</option><option value="read">read</option></select>
              <input name="expires" type="number" min="1" placeholder="expires in N days (optional)">
              <button>Create token</button>
            </form>
            <form class="settings" method="post" action="/account/sign-out-everywhere" data-confirm="Sign out everywhere? This revokes all your tokens and signs out every session.">
              {Auth.CsrfField(ctx)}
              <button>Sign out everywhere</button>
            </form>
            <form class="settings" method="post" action="/account/delete" data-confirm="Permanently delete your account? This erases your identity, tokens, and all worlds you own. This cannot be undone.">
              {Auth.CsrfField(ctx)}
              <button>Delete my account</button>
            </form>
            """);

        var mine = store.List().Where(r => db.GetRepo(r.Name)?.OwnerId == me.Id).ToList();
        b.Append("<h2>Your worlds</h2>");
        if (mine.Count == 0) b.Append("""<p class="empty">None yet — push one and it's yours.</p>""");
        else
        {
            b.Append("<ul class=\"repos\">");
            foreach (RepoSummary r in mine)
            {
                bool priv = db.GetRepo(r.Name)?.Private == true;
                b.Append($"""<li><a href="/r/{E(r.Name)}">{E(r.Name)}</a> <span class="vis vis-{(priv ? "private" : "public")}">{(priv ? "private" : "public")}</span><span class="meta">{r.Branches} branch(es)</span></li>""");
            }
            b.Append("</ul>");
        }

        var shared = db.CollabsForUser(me.Id).Where(c => store.Exists(c.Repo)).ToList();
        if (shared.Count > 0)
        {
            b.Append("<h2>Shared with you</h2><ul class=\"repos\">");
            foreach (Collab c in shared)
                b.Append($"""<li><a href="/r/{E(c.Repo)}">{E(c.Repo)}</a> <span class="role role-{E(c.Role)}">{E(c.Role)}</span><span class="meta">owned by {E(db.GetUser(db.GetRepo(c.Repo)?.OwnerId)?.Login ?? "?")}</span></li>""");
            b.Append("</ul>");
        }
        return Page(me.Login, b.ToString(), chip);
    }

    private static void RenderCollaborators(StringBuilder b, HttpContext ctx, HubDb db, string name, HubRepoMeta? m, HubUser? me)
    {
        if (m is null) return;
        bool canPeople = me is not null && Auth.CanManagePeople(db, name, me.Id); // owner/admin manage collaborators
        var collabs = db.CollabsOf(name);
        var teamGrants = db.TeamGrantsOf(name);
        if (collabs.Count == 0 && teamGrants.Count == 0 && !canPeople) return; // nothing to show, no controls to offer

        b.Append("<h2>Collaborators</h2>");
        if (canPeople && ctx.Request.Query["err"] == "nouser")
            b.Append("""<p class="empty">That user hasn't signed in to the hub yet — they need to sign in once before you can add them.</p>""");
        if (canPeople && ctx.Request.Query["err"] == "rank")
            b.Append("""<p class="empty">You can't grant a role at or above your own — only the owner can grant admin.</p>""");

        b.Append("<ul class=\"repos\">");
        b.Append($"""<li>{E(db.GetUser(m.OwnerId)?.Login ?? "?")} <span class="role role-owner">owner</span></li>""");
        foreach (Collab c in collabs)
        {
            string remove = canPeople
                ? $"""<form class="revoke" method="post" action="/r/{E(name)}/collaborators/remove">{Auth.CsrfField(ctx)}<input type="hidden" name="userId" value="{E(c.UserId)}"><button>remove</button></form>"""
                : "";
            b.Append($"""<li>{E(db.GetUser(c.UserId)?.Login ?? "?")} <span class="role role-{E(c.Role)}">{E(c.Role)}</span>{remove}</li>""");
        }
        b.Append("</ul>");

        if (canPeople)
            b.Append($"""
                <form class="find" method="post" action="/r/{E(name)}/collaborators">
                  {Auth.CsrfField(ctx)}
                  <input name="login" placeholder="username — they must have signed in once">
                  <select name="role">{RoleOpts()}</select>
                  <button>Add collaborator</button>
                </form>
                """);

        if (teamGrants.Count == 0 && !canPeople) return;
        b.Append("<h2>Teams with access</h2>");
        if (canPeople && ctx.Request.Query["err"] == "noteam")
            b.Append("""<p class="empty">No team by that name.</p>""");
        if (teamGrants.Count > 0)
        {
            b.Append("<ul class=\"repos\">");
            foreach (TeamGrant g in teamGrants)
            {
                string remove = canPeople
                    ? $"""<form class="revoke" method="post" action="/r/{E(name)}/teams/remove">{Auth.CsrfField(ctx)}<input type="hidden" name="team" value="{E(g.TeamName)}"><button>remove</button></form>"""
                    : "";
                string teamName = db.GetTeam(g.TeamName) is not null ? $"""<a href="/teams/{E(g.TeamName)}">{E(g.TeamName)}</a>""" : E(g.TeamName);
                b.Append($"""<li>👥 {teamName} <span class="role role-{E(g.Role)}">{E(g.Role)}</span>{remove}</li>""");
            }
            b.Append("</ul>");
        }
        if (canPeople)
            b.Append($"""
                <form class="find" method="post" action="/r/{E(name)}/teams">
                  {Auth.CsrfField(ctx)}
                  <input name="team" placeholder="team name">
                  <select name="role">{RoleOpts()}</select>
                  <button>Grant team</button>
                </form>
                """);
    }

    // One-phrase capability hints so read/write/maintain/admin mean something to a non-dev (#31).
    private static string RoleOpts() =>
        Opt("read", "read", "read — browse & clone") + Opt("write", null, "write — push new backups") +
        Opt("maintain", null, "maintain — + change visibility") + Opt("admin", null, "admin — + add/remove people");

    private static bool CanSee(HttpContext ctx, HubDb db, Auth.Config cfg, string repo) =>
        Auth.CanRead(cfg, db, repo, Auth.Current(ctx)?.Id, admin: false);

    private static string Opt(string v, string? sel, string? label = null) => $"""<option value="{v}"{(v == sel ? " selected" : "")}>{E(label ?? v)}</option>""";

    private static void RenderGrief(StringBuilder b, GriefSummary g)
    {
        if (g.Destroyed + g.Built + g.Replaced == 0) return;
        b.Append("<div class=\"grief\">");
        b.Append($"""<span class="g-d">{g.Destroyed:N0} destroyed</span> <span class="g-b">{g.Built:N0} placed</span> <span class="g-r">{g.Replaced:N0} replaced</span>""");
        if (g.Min is { } mn && g.Max is { } mx && g.Center is { } ce)
            b.Append($"""<div class="g-where">destruction spans ({mn.X},{mn.Y},{mn.Z})–({mx.X},{mx.Y},{mx.Z}), centered ~({ce.X},{ce.Y},{ce.Z})</div>""");
        if (g.TopDestroyed.Count > 0)
            b.Append("<div class=\"g-top\">most destroyed: " + string.Join(", ", g.TopDestroyed.Select(t => $"{E(Short(t.Block))} ×{t.Count}")) + "</div>");
        b.Append("</div>");
    }

    // ---- diff rendering ----

    private const int MaxFiles = 200, MaxChunks = 80, MaxChanges = 60;

    private static void RenderDiff(StringBuilder b, WorldDiff diff, bool canSeeData)
    {
        foreach (FileDiff f in diff.Files.Take(MaxFiles))
        {
            b.Append($"""<div class="file"><div class="fh"><span class="st st-{f.Status.ToString().ToLowerInvariant()}">{f.Status}</span> {E(f.RelativePath)}{(f.ItemCount is { } n ? $" <span class=\"meta\">({n} chunks)</span>" : "")}</div>""");
            if (f.Error is { } err) b.Append($"""<div class="err">{E(err)}</div>""");

            // #34: player data (positions, inventory, sign text) is doxxing material, hidden from non-collaborators
            // on a public world — same gate the world explorer uses. A whole player-data file (level.dat, playerdata/,
            // entities/) is suppressed; container/sign (block-entity) and entity changes inside region files are
            // dropped per-row. Block/biome changes and the grief summary stay public (that's the headline feature).
            if (!canSeeData && SensitiveFile(f.RelativePath))
            {
                int total = f.Chunks.Sum(ch => ch.Changes.Count) + f.Changes.Count;
                b.Append($"""<div class="more">{total} player-data change(s) hidden — visible only to this world's collaborators</div></div>""");
                continue;
            }

            foreach (ChunkDiff ch in f.Chunks.Take(MaxChunks))
            {
                b.Append($"""<div class="chunk">chunk ({ch.Pos.X}, {ch.Pos.Z})</div><ul class="changes">""");
                int hidden = 0;
                foreach (NbtChange c in ch.Changes.Take(MaxChanges))
                {
                    if (!canSeeData && SensitivePath(c.Path)) { hidden++; continue; }
                    b.Append(Change(c));
                }
                if (hidden > 0) b.Append($"<li class=\"more\">… {hidden} container/sign/entity change(s) hidden</li>");
                if (ch.Changes.Count > MaxChanges) b.Append($"<li class=\"more\">… {ch.Changes.Count - MaxChanges} more</li>");
                b.Append("</ul>");
            }
            if (f.Chunks.Count > MaxChunks) b.Append($"<div class=\"more\">… {f.Chunks.Count - MaxChunks} more chunks</div>");
            if (f.Changes.Count > 0)
            {
                b.Append("<ul class=\"changes\">");
                int hidden = 0;
                foreach (NbtChange c in f.Changes.Take(MaxChanges))
                {
                    if (!canSeeData && SensitivePath(c.Path)) { hidden++; continue; }
                    b.Append(Change(c));
                }
                if (hidden > 0) b.Append($"<li class=\"more\">… {hidden} container/sign/entity change(s) hidden</li>");
                if (f.Changes.Count > MaxChanges) b.Append($"<li class=\"more\">… {f.Changes.Count - MaxChanges} more</li>");
                b.Append("</ul>");
            }
            b.Append("</div>");
        }
        if (diff.Files.Count > MaxFiles) b.Append($"<div class=\"more\">… {diff.Files.Count - MaxFiles} more files</div>");
    }

    // #34 redaction: which diff entries carry player PII (positions / inventory / sign text). internal for tests.
    internal static bool SensitiveFile(string rel) =>
        rel is "level.dat" or "level.dat_old"
        || rel.StartsWith("playerdata/", StringComparison.OrdinalIgnoreCase)
        || rel.StartsWith("playerdata\\", StringComparison.OrdinalIgnoreCase)
        || rel.StartsWith("entities/", StringComparison.OrdinalIgnoreCase)
        || rel.StartsWith("entities\\", StringComparison.OrdinalIgnoreCase);

    // Catches block_entities (chests = inventory, signs = text) and chunk-level Entities (positions / names).
    // No public block_states/biome/section path contains "entit", so grief block changes are never redacted.
    internal static bool SensitivePath(string path) => path.Contains("entit", StringComparison.OrdinalIgnoreCase);

    private static string Change(NbtChange c)
    {
        string kind = c.Kind.ToString().ToLowerInvariant();
        string val = c.Kind switch
        {
            ChangeKind.Added => $"+ {E(c.NewValue)}",
            ChangeKind.Removed => $"− {E(c.OldValue)}",
            _ => $"{E(c.OldValue)} → {E(c.NewValue)}",
        };
        string note = c.Note is { } n ? $" <span class=\"note\">({E(n)})</span>" : "";
        return $"""<li class="ch ch-{kind}"><code>{E(c.Path)}</code>: {val}{note}</li>""";
    }

    private static WorldDiff CommitDiff(Repository repo, string commit, bool expand)
    {
        string? parent = repo.ReadCommit(commit).Parents is [string p, ..] ? p : null;
        Manifest mOld = parent is not null ? repo.ReadManifest(repo.ReadCommit(parent).Tree) : new Manifest();
        Manifest mNew = repo.ReadManifest(repo.ReadCommit(commit).Tree);
        return RepoDiffer.Diff(
            parent is null ? "(root)" : parent[..10], mOld, new RepoDiffer.CommitSource(repo, mOld),
            commit[..10], mNew, new RepoDiffer.CommitSource(repo, mNew), new DiffRunOptions(ExpandArrays: expand));
    }

    private static WorldDiff RefDiff(Repository repo, string ca, string cb, bool expand)
    {
        Manifest mA = repo.ReadManifest(repo.ReadCommit(ca).Tree);
        Manifest mB = repo.ReadManifest(repo.ReadCommit(cb).Tree);
        return RepoDiffer.Diff(
            ca[..10], mA, new RepoDiffer.CommitSource(repo, mA),
            cb[..10], mB, new RepoDiffer.CommitSource(repo, mB), new DiffRunOptions(ExpandArrays: expand));
    }

    private static string Oneline(string msg) { int nl = msg.IndexOf('\n'); return nl < 0 ? msg : msg[..nl]; }
    private static string Short(string id) => id.StartsWith("minecraft:") ? id["minecraft:".Length..] : id;
    private static string When(string iso)
    {
        if (!DateTimeOffset.TryParse(iso, out var d)) return iso;
        string abs = d.ToString("yyyy-MM-dd HH:mm");
        TimeSpan ago = DateTimeOffset.UtcNow - d.ToUniversalTime();
        // A grief-hunter thinks in "last night", not dates — prefix a relative time for recent events (#31).
        if (ago < TimeSpan.Zero) return abs;
        if (ago < TimeSpan.FromMinutes(1)) return $"just now · {abs}";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes}m ago · {abs}";
        if (ago < TimeSpan.FromHours(24)) return $"{(int)ago.TotalHours}h ago · {abs}";
        if (ago < TimeSpan.FromDays(7)) return $"{(int)ago.TotalDays}d ago · {abs}";
        return abs;
    }
}
