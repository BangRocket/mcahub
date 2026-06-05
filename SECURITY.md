# Security notes / reviewer's guide

This is a self-hostable web app that hosts [`mcadiff`](https://github.com/BangRocket/mcadiff) world
repositories over HTTP and renders them. It accepts **untrusted input from two directions**: the network
(anyone who can `mcadiff clone/fetch/push` to it) and the **world data itself** (attacker-controlled NBT /
region files that get parsed, materialized to disk, and rendered to images). This doc is a map of that
surface so a review can go straight at the interesting parts. Findings welcome as issues/PRs.

It's deliberately candid about soft spots — see [Where I'd look](#where-id-look). Nothing here is load-bearing
on secrecy; assume an attacker has read this file.

## Run it locally

See the README to build/run (.NET 10, needs the `mcadiff` core checked out as a sibling). For poking at the
account/permission logic without a real OAuth app, set `MCAHUB_DEV_LOGIN=1` and sign in at `/auth/dev`
(insecure local login — there's a warning on it; it must never be enabled on a real host). The three auth
modes (open / shared-token / OAuth accounts) are described in the README under "Auth modes".

## Trust boundaries

| Boundary | Entry point | Untrusted input | Gate |
|---|---|---|---|
| **Network — read** | `GET /r/{repo}/info\|have\|objects` | repo name, object hashes, Bearer token | `Transport.Readable` → `Auth.CanRead`; private repos return **404** (hide existence) |
| **Network — write** | `POST /r/{repo}/objects\|pack\|refs` | pack bytes, RefUpdate JSON, token | `Transport.Write` → Bearer auth + `Auth.CanWrite`; FF-check & pack hash verify happen in the core |
| **World data** | push → `ObjectStore` → `Checkout.Materialize` → `MapRenderer`/`WorldQuery` | compressed chunk NBT, manifests, file paths | core `SafeInflate` / `NbtDepthGuard` / `PathGuard.Confine`; `BlockStateDecoder` bounds |
| **Web — session** | OAuth `/auth/*`, cookie | OAuth callback, `returnUrl` | framework cookie+OAuth (PKCE+state); `Auth.Local` open-redirect guard |
| **Web — actions** | state-changing `POST`s | form fields | **antiforgery token** (`Auth.CsrfOk`) + `SameSite=Lax` + capability checks |
| **Config** | env / `.env` | OAuth secrets, master token | `.env` gitignored; `LoadDotEnv` never overrides real env |

## Start here (the files that matter)

- **`src/McadiffHub/Auth.cs`** — the crux. OAuth wiring (PKCE, state, userinfo→`provider:id`), cookie session,
  **Bearer-token identity** (`Identify` — master token via `CryptographicOperations.FixedTimeEquals`, else
  per-user PAT), the **CSRF** helpers (`CsrfField`/`CsrfOk`), the **access-control predicates**
  (`CanRead`/`CanWrite`/`CanManageSettings`/`CanManagePeople`, all routed through `HubDb.RoleOf`), and the
  open-redirect guard `Local`.
- **`src/McadiffHub/Transport.cs`** — the network protocol. Every GET checks `Readable`; every write goes
  through `Write` (auth + `CanWrite` + auto-create + **ownership claim-on-first-push**).
- **`src/McadiffHub/HubDb.cs`** — the account store. **Token hashing** (`mcahub_` + 30 random bytes, stored
  SHA-256, plaintext shown once, looked up by hash), role/rank resolution, collaborators + teams.
- **`src/McadiffHub/RepoStore.cs`** — `IsValidName` regex (the path-traversal guard for repo names) and
  `PathOf`.
- **`src/McadiffHub/Pages.cs`** — web handlers; `CanSee` gates private repos to 404; the `/r/{repo}/map/{ref}.png`
  endpoint; the time-machine page embeds backup JSON (`System.Text.Json`, captions via `textContent`).
- **`src/McadiffHub/MapRenderer.cs` + `MapCache.cs`** — the **untrusted-NBT-to-image** path: decodes
  attacker-pushed chunk data and allocates image buffers from it.
- **`src/McadiffHub/WorldCache.cs`** — materializes pushed worlds to disk (`cache/<repo>/<commit>`).
- **`src/McadiffHub/Program.cs`** — `LoadDotEnv`, the `MCAHUB_BEHIND_PROXY` forwarded-headers switch, the
  `render` CLI mode.

## Controls already in place (what's intended)

- **Repo names** are validated `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$` before any path is built — no `/`, can't
  escape the data dir.
- **Tokens** are never stored in the clear: SHA-256 of `mcahub_<30 random bytes>`, shown once, resolved by
  hash. The master/admin token is compared in constant time.
- **Private repos return 404, not 403**, on both the web pages and the transport (`info/refs`, `have`,
  `objects`) — a non-collaborator can't even confirm a private world exists.
- **One source of truth for authz:** `HubDb.RoleOf` (owner > admin > maintain > write > read) feeds the
  transport, the web pages, *and* which controls render — they can't drift.
- **CSRF:** every cookie-authenticated state-changing POST validates an antiforgery token; the Bearer-only
  transport POSTs are intentionally *not* in that path, so the CLI still works.
- **OAuth** uses the framework's handlers with PKCE + state; identities are namespaced by provider so two
  providers can't collide. `returnUrl` is restricted to local paths.
- **Response headers + cookies:** every response carries a strict CSP (`script-src 'self'` — all client JS
  is the static `/app.js`, with per-request scrubber data in a JSON data-island), `X-Frame-Options`,
  `X-Content-Type-Options`, `Referrer-Policy`, and HSTS over HTTPS; session + antiforgery cookies are
  `HttpOnly`, `SameSite=Lax`, and `Secure` on HTTPS.

## Where I'd look

Honest list of the thinner spots — these are the bugs I'd expect a review to find:

1. **Resource exhaustion / DoS (the big one).** There is **no size or rate limiting** on push, on
   `Checkout.Materialize`, or on map rendering. A single push can be enormous (we hit a 310k-chunk world that
   pegged the host). `WorldCache`/`MapCache` write to disk **unbounded** — a hostile or just-large push can
   fill the disk; many distinct commits multiply it. `MapRenderer` caps the rendered *span* at 160×160 chunks
   but the per-render chunk dictionary holds up to 30k decoded chunks, and the materialize that precedes it is
   unbounded. No auth-attempt throttling. **This is the area most likely to bite a real deployment.**
2. **Untrusted NBT → image.** `MapRenderer` trusts the core's `BlockStateDecoder` output and sizes image
   buffers from chunk coordinates found in the data. Worth checking: extreme/negative section `Y`, malformed
   palettes, huge or sparse chunk bounds, and whether any allocation is attacker-sized before the 160-cap
   applies. The decompression/depth bounds (`SafeInflate`, `NbtDepthGuard`) live in the **mcadiff core**, not
   here — verify the hub never reaches around them.
3. **Path handling on materialize.** Writing a pushed world to disk relies on the core's `PathGuard.Confine`
   to keep manifest file paths inside the target dir. A malicious manifest with `..`/absolute paths is the
   classic test; confirm the boundary holds from the hub's call sites.
4. **Ownership claim-on-first-push.** In accounts mode, a push to a *genuinely new name* auto-creates and
   claims it. A repo that already exists on disk with **no owner** is **not** claimable by a non-admin
   (the takeover guard) unless `MCAHUB_ADOPT_UNOWNED=1` opens a supervised migration window; master-token
   pushes stamp a `__system__` owner so they're never orphan-claimable, and the hub warns at startup about
   any unowned worlds (`Transport.Write` → `HubDb.EnsureRepo`). Was a takeover vector pre-#6.
5. **Master token = full admin bypass** (`MCAHUB_TOKEN`). Single shared secret; if it leaks, game over. No
   rotation, no scoping.
6. **Account store is a plaintext JSON file** (`hub.json`): usernames, avatars, token *hashes*, grants. Not
   encrypted at rest. Concurrency is a single process-wide lock with atomic tmp+rename writes — check for
   races / partial reads under load.
7. **CSRF edges.** Logout is a `GET` (low-impact forgery). The antiforgery cookie is `SameSite=Lax`; confirm
   there's no state-changing `GET` that matters. Dev-login (`/auth/dev`) is CSRF-protected but is insecure by
   construction — make sure it can't be reached when `MCAHUB_DEV_LOGIN` is unset.
8. **Reverse-proxy header trust.** With `MCAHUB_BEHIND_PROXY=1`, `ForwardedHeaders` trusts
   `X-Forwarded-Proto/Host` from *any* source (KnownProxies/Networks cleared). Only safe if the app is truly
   reachable only via the proxy — easy to misconfigure into header spoofing.

## By-design / out of scope

- **Open mode** (no `MCAHUB_TOKEN`, no OAuth) is intentionally unauthenticated — for a trusted LAN. The hub
  now **fails closed**: it refuses to start in open mode on a non-loopback bind unless
  `MCAHUB_I_KNOW_OPEN_MODE_IS_PUBLIC=1` is set (`StartupGuard`).
- **`MCAHUB_DEV_LOGIN`** is an insecure eval-only login, gated off by default, loudly labeled, and refused
  outright (no override) on a non-loopback bind.
- The hub leans on the **mcadiff core** for NBT parsing, decompression bounds, pack/hash verification, and
  path confinement. Core-level vulnerabilities belong in that repo, but flag the boundary if the hub feeds it
  unsafely.

## Supply chain

The hub references the core via a **sibling `ProjectReference`** (`../mca-git/...`), so it ships the core's
transitive NuGet deps (`fNbt`, `K4os.Compression.LZ4`, and the cloud SDKs the core carries) — which the
hub's own Dependabot doesn't see, since the hub declares no direct runtime packages. CI compensates by
running `dotnet list package --vulnerable --include-transitive` over the **shipped graph** (with the core
checked out) and **failing on a known CVE** — `fNbt` in particular sits directly on attacker-controlled
bytes (anonymous push → render). The intended end state (cross-repo) is to publish the core as a
**versioned NuGet** and replace the sibling reference with a **pinned `PackageReference`**, so Dependabot
tracks the transitive graph and the unused cloud SDKs can be trimmed. CI pins the core to a known commit
(`41f6f2f`) until then.

## Reporting

Open an issue (or PR) on this repo. For anything you'd rather not file publicly, contact the owner directly.
