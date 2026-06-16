# Repo-page parity: About + README, cards, copy, avatars

**Date:** 2026-06-15
**Status:** Approved, ready for implementation plan
**Goal:** Make a world's landing page read more like a GitHub repo page — give it an
editable description and a rendered README, surface the description on the home cards,
add a copy-to-clipboard affordance to the clone command, and show owner avatars.

## Context

The hub already wins the "content" layer (semantic diff, grief forensics, map renders,
time machine, world explorer). What makes it feel *unlike* GitHub is the wrapper. An
inventory of the current web UI found the clone line already exists (`Pages.cs:535`) and
home cards already show owner + visibility + last-backup time. The conspicuous gap in
repo-page parity is **About/README content**, which is entirely absent.

GitHub READMEs live as a file *in the repo*. That does not map here — these repos are
binary Minecraft world data with no natural place for a user-edited README, and editing
one would require a CLI push. So the content is **hub-stored metadata**, edited through
the web UI. (Decision: hub metadata, not a file in the world repo.)

Markdown rendering uses **Markdig** — the project's first NuGet dependency. This is a
deliberate, approved break from the "no NuGet packages beyond the framework" rule.

## Scope (this iteration)

In:
- About description + README: data model, web edit form, rendering on the landing page.
- Description shown on home cards.
- Copy-to-clipboard button on the clone command.
- Owner avatar on home cards and the repo header.

Out (explicitly not now): issues/discussions, user profile pages, global search, stars,
light-mode toggle, avatar proxying (camo). These are later tiers.

## Design

### 1. Data model (`HubDb.cs`)

Extend the positional record:

```csharp
HubRepoMeta(string Name, string OwnerId, bool Private, string CreatedAt,
            string? Description = null, string? Readme = null)
```

Additive and nullable: old `hub.json` files deserialize the new params as `null` (the
constructor defaults), and older binaries ignore unknown JSON keys (System.Text.Json
default). **No `CurrentSchema` bump** — the shape change is backward- and
forward-compatible.

New mutator, routed through the existing `Mutate` (cross-process lock + reload + atomic
publish):

```csharp
public void SetRepoAbout(string name, string? description, string? readme) => Mutate(() =>
{
    int i = _db.Repos.FindIndex(r => r.Name == name);
    if (i >= 0) _db.Repos[i] = _db.Repos[i] with { Description = description, Readme = readme };
});
```

Validation (trim, caps) happens in the handler before this call; the store trusts the
already-clamped values.

### 2. Edit UX (`Pages.cs`, `Program.cs`)

- `GET /r/{repo}/edit` — a small page: a one-line **Description** `<input maxlength=200>`
  and a **README** `<textarea>` (markdown), both prefilled from `HubRepoMeta`.
- `POST /r/{repo}/about` — validates, calls `SetRepoAbout`, redirects to `/r/{repo}`.
- **Authz:** both gated by `Auth.CanManageSettings` (owner/admin/maintain) — the same
  predicate as the existing visibility toggle. The POST validates CSRF (`CsrfField` /
  `CsrfOk`). A caller who can't see the repo gets the standard 404 (existence hidden); a
  signed-in non-manager who can see it is refused.
- The landing page shows an "✎ Edit details" link in the About area to managers only.
- In open mode there is no identity, so no editor renders — consistent with the existing
  settings/collaborators UI, which only appears when `me is not null`.

### 3. Rendering (`Pages.cs`)

- **Description** — escaped plain text (`Html.E`) in the repo header, under the
  visibility/owner line, and on each home card (`<span class="desc">`). Managers see an
  "Add a description" empty-state link when it's unset.
- **README** — a `<section class="readme">` holding the sanitized HTML from `Markdown.cs`,
  placed **after** the clone/actions block and **before** the Branches/Backups sections
  (it reads as the world's intro). Rendered only when non-empty; managers see an "Add a
  README" prompt otherwise.
- **OG tags** — add `OgTags` to the repo landing: title = name, description =
  `Description` ?? last commit message ?? a default, image = the HEAD map PNG. Privacy is
  preserved because the landing page only renders to callers who pass `CanSee`; a crawler
  hitting a private repo gets a 404, identical to the commit page's existing behavior.

### 4. Markdown rendering + sanitization (new `Markdown.cs`)

A single static, build-once `MarkdownPipeline`. The README is **user-authored content
rendered to HTML** — a new trust boundary. It is the one place the app emits HTML *not*
routed through `Html.E`, so it must be airtight.

Pipeline:
- **`.DisableHtml()`** — strips raw inline and block HTML, removing the primary XSS
  vector (`<script>`, `<img onerror=…>`, etc.).
- Conservative extensions only: CommonMark core + autolinks + pipe tables. No raw-HTML
  extension; no blanket `UseAdvancedExtensions()`.
- **URL allowlist:** after parsing to a `MarkdownDocument`, walk every `LinkInline`
  (links and images) and blank any `Url` whose scheme isn't `http`, `https`, `mailto`, or
  relative/anchor. This kills `[x](javascript:alert(1))` and `data:`-URI images that
  survive `DisableHtml`. Add `rel="nofollow ugc noopener"` to anchors.

`Markdown.Render(string md) -> string` returns the safe HTML, injected raw into the page.

Defense in depth: the existing CSP (`script-src 'self'`, no `unsafe-inline`;
`img-src 'self' data: https:`) neutralizes anything that slips past the sanitizer.

This file and its call site go through the `hub-security-adversary` agent before merge.

### 5. Copy button (`Pages.cs`, `app.js`, `style.css`)

The clone line becomes:

```html
<code id="clone-cmd">mcagit clone …</code>
<button class="copy" data-copy-target="clone-cmd">copy</button>
```

A delegated `app.js` handler on `[data-copy-target]` reads the target element's
`textContent`, calls `navigator.clipboard.writeText(...)`, and shows brief "copied!"
feedback. It no-ops gracefully where the Clipboard API is unavailable (insecure context /
old browser). CSP-safe — no inline JS, same external-file pattern as the existing
confirm/scrubber handlers.

### 6. Avatars (`Pages.cs`, `style.css`)

Render `<img class="avatar" src="{E(avatar)}" alt="" width=20 height=20 loading="lazy"
referrerpolicy="no-referrer">` next to the owner login on home cards and the repo header,
only when `HubUser.Avatar` is non-empty. `img-src https:` already permits it — **no CSP
change**.

Privacy note: loading a third-party avatar leaks the viewer's IP to the avatar host.
`referrerpolicy="no-referrer"` trims the referer header; a camo-style proxy is the proper
fix and is deferred. Flagged for the docs/security review.

### 7. Limits / validation

- **Description:** trimmed, newlines collapsed/stripped, capped at **200 chars**.
- **README:** capped at **32 KB** of raw markdown — over-limit input is **rejected with a
  clear message**, not silently truncated. Rationale: `hub.json` is loaded into memory and
  rewritten atomically (temp + rename, under a cross-process lock) on *every* mutation
  across all instances; large blobs slow every write and bloat the shared file.

## Testing (TDD — tests first)

- `HubDb.SetRepoAbout` round-trips description + readme; survives a reload; old JSON
  without the fields still loads (back-compat).
- `Markdown.Render`:
  - `[x](javascript:alert(1))` → no `javascript:` in output.
  - `data:`-URI image → stripped.
  - raw `<script>` / `<img onerror>` in the source → stripped (`DisableHtml`).
  - normal markdown (heading, bold, link, list, table) → expected tags present; links
    carry `rel="nofollow ugc noopener"`.
- Authz: `GET /r/{repo}/edit` and `POST /r/{repo}/about` require `CanManageSettings`; a
  write-only collaborator and an anonymous user are denied; a private repo's existence
  stays hidden (404, not 403).
- Limits: oversize README rejected; over-length description capped.
- Landing render: description + README appear when set; managers get the empty-state
  prompts when unset; non-managers never see edit affordances.

## Docs (hub-docs-steward, after implementation)

- **CLAUDE.md** — the "no NuGet packages beyond the framework" claim is now false; document
  Markdig as the first dependency and add `Markdown.cs` to the subsystem list; note the new
  `HubRepoMeta` fields under `HubDb.cs`.
- **SECURITY.md** — new trust boundary: rendering user-authored markdown to HTML. State
  the invariant (raw-HTML disabled + URL scheme allowlist; README is the only un-escaped
  HTML sink) and the avatar IP-leak soft spot.
- **README.md** — feature entry + the new `/r/{repo}/edit` and `POST /r/{repo}/about`
  routes and the description/readme fields.

## Decisions on record

- Content source: **hub metadata**, not a file in the world repo.
- Markdown: **Markdig** (first NuGet dependency), with `DisableHtml` + URL allowlist.
- Edit lives on a **dedicated `/r/{repo}/edit` page**, not inline on the landing page.
- README renders **above** the backups timeline (intro position), not GitHub-bottom.
- About editing is **accounts-mode only**, gated by `CanManageSettings`.
- No `hub.json` schema bump (additive nullable fields).
