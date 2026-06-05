using System.Text;
using McaDiff.Diff;
using McaDiff.Query;
using McaDiff.Repo;
using static McadiffHub.Html;

namespace McadiffHub;

/// <summary>Server-rendered pages: the repo list, a repo's backup timeline, a backup's view (semantic
/// diff + grief summary), compare-any-two-backups, a world explorer, and the account page (personal
/// access tokens + per-repo visibility). Private worlds are hidden from anyone but their owner.</summary>
public static class Pages
{
    public static void MapPages(WebApplication app, RepoStore store, WorldCache cache, HubDb db, Auth.Config cfg)
    {
        app.MapGet("/", (HttpContext ctx) => Home(ctx, store, db, cfg));
        app.MapGet("/r/{repo}", (string repo, HttpContext ctx) => Repo(ctx, store, db, cfg, repo));
        app.MapGet("/r/{repo}/commit/{hash}", (string repo, string hash, HttpContext ctx) => Commit(ctx, store, db, cfg, repo, hash));
        app.MapGet("/r/{repo}/compare/{a}/{b}", (string repo, string a, string b, HttpContext ctx) => Compare(ctx, store, db, cfg, repo, a, b));
        app.MapGet("/r/{repo}/world/{reff}", (string repo, string reff, HttpContext ctx) =>
            World(ctx, store, db, cfg, cache, repo, reff, ctx.Request.Query["find"], ctx.Request.Query["q"]));

        if (!cfg.Accounts) return; // account + visibility surfaces only exist when accounts are enabled

        app.MapGet("/account", (HttpContext ctx) => Account(ctx, store, db, cfg));
        app.MapPost("/account/tokens", async (HttpContext ctx) =>
        {
            if (Auth.Current(ctx) is not { } me) return Results.Redirect("/auth/login");
            string label = (await ctx.Request.ReadFormAsync())["label"].ToString();
            string secret = db.CreateToken(me.Id, label);
            return Account(ctx, store, db, cfg, fresh: secret);
        });
        app.MapPost("/account/tokens/revoke", async (HttpContext ctx) =>
        {
            if (Auth.Current(ctx) is { } me)
                db.RevokeToken(me.Id, (await ctx.Request.ReadFormAsync())["prefix"].ToString());
            return Results.Redirect("/account");
        });
        app.MapPost("/r/{repo}/settings", async (string repo, HttpContext ctx) =>
        {
            if (Auth.Current(ctx) is { } me && db.GetRepo(repo) is { } m && m.OwnerId == me.Id)
                db.SetPrivate(repo, (await ctx.Request.ReadFormAsync())["private"].ToString() is "on" or "true");
            return Results.Redirect($"/r/{repo}");
        });
        app.MapPost("/r/{repo}/collaborators", async (string repo, HttpContext ctx) =>
        {
            if (Auth.Current(ctx) is { } me && db.GetRepo(repo) is { } m && m.OwnerId == me.Id)
            {
                IFormCollection form = await ctx.Request.ReadFormAsync();
                string role = form["role"].ToString() is "write" ? "write" : "read";
                HubUser? target = db.UserByLogin(form["login"].ToString().Trim());
                if (target is null) return Results.Redirect($"/r/{repo}?err=nouser");
                if (target.Id != m.OwnerId) db.SetCollab(repo, target.Id, role); // owner is already above any grant
            }
            return Results.Redirect($"/r/{repo}");
        });
        app.MapPost("/r/{repo}/collaborators/remove", async (string repo, HttpContext ctx) =>
        {
            if (Auth.Current(ctx) is { } me && db.GetRepo(repo) is { } m && m.OwnerId == me.Id)
                db.RemoveCollab(repo, (await ctx.Request.ReadFormAsync())["userId"].ToString());
            return Results.Redirect($"/r/{repo}");
        });

        // ---- teams ----
        app.MapGet("/teams", (HttpContext ctx) => Teams(ctx, db, cfg));
        app.MapPost("/teams", async (HttpContext ctx) =>
        {
            if (Auth.Current(ctx) is { } me)
            {
                string name = (await ctx.Request.ReadFormAsync())["name"].ToString().Trim();
                if (!RepoStore.IsValidName(name)) return Results.Redirect("/teams?err=name");
                if (db.CreateTeam(name, me.Id) is null) return Results.Redirect("/teams?err=taken");
                return Results.Redirect($"/teams/{name}");
            }
            return Results.Redirect("/auth/login");
        });
        app.MapGet("/teams/{name}", (string name, HttpContext ctx) => TeamPage(ctx, db, cfg, name));
        app.MapPost("/teams/{name}/members", async (string name, HttpContext ctx) =>
        {
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
            if (Auth.Current(ctx) is { } me && db.GetTeam(name) is { } t && t.OwnerId == me.Id)
                db.RemoveTeamMember(name, (await ctx.Request.ReadFormAsync())["userId"].ToString());
            return Results.Redirect($"/teams/{name}");
        });
        app.MapPost("/teams/{name}/delete", (string name, HttpContext ctx) =>
        {
            if (Auth.Current(ctx) is { } me && db.GetTeam(name) is { } t && t.OwnerId == me.Id) db.DeleteTeam(name);
            return Results.Redirect("/teams");
        });
        app.MapPost("/r/{repo}/teams", async (string repo, HttpContext ctx) =>
        {
            if (Auth.Current(ctx) is { } me && db.GetRepo(repo) is { } m && m.OwnerId == me.Id)
            {
                IFormCollection form = await ctx.Request.ReadFormAsync();
                string role = form["role"].ToString() is "write" ? "write" : "read";
                string team = form["team"].ToString().Trim();
                if (db.GetTeam(team) is null) return Results.Redirect($"/r/{repo}?err=noteam");
                db.SetTeamGrant(repo, team, role);
            }
            return Results.Redirect($"/r/{repo}");
        });
        app.MapPost("/r/{repo}/teams/remove", async (string repo, HttpContext ctx) =>
        {
            if (Auth.Current(ctx) is { } me && db.GetRepo(repo) is { } m && m.OwnerId == me.Id)
                db.RemoveTeamGrant(repo, (await ctx.Request.ReadFormAsync())["team"].ToString());
            return Results.Redirect($"/r/{repo}");
        });
    }

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
        b.Append("""
            <h2>New team</h2>
            <form class="find" method="post" action="/teams">
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
                ? $"""<form class="revoke" method="post" action="/teams/{E(name)}/members/remove"><input type="hidden" name="userId" value="{E(uid)}"><button>remove</button></form>"""
                : "";
            b.Append($"""<li>{E(db.GetUser(uid)?.Login ?? "?")}{(uid == t.OwnerId ? """ <span class="role role-owner">owner</span>""" : "")}{remove}</li>""");
        }
        b.Append("</ul>");

        if (isOwner)
        {
            b.Append($"""
                <form class="find" method="post" action="/teams/{E(name)}/members">
                  <input name="login" placeholder="username — they must have signed in once">
                  <button>Add member</button>
                </form>
                <form class="settings" method="post" action="/teams/{E(name)}/delete" onsubmit="return confirm('Delete team {E(name)}? Its grants are removed.')">
                  <button>Delete team</button>
                </form>
                """);
        }
        return Page(name, b.ToString(), chip);
    }

    private static IResult Home(HttpContext ctx, RepoStore store, HubDb db, Auth.Config cfg)
    {
        string chip = Auth.HeaderRight(ctx, cfg);
        string? meId = Auth.Current(ctx)?.Id;
        var repos = store.List().Where(r => Auth.CanRead(cfg, db, r.Name, meId, admin: false)).ToList();

        var b = new StringBuilder("<h1>Worlds</h1>");
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

    private static IResult Repo(HttpContext ctx, RepoStore store, HubDb db, Auth.Config cfg, string name)
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
            if (me is not null && me.Id == m.OwnerId)
                b.Append($"""
                    <form class="settings" method="post" action="/r/{E(name)}/settings">
                      <input type="hidden" name="private" value="{(m.Private ? "off" : "on")}">
                      <button>Make {(m.Private ? "public" : "private")}</button>
                    </form>
                    """);
        }
        RenderCollaborators(b, ctx, db, name, m, me);
        string baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        b.Append($"""<p class="clone">Clone: <code>mcadiff clone {E(baseUrl)}/r/{E(name)} {E(name)}.mcagit</code></p>""");

        b.Append("<h2>Branches</h2><ul class=\"branches\">");
        foreach (string br in repo.Branches())
            if (repo.ReadBranch(br) is { } tip)
                b.Append($"""<li><a href="/r/{E(name)}/commit/{tip}">{E(br)}</a> <span class="hash">{tip[..10]}</span></li>""");
        b.Append("</ul>");

        if (repo.HeadCommit() is { } head)
        {
            b.Append("<h2>Backups</h2><ol class=\"timeline\">");
            string? cur = head;
            for (int i = 0; cur is not null && i < 50; i++)
            {
                CommitObject c = repo.ReadCommit(cur);
                string? par = repo.ParentsOf(cur) is [string pp, ..] ? pp : null;
                string actions = $"""<a href="/r/{E(name)}/world/{cur}">explore</a>{(par is null ? "" : $""" · <a href="/r/{E(name)}/compare/{par}/{cur}">what changed</a>""")}""";
                b.Append($"""<li><a href="/r/{E(name)}/commit/{cur}">{cur[..10]}</a> <span class="cmsg">{E(Oneline(c.Message))}</span><span class="meta">{E(c.Author)} · {When(c.CommitTime ?? c.Time)}{(c.Parents.Count > 1 ? " · merge" : "")}{(c.Signature is not null ? " · signed" : "")}</span><span class="actions">{actions}</span></li>""");
                cur = par;
            }
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

        RenderGrief(b, g);
        b.Append("<h2>Changes</h2>");
        if (!diff.HasDifferences) b.Append("""<p class="empty">No changes from the previous backup.</p>""");
        RenderDiff(b, diff);
        return Page($"Backup {commit[..10]}", b.ToString(), chip);
    }

    private static IResult Compare(HttpContext ctx, RepoStore store, HubDb db, Auth.Config cfg, string name, string a, string bRef)
    {
        string chip = Auth.HeaderRight(ctx, cfg);
        if (!store.Exists(name) || !CanSee(ctx, db, cfg, name)) return NotFound("world", chip);
        Repository repo = store.Open(name);
        string ca, cb;
        try { ca = repo.ResolveRef(a); cb = repo.ResolveRef(bRef); } catch { return NotFound("backup", chip); }

        WorldDiff diff = RefDiff(repo, ca, cb, expand: true);
        var sb = new StringBuilder();
        sb.Append($"""<p class="back"><a href="/r/{E(name)}">← {E(name)}</a></p>""");
        sb.Append($"<h1>{ca[..10]} → {cb[..10]}</h1>");
        RenderGrief(sb, GriefReport.Analyze(diff));
        sb.Append("<h2>Changes</h2>");
        if (!diff.HasDifferences) sb.Append("""<p class="empty">No differences between these backups.</p>""");
        RenderDiff(sb, diff);
        return Page($"Compare {ca[..10]}…{cb[..10]}", sb.ToString(), chip);
    }

    private static IResult World(HttpContext ctx, RepoStore store, HubDb db, Auth.Config cfg, WorldCache cache,
        string name, string refName, string? findKind, string? q)
    {
        string chip = Auth.HeaderRight(ctx, cfg);
        if (!store.Exists(name) || !CanSee(ctx, db, cfg, name)) return NotFound("world", chip);
        Repository repo = store.Open(name);
        string commit;
        try { commit = repo.ResolveRef(refName); } catch { return NotFound("backup", chip); }

        string worldDir = cache.Materialize(name, repo, commit); // immutable cache; first view materializes
        var wq = new WorldQuery(worldDir);

        var sb = new StringBuilder();
        sb.Append($"""<p class="back"><a href="/r/{E(name)}/commit/{commit}">← backup {commit[..10]}</a></p>""");
        sb.Append($"<h1>World at {commit[..10]}</h1>");

        var players = wq.Players().ToList();
        sb.Append("<h2>Players</h2>");
        if (players.Count == 0) sb.Append("""<p class="empty">No player data.</p>""");
        else
        {
            sb.Append("<ul class=\"branches\">");
            foreach (PlayerHit p in players)
                sb.Append($"""<li>{E(p.Source)} <span class="meta">({p.X:0.#},{p.Y:0.#},{p.Z:0.#}) [{E(p.Dimension)}]{(p.Health >= 0 ? $" · {p.Health:0.#} hp" : "")}</span></li>""");
            sb.Append("</ul>");
        }

        sb.Append($"""
            <h2>Find</h2>
            <form class="find" method="get" action="/r/{E(name)}/world/{E(refName)}">
              <select name="find">{Opt("entity", findKind)}{Opt("block-entity", findKind)}{Opt("sign", findKind)}</select>
              <input name="q" placeholder="id or text — chest, zombie, spawn…" value="{E(q)}">
              <button>Search</button>
            </form>
            """);
        if (findKind is { Length: > 0 } && q is { Length: > 0 })
        {
            sb.Append("<ul class=\"changes\">");
            int n = 0;
            if (findKind == "entity") foreach (EntityHit h in wq.Entities(q).Take(300)) { sb.Append($"""<li><code>{E(h.Id)}</code> ({h.X:0.#},{h.Y:0.#},{h.Z:0.#}){(h.CustomName is null ? "" : $" \"{E(h.CustomName)}\"")}</li>"""); n++; }
            else if (findKind == "block-entity") foreach (BlockEntityHit h in wq.BlockEntities(q).Take(300)) { sb.Append($"""<li><code>{E(h.Id)}</code> ({h.X},{h.Y},{h.Z}){(h.ItemCount > 0 ? $" · {h.ItemCount} items" : "")}</li>"""); n++; }
            else if (findKind == "sign") foreach (SignHit h in wq.Signs(q).Take(300)) { sb.Append($"""<li>({h.X},{h.Y},{h.Z}) "{E(string.Join(" / ", h.Lines))}"</li>"""); n++; }
            if (n == 0) sb.Append("""<li class="empty">No matches.</li>""");
            sb.Append("</ul>");
        }
        return Page($"World {commit[..10]}", sb.ToString(), chip);
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
                b.Append($"""<li><code>{E(t.Prefix)}…</code> <span class="cmsg">{E(t.Label)}</span><span class="meta">created {When(t.CreatedAt)}{(t.LastUsedAt is null ? " · never used" : $" · last used {When(t.LastUsedAt)}")}</span><form class="revoke" method="post" action="/account/tokens/revoke"><input type="hidden" name="prefix" value="{E(t.Prefix)}"><button>revoke</button></form></li>""");
            b.Append("</ul>");
        }
        b.Append("""
            <form class="find" method="post" action="/account/tokens">
              <input name="label" placeholder="label — laptop, backup-server…">
              <button>Create token</button>
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
                b.Append($"""<li><a href="/r/{E(c.Repo)}">{E(c.Repo)}</a> <span class="role role-{E(c.Role)}">{E(c.Role)}</span><span class="meta">owned by {E(db.GetUser(db.GetRepo(c.Repo)!.OwnerId)?.Login ?? "?")}</span></li>""");
            b.Append("</ul>");
        }
        return Page(me.Login, b.ToString(), chip);
    }

    private static void RenderCollaborators(StringBuilder b, HttpContext ctx, HubDb db, string name, HubRepoMeta? m, HubUser? me)
    {
        if (m is null) return;
        bool isOwner = me is not null && me.Id == m.OwnerId;
        var collabs = db.CollabsOf(name);
        var teamGrants = db.TeamGrantsOf(name);
        if (collabs.Count == 0 && teamGrants.Count == 0 && !isOwner) return; // nothing to show, no controls to offer

        b.Append("<h2>Collaborators</h2>");
        if (isOwner && ctx.Request.Query["err"] == "nouser")
            b.Append("""<p class="empty">That user hasn't signed in to the hub yet — they need to sign in once before you can add them.</p>""");

        b.Append("<ul class=\"repos\">");
        b.Append($"""<li>{E(db.GetUser(m.OwnerId)?.Login ?? "?")} <span class="role role-owner">owner</span></li>""");
        foreach (Collab c in collabs)
        {
            string remove = isOwner
                ? $"""<form class="revoke" method="post" action="/r/{E(name)}/collaborators/remove"><input type="hidden" name="userId" value="{E(c.UserId)}"><button>remove</button></form>"""
                : "";
            b.Append($"""<li>{E(db.GetUser(c.UserId)?.Login ?? "?")} <span class="role role-{E(c.Role)}">{E(c.Role)}</span>{remove}</li>""");
        }
        b.Append("</ul>");

        if (isOwner)
            b.Append($"""
                <form class="find" method="post" action="/r/{E(name)}/collaborators">
                  <input name="login" placeholder="username — they must have signed in once">
                  <select name="role">{Opt("read", "read")}{Opt("write", null)}</select>
                  <button>Add collaborator</button>
                </form>
                """);

        if (teamGrants.Count == 0 && !isOwner) return;
        b.Append("<h2>Teams with access</h2>");
        if (isOwner && ctx.Request.Query["err"] == "noteam")
            b.Append("""<p class="empty">No team by that name.</p>""");
        if (teamGrants.Count > 0)
        {
            b.Append("<ul class=\"repos\">");
            foreach (TeamGrant g in teamGrants)
            {
                string remove = isOwner
                    ? $"""<form class="revoke" method="post" action="/r/{E(name)}/teams/remove"><input type="hidden" name="team" value="{E(g.TeamName)}"><button>remove</button></form>"""
                    : "";
                string teamName = db.GetTeam(g.TeamName) is not null ? $"""<a href="/teams/{E(g.TeamName)}">{E(g.TeamName)}</a>""" : E(g.TeamName);
                b.Append($"""<li>👥 {teamName} <span class="role role-{E(g.Role)}">{E(g.Role)}</span>{remove}</li>""");
            }
            b.Append("</ul>");
        }
        if (isOwner)
            b.Append($"""
                <form class="find" method="post" action="/r/{E(name)}/teams">
                  <input name="team" placeholder="team name">
                  <select name="role">{Opt("read", "read")}{Opt("write", null)}</select>
                  <button>Grant team</button>
                </form>
                """);
    }

    private static bool CanSee(HttpContext ctx, HubDb db, Auth.Config cfg, string repo) =>
        Auth.CanRead(cfg, db, repo, Auth.Current(ctx)?.Id, admin: false);

    private static string Opt(string v, string? sel) => $"""<option value="{v}"{(v == sel ? " selected" : "")}>{v}</option>""";

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

    private static void RenderDiff(StringBuilder b, WorldDiff diff)
    {
        foreach (FileDiff f in diff.Files.Take(MaxFiles))
        {
            b.Append($"""<div class="file"><div class="fh"><span class="st st-{f.Status.ToString().ToLowerInvariant()}">{f.Status}</span> {E(f.RelativePath)}{(f.ItemCount is { } n ? $" <span class=\"meta\">({n} chunks)</span>" : "")}</div>""");
            if (f.Error is { } err) b.Append($"""<div class="err">{E(err)}</div>""");
            foreach (ChunkDiff ch in f.Chunks.Take(MaxChunks))
            {
                b.Append($"""<div class="chunk">chunk ({ch.Pos.X}, {ch.Pos.Z})</div><ul class="changes">""");
                foreach (NbtChange c in ch.Changes.Take(MaxChanges)) b.Append(Change(c));
                if (ch.Changes.Count > MaxChanges) b.Append($"<li class=\"more\">… {ch.Changes.Count - MaxChanges} more</li>");
                b.Append("</ul>");
            }
            if (f.Chunks.Count > MaxChunks) b.Append($"<div class=\"more\">… {f.Chunks.Count - MaxChunks} more chunks</div>");
            if (f.Changes.Count > 0)
            {
                b.Append("<ul class=\"changes\">");
                foreach (NbtChange c in f.Changes.Take(MaxChanges)) b.Append(Change(c));
                if (f.Changes.Count > MaxChanges) b.Append($"<li class=\"more\">… {f.Changes.Count - MaxChanges} more</li>");
                b.Append("</ul>");
            }
            b.Append("</div>");
        }
        if (diff.Files.Count > MaxFiles) b.Append($"<div class=\"more\">… {diff.Files.Count - MaxFiles} more files</div>");
    }

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
    private static string When(string iso) => DateTimeOffset.TryParse(iso, out var d) ? d.ToString("yyyy-MM-dd HH:mm") : iso;
}
