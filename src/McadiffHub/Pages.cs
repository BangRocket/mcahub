using System.Text;
using McaDiff.Diff;
using McaDiff.Query;
using McaDiff.Repo;
using static McadiffHub.Html;

namespace McadiffHub;

/// <summary>Server-rendered pages: the repo list, a repo's backup timeline, and a backup's view —
/// the semantic diff plus a "what happened here" grief summary (the bit a generic git host can't do).</summary>
public static class Pages
{
    public static void MapPages(WebApplication app, RepoStore store)
    {
        app.MapGet("/", () => Home(store));
        app.MapGet("/r/{repo}", (string repo) => Repo(store, repo));
        app.MapGet("/r/{repo}/commit/{hash}", (string repo, string hash) => Commit(store, repo, hash));
    }

    private static IResult Home(RepoStore store)
    {
        var repos = store.List().ToList();
        var b = new StringBuilder("<h1>Worlds</h1>");
        if (repos.Count == 0)
            b.Append("""<p class="empty">No worlds yet. Push one: <code>mcadiff push http://&lt;this-host&gt;/r/&lt;name&gt; main</code> (the hub auto-creates it).</p>""");
        else
        {
            b.Append("<ul class=\"repos\">");
            foreach (RepoSummary r in repos)
                b.Append($"""<li><a href="/r/{E(r.Name)}">{E(r.Name)}</a><span class="meta">{r.Branches} branch(es){(r.LastWhen is null ? "" : $" · last backup {When(r.LastWhen)}")}</span>{(r.LastMessage is null ? "" : $"<span class=\"msg\">{E(Oneline(r.LastMessage))}</span>")}</li>""");
            b.Append("</ul>");
        }
        return Page("Worlds", b.ToString());
    }

    private static IResult Repo(RepoStore store, string name)
    {
        if (!store.Exists(name)) return NotFound("world");
        Repository repo = store.Open(name);
        var b = new StringBuilder($"<h1>{E(name)}</h1>");
        b.Append($"""<p class="clone">Clone: <code>mcadiff clone {E(BaseHint())}/r/{E(name)} {E(name)}.mcagit</code></p>""");

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
                b.Append($"""<li><a href="/r/{E(name)}/commit/{cur}">{cur[..10]}</a> <span class="cmsg">{E(Oneline(c.Message))}</span><span class="meta">{E(c.Author)} · {When(c.CommitTime ?? c.Time)}{(c.Parents.Count > 1 ? " · merge" : "")}{(c.Signature is not null ? " · signed" : "")}</span></li>""");
                cur = repo.ParentsOf(cur) is [string p, ..] ? p : null;
            }
            b.Append("</ol>");
        }
        else b.Append("""<p class="empty">No backups yet.</p>""");
        return Page(name, b.ToString());
    }

    private static IResult Commit(RepoStore store, string name, string hash)
    {
        if (!store.Exists(name)) return NotFound("world");
        Repository repo = store.Open(name);
        string commit;
        try { commit = repo.ResolveRef(hash); } catch { return NotFound("backup"); }
        CommitObject c = repo.ReadCommit(commit);

        WorldDiff diff = CommitDiff(repo, commit, expand: true);
        GriefSummary g = GriefReport.Analyze(diff);

        var b = new StringBuilder();
        b.Append($"""<p class="back"><a href="/r/{E(name)}">← {E(name)}</a></p>""");
        b.Append($"<h1>Backup {commit[..10]}</h1>");
        b.Append($"""<p class="cmeta">{E(c.Message)}<br><span class="meta">{E(c.Author)} · {When(c.CommitTime ?? c.Time)}{(c.Signature is not null ? " · ✓ signed" : "")}</span></p>""");

        // "What happened here" — the grief summary.
        if (g.Destroyed + g.Built + g.Replaced > 0)
        {
            b.Append("<div class=\"grief\">");
            b.Append($"""<span class="g-d">{g.Destroyed:N0} destroyed</span> <span class="g-b">{g.Built:N0} placed</span> <span class="g-r">{g.Replaced:N0} replaced</span>""");
            if (g.Min is { } mn && g.Max is { } mx && g.Center is { } ce)
                b.Append($"""<div class="g-where">destruction spans ({mn.X},{mn.Y},{mn.Z})–({mx.X},{mx.Y},{mx.Z}), centered ~({ce.X},{ce.Y},{ce.Z})</div>""");
            if (g.TopDestroyed.Count > 0)
                b.Append("<div class=\"g-top\">most destroyed: " + string.Join(", ", g.TopDestroyed.Select(t => $"{E(Short(t.Block))} ×{t.Count}")) + "</div>");
            b.Append("</div>");
        }

        // The semantic diff.
        b.Append("<h2>Changes</h2>");
        if (!diff.HasDifferences) b.Append("""<p class="empty">No changes from the previous backup.</p>""");
        RenderDiff(b, diff);
        return Page($"Backup {commit[..10]}", b.ToString());
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
        CommitObject c = repo.ReadCommit(commit);
        Manifest mNew = repo.ReadManifest(c.Tree);
        string? parent = c.Parents.Count > 0 ? c.Parents[0] : null;
        Manifest mOld = parent is not null ? repo.ReadManifest(repo.ReadCommit(parent).Tree) : new Manifest();
        return RepoDiffer.Diff(
            parent is null ? "(root)" : parent[..10], mOld, new RepoDiffer.CommitSource(repo, mOld),
            commit[..10], mNew, new RepoDiffer.CommitSource(repo, mNew), new DiffRunOptions(ExpandArrays: expand));
    }

    private static string Oneline(string msg) { int nl = msg.IndexOf('\n'); return nl < 0 ? msg : msg[..nl]; }
    private static string Short(string id) => id.StartsWith("minecraft:") ? id["minecraft:".Length..] : id;
    private static string When(string iso) => DateTimeOffset.TryParse(iso, out var d) ? d.ToString("yyyy-MM-dd HH:mm") : iso;
    private static string BaseHint() => "http://localhost:5080";
}
