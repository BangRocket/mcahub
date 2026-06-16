# Repo-page parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give a world's landing page GitHub-like repo-page anchors — an editable description and a rendered README, the description on home cards, a copy-to-clipboard clone button, and owner avatars.

**Architecture:** Description + README are hub-stored metadata (new nullable fields on `HubRepoMeta` in `hub.json`), edited through a new web form gated by `CanManageSettings`, and rendered on the landing page. README markdown is rendered by Markdig (the project's first NuGet dependency) through a single sanitizing helper (`Markdown.cs`) that disables raw HTML and allowlists URL schemes — the one place the hub emits HTML not routed through `Html.E`.

**Tech Stack:** ASP.NET Core (.NET 10), server-rendered HTML (no SPA), Markdig, xUnit + `WebApplicationFactory<Program>`.

---

## File Structure

**Create:**
- `src/McaHub/Markdown.cs` — sanitizing markdown→HTML renderer (`Markdown.Render`). One responsibility: turn untrusted markdown into safe HTML.
- `tests/McaHub.Tests/MarkdownRenderTests.cs` — unit tests for the renderer's sanitization + rendering.
- `tests/McaHub.Tests/RepoAboutTests.cs` — data-layer + web tests for the About/README feature.

**Modify:**
- `src/McaHub/McaHub.csproj` — add the Markdig `PackageReference`.
- `src/McaHub/HubDb.cs` — two nullable fields on `HubRepoMeta`; a `SetRepoAbout` mutator.
- `src/McaHub/Pages.cs` — edit-page route + `POST /about` handler; validation constants + `CleanDescription`; an `Avatar` helper; landing render (description, README, OG, edit link); home-card render (description + avatar); clone copy button.
- `src/McaHub/wwwroot/app.js` — delegated copy-to-clipboard handler.
- `src/McaHub/wwwroot/style.css` — `.readme`, `button.copy`, card `.desc`, card `.avatar`, edit-form styles.
- `tests/McaHub.Tests/Accounts.cs` — a `SetAboutAsync` test helper.

---

### Task 1: Add the Markdig dependency

**Files:**
- Modify: `src/McaHub/McaHub.csproj`

- [ ] **Step 1: Add the package**

Run from the repo root:

```bash
dotnet add src/McaHub package Markdig
```

This resolves the current stable Markdig and writes a `<PackageReference Include="Markdig" Version="..."/>` into `McaHub.csproj`.

- [ ] **Step 2: Verify it restores and builds**

Run: `dotnet build src/McaHub`
Expected: `Build succeeded`. The `<ItemGroup>` in `McaHub.csproj` now contains both the existing `InternalsVisibleTo` and the new `PackageReference` (they may be in the same or separate `ItemGroup`s — either is fine).

- [ ] **Step 3: Commit**

```bash
git add src/McaHub/McaHub.csproj
git commit -m "build: add Markdig (first NuGet dependency) for README rendering"
```

---

### Task 2: The sanitizing markdown renderer

**Files:**
- Create: `src/McaHub/Markdown.cs`
- Test: `tests/McaHub.Tests/MarkdownRenderTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/McaHub.Tests/MarkdownRenderTests.cs`:

```csharp
namespace McaHub.Tests;

/// <summary>The README renderer is the one place the hub emits HTML not escaped by Html.E, so these
/// tests pin its sanitization: raw HTML is neutralized and dangerous URL schemes are dropped.</summary>
public class MarkdownRenderTests
{
    [Fact]
    public void Renders_basic_markdown_to_html()
    {
        string html = Markdown.Render("# Title\n\nSome **bold** text.");
        Assert.Contains("<h1", html);
        Assert.Contains("Title", html);
        Assert.Contains("<strong>bold</strong>", html);
    }

    [Fact]
    public void Neutralizes_raw_html_tags()
    {
        // DisableHtml turns raw tags into escaped text, so no live <script>/<img> element appears.
        string html = Markdown.Render("Hi <script>alert(1)</script> <img src=x onerror=alert(1)>");
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void Drops_javascript_scheme_links()
    {
        string html = Markdown.Render("[click](javascript:alert(1))");
        Assert.DoesNotContain("javascript:", html);
    }

    [Fact]
    public void Drops_data_uri_images()
    {
        string html = Markdown.Render("![x](data:text/html;base64,PHN2Zz4=)");
        Assert.DoesNotContain("data:text/html", html);
    }

    [Fact]
    public void Keeps_http_links_and_marks_them_nofollow()
    {
        string html = Markdown.Render("[site](https://example.com/page)");
        Assert.Contains("https://example.com/page", html);
        Assert.Contains("nofollow", html);
    }

    [Fact]
    public void Renders_pipe_tables()
    {
        string html = Markdown.Render("| a | b |\n|---|---|\n| 1 | 2 |");
        Assert.Contains("<table", html);
    }

    [Fact]
    public void Empty_input_renders_empty_string()
    {
        Assert.Equal("", Markdown.Render(null));
        Assert.Equal("", Markdown.Render("   "));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/McaHub.Tests --filter MarkdownRenderTests`
Expected: FAIL — compile error, `Markdown` type does not exist.

- [ ] **Step 3: Implement the renderer**

Create `src/McaHub/Markdown.cs`:

```csharp
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace McaHub;

/// <summary>
/// Renders a repo's user-authored README (Markdown) to <b>sanitized</b> HTML. This is the only place the
/// hub emits HTML that is not routed through <see cref="Html.E"/>, so it must stay airtight:
/// <list type="bullet">
///   <item><c>DisableHtml()</c> turns any raw inline/block HTML (e.g. <c>&lt;script&gt;</c>,
///   <c>&lt;img onerror&gt;</c>) into escaped text rather than live markup.</item>
///   <item>Every link/image URL is checked against a scheme allowlist, so <c>javascript:</c> / <c>data:</c>
///   URIs that survive DisableHtml are blanked.</item>
/// </list>
/// The strict CSP (<c>script-src 'self'</c>, no <c>unsafe-inline</c>) is the backstop.
/// </summary>
public static class Markdown
{
    // Conservative pipeline: CommonMark + autolinks + pipe tables, raw HTML disabled. Built once.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UsePipeTables()
        .Build();

    public static string Render(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        // Markdig's static API is also named Markdown — fully-qualify to avoid resolving to this class.
        MarkdownDocument doc = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (LinkInline link in doc.Descendants<LinkInline>())
        {
            if (!IsSafeUrl(link.Url)) link.Url = "";                  // blank javascript:/data:/unknown schemes
            else if (!link.IsImage) link.GetAttributes().AddProperty("rel", "nofollow ugc noopener");
        }
        return doc.ToHtml(Pipeline);
    }

    // Allow http(s), mailto, and relative/anchor URLs; reject everything else (javascript:, data:, vbscript:, …).
    private static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string u = url.TrimStart();
        int colon = u.IndexOf(':');
        if (colon < 0) return true;                                   // no scheme ⇒ relative ⇒ safe
        int slash = u.IndexOf('/'), hash = u.IndexOf('#'), q = u.IndexOf('?');
        // A separator before the first colon means the colon is in a path segment, not a scheme.
        if ((slash >= 0 && slash < colon) || (hash >= 0 && hash < colon) || (q >= 0 && q < colon)) return true;
        return u[..colon].ToLowerInvariant() is "http" or "https" or "mailto";
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/McaHub.Tests --filter MarkdownRenderTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/McaHub/Markdown.cs tests/McaHub.Tests/MarkdownRenderTests.cs
git commit -m "feat: sanitizing Markdown renderer (DisableHtml + URL scheme allowlist)"
```

---

### Task 3: HubDb — About fields + SetRepoAbout

**Files:**
- Modify: `src/McaHub/HubDb.cs:435` (the `HubRepoMeta` record) and add a mutator near `SetPrivate` (`src/McaHub/HubDb.cs:227-231`)
- Test: `tests/McaHub.Tests/RepoAboutTests.cs`

- [ ] **Step 1: Write the failing data-layer tests**

Create `tests/McaHub.Tests/RepoAboutTests.cs`:

```csharp
namespace McaHub.Tests;

/// <summary>The About/README feature at the data layer: the new HubRepoMeta fields round-trip,
/// survive a reload, and a pre-existing hub.json without them still loads (additive, no schema bump).</summary>
public class RepoAboutTests
{
    [Fact]
    public void SetRepoAbout_round_trips_and_survives_reload()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "hub.json");
        var db = new HubDb(path);
        db.EnsureRepo("world", "u1", isPrivate: false);
        db.SetRepoAbout("world", "My base", "# Hello\nworld");

        Assert.Equal("My base", db.GetRepo("world")!.Description);
        Assert.Equal("# Hello\nworld", db.GetRepo("world")!.Readme);

        var reopened = new HubDb(path);                  // a fresh instance reads it back from disk
        Assert.Equal("My base", reopened.GetRepo("world")!.Description);
        Assert.Equal("# Hello\nworld", reopened.GetRepo("world")!.Readme);
    }

    [Fact]
    public void Old_hub_json_without_about_fields_still_loads()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "hub.json");
        File.WriteAllText(path, """
            { "SchemaVersion": 1, "Repos": [
              { "Name": "w", "OwnerId": "u1", "Private": false, "CreatedAt": "2026-01-01T00:00:00Z" } ] }
            """);
        var db = new HubDb(path);
        Assert.NotNull(db.GetRepo("w"));
        Assert.Null(db.GetRepo("w")!.Description);
        Assert.Null(db.GetRepo("w")!.Readme);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/McaHub.Tests --filter RepoAboutTests`
Expected: FAIL — `HubRepoMeta` has no `Description`/`Readme`; `SetRepoAbout` is undefined.

- [ ] **Step 3: Add the fields to the record**

In `src/McaHub/HubDb.cs`, change the `HubRepoMeta` declaration (line 435) from:

```csharp
public sealed record HubRepoMeta(string Name, string OwnerId, bool Private, string CreatedAt);
```

to:

```csharp
public sealed record HubRepoMeta(string Name, string OwnerId, bool Private, string CreatedAt,
    string? Description = null, string? Readme = null); // about/README, web-edited (#repo-page-parity)
```

The trailing optional params keep this backward-compatible: an old `hub.json` deserializes them as null via the constructor defaults, and `with { … }` preserves them. No `CurrentSchema` bump.

- [ ] **Step 4: Add the mutator**

In `src/McaHub/HubDb.cs`, immediately after the `SetPrivate` method (ends at line 231), add:

```csharp
    /// <summary>Set a repo's web-edited About description + README (either may be null to clear). Caller
    /// validates/caps the values; the store trusts them.</summary>
    public void SetRepoAbout(string name, string? description, string? readme) => Mutate(() =>
    {
        int i = _db.Repos.FindIndex(r => r.Name == name);
        if (i >= 0) _db.Repos[i] = _db.Repos[i] with { Description = description, Readme = readme };
    });
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/McaHub.Tests --filter RepoAboutTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/McaHub/HubDb.cs tests/McaHub.Tests/RepoAboutTests.cs
git commit -m "feat: HubRepoMeta gains Description/Readme + SetRepoAbout mutator"
```

---

### Task 4: Edit page + POST /about (routes, authz, validation)

**Files:**
- Modify: `tests/McaHub.Tests/Accounts.cs` (add `SetAboutAsync`)
- Modify: `src/McaHub/Pages.cs` — constants + `CleanDescription` near the top of the class; `EditDetails` page method; route registrations inside the `if (cfg.Accounts)` block.
- Test: `tests/McaHub.Tests/RepoAboutTests.cs` (add web tests)

- [ ] **Step 1: Add the test helper**

In `tests/McaHub.Tests/Accounts.cs`, after `SetPrivateAsync` (ends at line 108), add:

```csharp
    /// <summary>Set a repo's About description + README as a manager (loads CSRF from the edit page).
    /// Returns the raw response so a test can assert the redirect target (e.g. rejection on oversize).</summary>
    public static async Task<HttpResponseMessage> SetAboutAsync(HttpClient manager, string repo, string description, string readme)
    {
        string csrf = Csrf(await GetStringAsync(manager, $"/r/{repo}/edit"));
        return await manager.PostAsync($"/r/{repo}/about",
            Form(("__RequestVerificationToken", csrf), ("description", description), ("readme", readme)));
    }
```

- [ ] **Step 2: Write the failing web tests**

In `tests/McaHub.Tests/RepoAboutTests.cs`, add these to the class (and add the two `using`s at the top of the file):

```csharp
// add at the top of RepoAboutTests.cs:
using System.Net;
```

```csharp
    [Fact]
    public async Task Manager_can_set_about_and_it_renders_on_the_landing_page()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient owner = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(owner);
        await Accounts.CreateRepoAsync(f, token, "base");

        HttpResponseMessage saved = await Accounts.SetAboutAsync(owner, "base",
            "Our survival world", "# Welcome\n\nDig **carefully**.");
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal("/r/base", saved.Headers.Location!.ToString());

        string page = await (await owner.GetAsync("/r/base")).Content.ReadAsStringAsync();
        Assert.Contains("Our survival world", page);          // description
        Assert.Contains("<h1", page);                          // README heading rendered
        Assert.Contains("<strong>carefully</strong>", page);   // README markdown rendered
    }

    [Fact]
    public async Task Non_manager_cannot_reach_the_edit_page()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient owner = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(owner);
        await Accounts.CreateRepoAsync(f, token, "base");
        await Accounts.SetPrivateAsync(owner, "base", false);  // public, so a stranger can SEE it

        HttpClient bob = await Accounts.SignInAsync(f, "bob");
        HttpResponseMessage edit = await bob.GetAsync("/r/base/edit");
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode); // edit surface hidden from non-managers
    }

    [Fact]
    public async Task Oversize_readme_is_rejected_not_saved()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient owner = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(owner);
        await Accounts.CreateRepoAsync(f, token, "base");

        string huge = new string('a', 40 * 1024);             // > 32 KB cap
        HttpResponseMessage resp = await Accounts.SetAboutAsync(owner, "base", "x", huge);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/r/base/edit", resp.Headers.Location!.ToString()); // bounced back to the editor

        string page = await (await owner.GetAsync("/r/base")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(huge, page);                     // not persisted
    }

    [Fact]
    public async Task Description_is_capped_at_200_chars()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient owner = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(owner);
        await Accounts.CreateRepoAsync(f, token, "base");

        await Accounts.SetAboutAsync(owner, "base", new string('d', 300), "");
        string page = await (await owner.GetAsync("/r/base")).Content.ReadAsStringAsync();
        Assert.Contains(new string('d', 200), page);
        Assert.DoesNotContain(new string('d', 201), page);
    }
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/McaHub.Tests --filter RepoAboutTests`
Expected: FAIL — `/r/base/edit` and `/r/base/about` are 404 (routes not registered yet).

- [ ] **Step 4: Add constants + helpers near the top of the `Pages` class**

In `src/McaHub/Pages.cs`, just after `public const string RenderTimeoutPolicy = "render";` (line 13), add:

```csharp
    /// <summary>About/README limits — README is capped because hub.json is rewritten in full on every
    /// mutation across instances, so a large blob slows every write.</summary>
    private const int MaxDescriptionChars = 200;
    private const int MaxReadmeBytes = 32 * 1024;

    /// <summary>Single-line, trimmed, length-capped description for storage.</summary>
    private static string CleanDescription(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > MaxDescriptionChars ? s[..MaxDescriptionChars] : s;
    }

    /// <summary>A small round owner avatar, or empty when the user has none. referrerpolicy trims the
    /// referer leaked to the third-party avatar host (img-src https: already permits it).</summary>
    private static string Avatar(HubUser? u) =>
        u is { Avatar.Length: > 0 } ? $"""<img class="avatar sm" src="{E(u.Avatar)}" alt="" width="20" height="20" loading="lazy" referrerpolicy="no-referrer"> """ : "";
```

- [ ] **Step 5: Add the `EditDetails` page method**

In `src/McaHub/Pages.cs`, add this method next to the other page methods (e.g. immediately before `private static IResult Repo(` at line 510):

```csharp
    private static IResult EditDetails(HttpContext ctx, RepoStore store, HubDb db, Auth.Config cfg, string name)
    {
        string chip = Auth.HeaderRight(ctx, cfg);
        if (!store.Exists(name) || !CanSee(ctx, db, cfg, name)) return NotFound("world", chip);
        HubUser? me = Auth.Current(ctx);
        if (me is null || !Auth.CanManageSettings(db, name, me.Id)) return NotFound("world", chip); // hide the edit surface (no 403)
        HubRepoMeta? m = db.GetRepo(name);

        var b = new StringBuilder();
        b.Append($"""<p class="back"><a href="/r/{E(name)}">← {E(name)}</a></p>""");
        b.Append($"<h1>Edit {E(name)}</h1>");
        if (ctx.Request.Query["err"] == "toolong")
            b.Append($"""<p class="empty">README is too large (max {MaxReadmeBytes / 1024} KB). Nothing was saved.</p>""");
        b.Append($"""
            <form class="find edit-details" method="post" action="/r/{E(name)}/about">
              {Auth.CsrfField(ctx)}
              <label class="fld">Description
                <input name="description" maxlength="{MaxDescriptionChars}" value="{E(m?.Description)}" placeholder="A short summary of this world"></label>
              <label class="fld">README (Markdown)
                <textarea name="readme" rows="16" placeholder="# My world&#10;&#10;Tell visitors about it…">{E(m?.Readme)}</textarea></label>
              <button>Save details</button>
            </form>
            """);
        return Page($"Edit {name}", b.ToString(), chip);
    }
```

- [ ] **Step 6: Register the routes**

In `src/McaHub/Pages.cs`, inside the accounts block (after the `app.MapPost("/r/{repo}/settings", …)` handler that ends at line 101), add:

```csharp
        app.MapGet("/r/{repo}/edit", (string repo, HttpContext ctx) => EditDetails(ctx, store, db, cfg, repo));
        app.MapPost("/r/{repo}/about", async (string repo, HttpContext ctx) =>
        {
            if (!await Auth.CsrfOk(ctx)) return BadCsrf();
            if (Auth.Current(ctx) is { } me && Auth.CanManageSettings(db, repo, me.Id))
            {
                IFormCollection form = await ctx.Request.ReadFormAsync();
                string readme = form["readme"].ToString();
                if (System.Text.Encoding.UTF8.GetByteCount(readme) > MaxReadmeBytes)
                    return Results.Redirect($"/r/{repo}/edit?err=toolong"); // reject, don't truncate
                string desc = CleanDescription(form["description"].ToString());
                db.SetRepoAbout(repo, desc.Length == 0 ? null : desc, readme.Length == 0 ? null : readme);
                Log(ctx, audit, "about.edit", repo, desc.Length == 0 ? "(cleared description)" : "updated");
            }
            return Results.Redirect($"/r/{repo}");
        });
```

- [ ] **Step 7: Run to verify the tests pass**

Run: `dotnet test tests/McaHub.Tests --filter RepoAboutTests`
Expected: the data-layer tests still pass and `Non_manager_cannot_reach_the_edit_page`, `Oversize_readme_is_rejected_not_saved`, and `Description_is_capped_at_200_chars` pass. `Manager_can_set_about_and_it_renders_on_the_landing_page` will still FAIL on the render assertions (`Our survival world` / `<h1` not on the landing page yet) — that landing render is Task 5. The route/authz/limit assertions in it should pass; the render assertions fail.

To keep this task green, temporarily run only the non-render tests:
Run: `dotnet test tests/McaHub.Tests --filter "Non_manager_cannot_reach_the_edit_page|Oversize_readme_is_rejected_not_saved|Description_is_capped_at_200_chars"`
Expected: PASS (3 tests).

- [ ] **Step 8: Commit**

```bash
git add src/McaHub/Pages.cs tests/McaHub.Tests/Accounts.cs tests/McaHub.Tests/RepoAboutTests.cs
git commit -m "feat: edit page + POST /about with authz, CSRF, and size caps"
```

---

### Task 5: Landing page render — description, README, OG, edit link

**Files:**
- Modify: `src/McaHub/Pages.cs` — the `Repo` method (lines 510-582)

- [ ] **Step 1: Confirm the failing render test**

Run: `dotnet test tests/McaHub.Tests --filter Manager_can_set_about_and_it_renders_on_the_landing_page`
Expected: FAIL — the description and rendered README are not on the landing page yet.

- [ ] **Step 2: Hoist the HEAD lookup and add the description under the meta line**

In `Repo` (`src/McaHub/Pages.cs`), the block at lines 519-532 currently renders the visibility/owner meta line and the settings form. Replace the meta-line append (line 522) and add a description block right after it. Change:

```csharp
            b.Append($"""<p class="meta">{vis} · owned by {E(db.GetUser(m.OwnerId)?.Login ?? "?")}</p>""");
```

to:

```csharp
            b.Append($"""<p class="meta">{vis} · owned by {Avatar(db.GetUser(m.OwnerId))}{E(db.GetUser(m.OwnerId)?.Login ?? "?")}</p>""");
            if (!string.IsNullOrEmpty(m.Description))
                b.Append($"""<p class="desc">{E(m.Description)}</p>""");
            else if (me is not null && Auth.CanManageSettings(db, name, me.Id))
                b.Append($"""<p class="desc empty"><a href="/r/{E(name)}/edit">+ Add a description</a></p>""");
```

- [ ] **Step 3: Add the "Edit details" link for managers**

Still in `Repo`, the manage-people block begins at line 536 with `if (me is not null && Auth.CanManagePeople(db, name, me.Id))`. Just before that line, add an edit-details link for anyone who can manage settings:

```csharp
        if (me is not null && Auth.CanManageSettings(db, name, me.Id))
            b.Append($"""<p class="actions"><a href="/r/{E(name)}/edit">✎ Edit details</a></p>""");
```

- [ ] **Step 4: Render the README after the clone line**

In `Repo`, after the clone-line append (line 535) and before the manage-settings/people blocks, add the README section:

```csharp
        if (!string.IsNullOrEmpty(m?.Readme))
            b.Append($"""<section class="readme">{Markdown.Render(m.Readme)}</section>""");
        else if (me is not null && Auth.CanManageSettings(db, name, me.Id))
            b.Append($"""<p class="empty"><a href="/r/{E(name)}/edit">+ Add a README</a></p>""");
```

(The raw `Markdown.Render(...)` output is intentionally NOT wrapped in `E()` — it is already sanitized HTML.)

- [ ] **Step 5: Compute HEAD once and emit OG tags on the return**

In `Repo`, the backups section currently calls `rust.RevParse(repoDir, "HEAD")` at line 558. Hoist it: just before the `// A single branch …` comment (line 548), add:

```csharp
        string? head = rust.RevParse(repoDir, "HEAD");
```

Then change the backups guard at line 558 from:

```csharp
        if (rust.RevParse(repoDir, "HEAD") is { } head)
```

to:

```csharp
        if (head is { })
```

Finally, replace the method's return (line 582) from:

```csharp
        return Page(name, b.ToString(), chip);
```

to:

```csharp
        string ogDesc = !string.IsNullOrEmpty(m?.Description)
            ? m!.Description!
            : (head is { } h ? Oneline(rust.ReadCommit(repoDir, h).Message) : "A version-controlled Minecraft world");
        string ogImg = head is { } hi ? $"{ctx.Request.Scheme}://{ctx.Request.Host}/r/{E(name)}/map/{hi}.png" : "";
        return Page(name, b.ToString(), chip, OgTags(name, ogDesc, ogImg));
```

- [ ] **Step 6: Run the full RepoAboutTests suite**

Run: `dotnet test tests/McaHub.Tests --filter RepoAboutTests`
Expected: PASS (all 6 tests, including `Manager_can_set_about_and_it_renders_on_the_landing_page`).

- [ ] **Step 7: Run the broader page/privacy suites to catch regressions**

Run: `dotnet test tests/McaHub.Tests --filter "PageAuthzTests|PrivacyTests|UsabilityTests"`
Expected: PASS (the landing-page changes don't break existing page/authz/privacy expectations).

- [ ] **Step 8: Commit**

```bash
git add src/McaHub/Pages.cs
git commit -m "feat: render description + README + OG tags on the world landing page"
```

---

### Task 6: Home cards — description + owner avatar

**Files:**
- Modify: `src/McaHub/Pages.cs` — the `Home` method (lines 497-505)
- Test: `tests/McaHub.Tests/RepoAboutTests.cs` (add one web test)

- [ ] **Step 1: Write the failing test**

Add to `RepoAboutTests`:

```csharp
    [Fact]
    public async Task Home_card_shows_description()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient owner = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(owner);
        await Accounts.CreateRepoAsync(f, token, "base");
        await Accounts.SetPrivateAsync(owner, "base", false);          // public so it lists for everyone
        await Accounts.SetAboutAsync(owner, "base", "A cosy hillside town", "");

        string home = await (await owner.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("A cosy hillside town", home);
        Assert.Contains("class=\"desc\"", home);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/McaHub.Tests --filter Home_card_shows_description`
Expected: FAIL — the description is not on the home card.

- [ ] **Step 3: Update the home-card render**

In `Home` (`src/McaHub/Pages.cs`), replace the badges + `<li>` build (lines 500-503):

```csharp
                HubRepoMeta? m = db.GetRepo(r.Name);
                string badges = (m?.Private == true ? """ <span class="vis vis-private">private</span>""" : "")
                    + (m is not null ? $""" <span class="owner">{E(db.GetUser(m.OwnerId)?.Login ?? "?")}</span>""" : "");
                b.Append($"""<li><a href="/r/{E(r.Name)}">{E(r.Name)}</a>{badges}<span class="meta">{r.Branches} branch(es){(r.LastWhen is null ? "" : $" · last backup {When(r.LastWhen)}")}</span>{(r.LastMessage is null ? "" : $"<span class=\"msg\">{E(Oneline(r.LastMessage))}</span>")}</li>""");
```

with:

```csharp
                HubRepoMeta? m = db.GetRepo(r.Name);
                string badges = (m?.Private == true ? """ <span class="vis vis-private">private</span>""" : "")
                    + (m is not null ? $""" <span class="owner">{Avatar(db.GetUser(m.OwnerId))}{E(db.GetUser(m.OwnerId)?.Login ?? "?")}</span>""" : "");
                string desc = string.IsNullOrEmpty(m?.Description) ? "" : $"""<span class="desc">{E(m.Description)}</span>""";
                b.Append($"""<li><a href="/r/{E(r.Name)}">{E(r.Name)}</a>{badges}<span class="meta">{r.Branches} branch(es){(r.LastWhen is null ? "" : $" · last backup {When(r.LastWhen)}")}</span>{desc}{(r.LastMessage is null ? "" : $"<span class=\"msg\">{E(Oneline(r.LastMessage))}</span>")}</li>""");
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/McaHub.Tests --filter Home_card_shows_description`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/McaHub/Pages.cs tests/McaHub.Tests/RepoAboutTests.cs
git commit -m "feat: home cards show description + owner avatar"
```

---

### Task 7: Clone copy button + CSS

**Files:**
- Modify: `src/McaHub/Pages.cs` — the clone-line append in `Repo` (line 535)
- Modify: `src/McaHub/wwwroot/app.js`
- Modify: `src/McaHub/wwwroot/style.css`
- Test: `tests/McaHub.Tests/RepoAboutTests.cs` (assert the markup is present)

- [ ] **Step 1: Write the failing test**

Add to `RepoAboutTests`:

```csharp
    [Fact]
    public async Task Clone_line_has_a_copy_button()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient owner = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(owner);
        await Accounts.CreateRepoAsync(f, token, "base");

        string page = await (await owner.GetAsync("/r/base")).Content.ReadAsStringAsync();
        Assert.Contains("id=\"clone-cmd\"", page);
        Assert.Contains("data-copy-target=\"clone-cmd\"", page);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/McaHub.Tests --filter Clone_line_has_a_copy_button`
Expected: FAIL — the clone line has no `id`/copy button.

- [ ] **Step 3: Update the clone line**

In `Repo` (`src/McaHub/Pages.cs`), replace the clone-line append (line 535):

```csharp
        b.Append($"""<p class="clone">Clone: <code>mcagit clone {E(baseUrl)}/r/{E(name)} {E(name)}</code></p>""");
```

with:

```csharp
        b.Append($"""<p class="clone">Clone: <code id="clone-cmd">mcagit clone {E(baseUrl)}/r/{E(name)} {E(name)}</code> <button type="button" class="copy" data-copy-target="clone-cmd">copy</button></p>""");
```

- [ ] **Step 4: Add the copy handler to app.js**

In `src/McaHub/wwwroot/app.js`, add this function inside the first IIFE, just after `wireConfirms` (after line 58):

```javascript
  // Copy-to-clipboard buttons (e.g. the clone command). Reads the target element's text.
  function wireCopy() {
    document.querySelectorAll('button[data-copy-target]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var el = document.getElementById(btn.getAttribute('data-copy-target'));
        if (!el || !navigator.clipboard) return; // no-op where the Clipboard API is unavailable
        navigator.clipboard.writeText(el.textContent).then(function () {
          var prev = btn.textContent;
          btn.textContent = 'copied!';
          setTimeout(function () { btn.textContent = prev; }, 1200);
        }).catch(function () { /* user denied / insecure context — leave the text selectable */ });
      });
    });
  }
```

Then add `wireCopy();` to the `DOMContentLoaded` handler (currently lines 60-64) so it reads:

```javascript
  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.map-box img').forEach(wireMapBox);
    wireScrubber();
    wireConfirms();
    wireCopy();
  });
```

- [ ] **Step 5: Add the CSS**

In `src/McaHub/wwwroot/style.css`, append at the end of the file:

```css
/* repo-page parity (#repo-page-parity): card description, card avatar, clone copy button, README */
ul.repos li .desc { flex-basis: 100%; margin: 0; color: var(--muted); font-size: 13px; }
.desc { color: var(--fg); margin: 0 0 16px; }
.desc.empty a, p.empty a { color: var(--link); }
img.avatar.sm { width: 20px; height: 20px; border-radius: 50%; border: 1px solid var(--border); vertical-align: middle; }

button.copy {
  background: var(--panel); color: var(--muted); border: 1px solid var(--border);
  border-radius: 6px; padding: 2px 8px; font: inherit; font-size: 12px; cursor: pointer;
}
button.copy:hover { border-color: var(--link); color: var(--fg); }

section.readme {
  border: 1px solid var(--border); border-radius: 8px; padding: 16px 18px; margin: 16px 0; background: var(--panel);
}
section.readme h1, section.readme h2, section.readme h3 { text-transform: none; letter-spacing: 0; color: var(--fg); }
section.readme h1, section.readme h2 { border-bottom: 1px solid var(--border); padding-bottom: 6px; }
section.readme code { background: #11161c; padding: .15em .35em; border-radius: 4px; }
section.readme pre { background: #11161c; padding: 12px; border-radius: 6px; overflow-x: auto; }
section.readme pre code { background: none; padding: 0; }
section.readme table { border-collapse: collapse; }
section.readme th, section.readme td { border: 1px solid var(--border); padding: 4px 10px; }
section.readme img { max-width: 100%; }

/* edit-details form (column layout overriding form.find's row) */
form.edit-details { flex-direction: column; align-items: stretch; }
form.edit-details .fld { display: flex; flex-direction: column; gap: 6px; color: var(--muted); font-size: 13px; }
form.edit-details textarea {
  background: var(--panel); color: var(--fg); border: 1px solid var(--border);
  border-radius: 6px; padding: 8px 10px; font: inherit; font-family: ui-monospace, monospace; resize: vertical;
}
form.edit-details button { align-self: flex-start; }
```

- [ ] **Step 6: Run the test + full suite**

Run: `dotnet test tests/McaHub.Tests --filter Clone_line_has_a_copy_button`
Expected: PASS.

Run: `dotnet test tests/McaHub.Tests`
Expected: PASS (entire suite green — no regressions).

- [ ] **Step 7: Commit**

```bash
git add src/McaHub/Pages.cs src/McaHub/wwwroot/app.js src/McaHub/wwwroot/style.css tests/McaHub.Tests/RepoAboutTests.cs
git commit -m "feat: clone copy button + repo-page-parity styles"
```

---

### Task 8: Security review of the README sink

**Files:** none (review gate)

- [ ] **Step 1: Adversarial review of the markdown→HTML sink**

Launch the `hub-security-adversary` agent on the diff for `src/McaHub/Markdown.cs` and its call site in `Pages.cs` (the un-escaped `<section class="readme">{Markdown.Render(...)}</section>` injection). Ask it to confirm: raw HTML cannot survive `DisableHtml`; no URL scheme other than http/https/mailto/relative reaches an `href`/`src`; the CSP backstops anything missed; and the size cap can't be bypassed (e.g. multibyte inflation past `GetByteCount`).

- [ ] **Step 2: Address findings**

Apply any fixes the agent surfaces (add tests first if they reveal a gap), re-run `dotnet test tests/McaHub.Tests`, and commit. If clean, note "security review: no findings" in the task and move on.

---

### Task 9: Documentation

**Files:**
- Modify: `CLAUDE.md`, `SECURITY.md`, `README.md` (via the steward agent)

- [ ] **Step 1: Run the docs steward**

Launch the `hub-docs-steward` agent (or run `/sync-docs`) with this change set:
- **CLAUDE.md** — the "no NuGet packages beyond the framework" claims (appears in the intro line and the architecture section) are now false: document Markdig as the first dependency. Add `Markdown.cs` to the subsystem list. Note the new `HubRepoMeta.Description`/`Readme` fields under `HubDb.cs` and the `/r/{repo}/edit` + `POST /r/{repo}/about` routes under `Pages.cs`.
- **SECURITY.md** — add a trust boundary: rendering user-authored Markdown to HTML. State the invariant (raw HTML disabled + URL-scheme allowlist; the README is the only un-escaped HTML sink) and record the third-party-avatar IP-leak soft spot.
- **README.md** — add the About/README feature to the feature list and the new routes/fields to any route or env documentation.

- [ ] **Step 2: Verify and commit**

Run: `dotnet build src/McaHub` (sanity) and skim the three docs for accuracy.

```bash
git add CLAUDE.md SECURITY.md README.md
git commit -m "docs: README rendering, About fields, Markdig dependency, markdown trust boundary"
```

---

## Self-Review

**Spec coverage:**
- Data model (Description/Readme + SetRepoAbout) → Task 3. ✓
- Edit UX (`GET /edit`, `POST /about`, CanManageSettings, CSRF) → Task 4. ✓
- Rendering (description, README above backups, OG, empty states) → Task 5. ✓
- Markdown security (DisableHtml + URL allowlist, single sink) → Task 2 + review in Task 8. ✓
- Copy button → Task 7. ✓
- Avatars (cards + header) → `Avatar` helper in Task 4; header use in Task 5 Step 2; card use in Task 6. ✓
- Limits (200 chars / 32 KB, reject not truncate) → Task 4 (constants + handler) with tests. ✓
- Tests (data round-trip, back-compat, markdown sanitization, authz, limits, render) → Tasks 2, 3, 4, 5, 6, 7. ✓
- Docs (CLAUDE/SECURITY/README, Markdig-as-first-dep) → Task 9. ✓

**Placeholder scan:** No TBD/TODO; every code step shows the actual code and exact commands.

**Type consistency:** `Markdown.Render(string?)`, `HubRepoMeta(..., string? Description, string? Readme)`, `SetRepoAbout(string, string?, string?)`, `Avatar(HubUser?)`, `CleanDescription(string)`, `MaxDescriptionChars`/`MaxReadmeBytes`, `EditDetails(...)`, and the test helper `Accounts.SetAboutAsync(HttpClient, string, string, string)` are named identically across every task that references them.

**Note for the implementer:** Task 4 Step 7 deliberately leaves one assertion in `Manager_can_set_about_and_it_renders_on_the_landing_page` failing until Task 5; run the narrowed filter given there to stay green between tasks, then the full `RepoAboutTests` after Task 5.
