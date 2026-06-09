# mcahub

**GitHub, for Minecraft worlds.** A self-hostable platform that hosts [`mcagit`](https://github.com/BangRocket/mcagit)
repositories (version-controlled worlds) over HTTP and gives them a web face: push a world, browse its
backup timeline, and see the **semantic diff** of any backup — which chunks, blocks, and entities
changed, and *where the griefing happened* — none of which a generic git host can show.

If mcagit is git for worlds, this is the hub you push them to.

## What it does

- **Hosts worlds over HTTP** — `mcagit clone | fetch | push http://<hub>/r/<name>` works against it.
  Pushing to a new name **auto-creates** the world.
- **Web UI** — a repo list, each world's branches + backup timeline, a backup view that combines the
  file/chunk/block diff with a "what happened here" grief summary (destroyed / placed / replaced, the
  destruction bounding box, the most-destroyed blocks), a **compare-any-two-backups** view (the same
  forensics between arbitrary backups), a **world explorer** (players + find an entity / block entity /
  sign), a **rendered top-down map** per backup (with a before/after pair on the compare page), and a
  **time-machine scrubber** that slides the map across a world's whole backup history.
- **Powered by the Rust `mcagit` engine** — the C# web layer drives the `mcagit` binary out-of-process: it
  reverse-proxies the transport to a co-located `mcagit serve` sidecar, and renders the web UI from
  `mcagit diff/where-changed/players/find --json` + `mcagit render`. No in-process .NET core; worlds are
  stored in mcagit's native (blake3/zstd) object format.
- **Accounts (optional)** — OAuth sign-in (GitHub by default, any OAuth2 provider via config), per-user
  **personal access tokens** for the CLI, **public/private** worlds, and sharing via per-repo
  **collaborators** (read/write) and **teams** (grant a whole group at once). Off by default; the hub stays
  a zero-config local tool until you hand it an OAuth app.

## Run it

### Docker (no SDK)

The quickest way:

```sh
docker compose up          # serves http://localhost:5080
```

(or pull the published image directly: `docker run -p 5080:5080 -v mcahub:/data ghcr.io/bangrocket/mcahub`).
It runs as a non-root user with a read-only root filesystem and your data on a named volume.
**`docker compose` defaults to open mode** (anyone reachable can read **and** push) — fine for a trusted
LAN; set `MCAHUB_TOKEN` or the `MCAHUB_OAUTH_*` vars before exposing it publicly (see the compose file).
The image bundles the Rust **`mcagit`** binary (the hub spawns it for transport + rendering); see the
`Dockerfile` for how it's built/copied in.

### From source

Needs the **.NET 10 SDK** and the Rust **`mcagit`** binary — the hub drives it out-of-process (no more
.NET-core submodule). Build mcagit (`cargo build --release` in the mcagit repo) and put it on `PATH`, or
point `MCAGIT_BIN` at it:

```sh
git clone https://github.com/<you>/mcahub
export MCAGIT_BIN=/path/to/mcagit/target/release/mcagit   # or just have `mcagit` on PATH
dotnet run --project src/McaHub          # serves http://localhost:5080
```

A self-contained binary for your OS (no SDK needed) is attached to each [GitHub Release](../../releases).

Then point a client at it (the same `mcagit` binary, as a client):

```sh
mcagit init MyWorld.mcagit --worktree path/to/world
mcagit -C MyWorld.mcagit commit -m "first backup"
mcagit -C MyWorld.mcagit push http://localhost:5080/r/myworld main   # auto-creates "myworld"
```

Open <http://localhost:5080> to browse it.

### Automatic backups (sidecar)

For hands-off backups, run the **sidecar** (`src/Sidecar`) next to your server — it watches a world
directory and auto-pushes a backup on a schedule and whenever the world changes (debounced), committing
only when something actually changed, plus one final backup on shutdown:

```sh
MCAGIT_BIN=/path/to/mcagit \
MCASIDE_WORLD=/srv/minecraft/world \
MCASIDE_REMOTE=http://localhost:5080/r/myworld \
MCASIDE_TOKEN=mcahub_… \
dotnet run --project src/Sidecar          # or the published `mcahub-sidecar` binary
```

The sidecar also drives the Rust `mcagit` binary (`init`/`commit`/`push`), so it needs `mcagit` on
`PATH` or `MCAGIT_BIN`. This is the no-Java, any-server path; a drop-in **Paper/Spigot/Fabric** plugin
(which can also `save-off`/`save-all` around the snapshot) is tracked as a separate Java deliverable.

### Configuration

| Setting | Env var | Default | Purpose |
|---|---|---|---|
| mcagit binary | `MCAGIT_BIN` | `mcagit` (on `PATH`) | The Rust `mcagit` binary the hub spawns for transport (`serve`) + rendering/diff/query. |
| Data dir | `MCAHUB_DATA` | `data/repos` | Where hosted `<name>` repos live (mcagit blake3/zstd format). |
| World cache | `MCAHUB_CACHE` | `data/cache` | Materialized worlds for the explorer (one checkout per immutable backup). |
| Map cache | `MCAHUB_MAPS` | `data/maps` | Rendered map PNGs (one per immutable commit). |
| Account DB | `MCAHUB_DB` | `data/hub.json` | Users, hashed tokens, repo ownership/visibility. |
| Audit log | `MCAHUB_AUDIT` | `data/audit.jsonl` | Append-only trail of role/visibility/ownership/ref/token changes; owners see a per-world history at `/r/<name>/audit`. |
| Push token | `MCAHUB_TOKEN` | (none) | A shared/master token. In open mode it gates writes; in accounts mode it's an admin bypass. |
| Push token (hashed) | `MCAHUB_TOKEN_SHA256` | (none) | SHA-256 hex of the master token(s) — keeps the plaintext out of the env. Accepts a comma/space list so you can **rotate without downtime**: add the new hash, switch clients, then drop the old one. |
| Bind URL | `ASPNETCORE_URLS` | `http://localhost:5080` | Put it behind a reverse proxy for TLS. |

#### Resource limits (defense-in-depth)

The hub parses and stores attacker-supplied world data, so the heavy paths are bounded. Defaults are
generous for normal use; lower them on small/shared hosts.

| Env var | Default | Purpose |
|---|---|---|
| `MCAHUB_MAX_PUSH_BYTES` | `268435456` (256 MiB) | Max push body buffered; larger is rejected with `413` (also raises the Kestrel limit to match). |
| `MCAHUB_CACHE_MAX_GB` | `10` | Size ceiling for the materialized **world** cache; least-recently-used worlds are evicted past it. |
| `MCAHUB_MAX_WORLDS_PER_REPO` | `10` | Keep at most this many materialized worlds per repo. |
| `MCAHUB_MAP_CACHE_MAX_GB` | `2` | Size ceiling for the rendered **map** PNG cache (LRU eviction). |
| `MCAHUB_MAX_MAPS_PER_REPO` | `100` | Keep at most this many map PNGs per repo. |
| `MCAHUB_MAX_MANIFEST_ENTRIES` | `100000` | Refuse to materialize a world whose manifest has more file/dir entries (inode-exhaustion guard). |
| `MCAHUB_MAX_RENDER_CONCURRENCY` | `3` | Max map renders running at once. |
| `MCAHUB_MAX_RENDER_CHUNKS` | `10000` | Max chunks decoded per render; bigger worlds truncate. |
| `MCAHUB_RENDER_TIMEOUT_SECONDS` | `30` | Hard server-side deadline for a single map render. |
| `MCAHUB_RATELIMIT_AUTH` | `20` | Auth / token requests per IP per minute. |
| `MCAHUB_RATELIMIT_WRITE` | `60` | Push (transport write) requests per IP per minute. |
| `MCAHUB_RATELIMIT_RENDER` | `30` | Cold map renders per IP per minute. |
| `MCAHUB_RATELIMIT_READ` | `300` | Read / page requests per IP per minute. |
| `MCAHUB_AUTH_MAX_FAILURES` | `5` | Bad Bearer tokens from one IP before a temporary lockout. |
| `MCAHUB_AUTH_LOCKOUT_SECONDS` | `30` | Base lockout after the failure threshold (doubles with continued failures). |

A full cache evicts oldest-first and, if a single entry exceeds the ceiling, refuses it with a clear
error rather than silently filling the disk. Rate limits are **per client IP** and return `429` with
`Retry-After`; behind a reverse proxy, set `MCAHUB_BEHIND_PROXY=1` so the real client IP is used (else
every client shares one bucket).

**Operating.** `GET /health` returns `200 {"status":"ok",…}` for proxy/orchestrator liveness probes — it's
unauthenticated and rate-limit-exempt, so **don't block it at the proxy**. If the disk fills, a write to
the account database fails with **`507 Insufficient Storage`** (the in-memory state isn't advanced, so
nothing is silently lost) instead of a 500. `hub.json` carries a schema version: a file written by a
**newer** hub makes this one **refuse to start** with an actionable message rather than misread and
overwrite it — back it up and upgrade.

On **SIGTERM** (a deploy, `docker stop`, systemd) the hub **drains** — it stops accepting new connections
and lets in-flight requests finish (up to the render deadline) instead of guillotining a render. Cold map
renders (which also materialize the backup's world) run as **background jobs** on a hosted worker pool, not
on the request thread: a client that disconnects or times out no longer aborts the render — it finishes and
fills the immutable, commit-keyed cache for the next viewer. Each pending job has a **durable marker** that
is re-enqueued on startup, so a render interrupted by a crash or deploy **resumes** rather than waiting to be
re-triggered (and a resumed job that already completed simply hits the warm cache).

Two instances may **share one data directory** for zero-downtime rolling deploys: every account-store write
takes a **cross-process advisory lock** (`hub.json.lock`) and reloads `hub.json` before mutating, then
publishes atomically — so a concurrent writer can never tear or clobber the store, and neither instance
overwrites the other's committed change. Reads reload when the file changed underneath them, so a revoked
token or changed grant on one instance is seen by the other on its next read. A sustained two-writer workload
still wants vertical scaling (or a future real DB).

### Auth modes

The hub runs in one of three modes, chosen by what you configure:

- **Open** *(default — nothing set)*: anonymous, every world public, open push. Great for a trusted LAN.
  As a safety net, the hub **refuses to start in open mode on a non-loopback bind** unless you set
  `MCAHUB_I_KNOW_OPEN_MODE_IS_PUBLIC=1` — so you can't accidentally expose anonymous write to the internet.
  (`MCAHUB_DEV_LOGIN` is refused off-loopback with no override at all.)
- **Token** *(`MCAHUB_TOKEN` set)*: reads anonymous, writes need `--token <that token>`.
- **Accounts** *(OAuth configured)*: real users sign in via OAuth; each gets personal access tokens for the
  CLI; worlds can be **private**. The CLI can't run a browser redirect, so `mcagit push/clone` against a
  private world uses a personal access token (mint one at `/account`) — exactly how GitHub handles `git push`.
  Tokens carry a **scope** (`read` = clone/fetch, `write` = + push), an optional **expiry**, and can be
  **regenerated** (rotated) or wiped with **"sign out everywhere"** (revokes all tokens and invalidates
  every web session).
  An owner shares a private world by adding **collaborators** on the world's page, or by granting a **team**
  (a named group managed at `/teams`) — every member inherits the team's role. Roles form a ladder:
  **read** (browse/clone) → **write** (+ push) → **maintain** (+ change visibility) → **admin** (+ manage
  collaborators & team grants), with the **owner** above all. Effective access is the strongest of owner,
  direct collaborator, and any team grant. Every state-changing form is **CSRF-protected** with an
  antiforgery token (on top of the `SameSite=Lax` session cookie).

You can enable **several providers at once** — each is on iff its own client id+secret are set, and the
sign-in page shows a button per enabled provider. Identities are namespaced (`github:…`, `microsoft:…`,
`minecraft:<uuid>`, `discord:…`) so they never collide. Each first-class provider's callback is
`https://<host>/auth/callback/<name>` (the legacy generic one keeps `/auth/callback`).

| Accounts env var | Default | Purpose |
|---|---|---|
| `MCAHUB_OAUTH_GITHUB_CLIENT_ID` / `_SECRET` | — | GitHub sign-in. |
| `MCAHUB_OAUTH_MICROSOFT_CLIENT_ID` / `_SECRET` | — | Microsoft sign-in (work/school/personal). |
| `MCAHUB_OAUTH_MICROSOFT_TENANT` | `common` | `common` (any) or `consumers` (personal only). |
| `MCAHUB_OAUTH_MINECRAFT_CLIENT_ID` / `_SECRET` | — | **Minecraft (Java)** sign-in — yields the verified Minecraft UUID + username. The Azure app **must be approved for the Minecraft API** (apply at `aka.ms/mce-reviewappid`) and use the `consumers` tenant, or `api.minecraftservices.com` returns 403. |
| `MCAHUB_OAUTH_DISCORD_CLIENT_ID` / `_SECRET` | — | Discord sign-in. |
| `MCAHUB_OAUTH_CLIENT_ID` / `_SECRET` (+ `_PROVIDER` / `_AUTH_URL` / `_TOKEN_URL` / `_USER_URL` / `_SCOPE`) | — | Generic OAuth2 escape hatch (GitLab, Gitea, any OIDC). Kept for back-compat; callback stays `/auth/callback`. |
| `MCAHUB_DEV_LOGIN` | (off) | ⚠ Insecure local login at `/auth/dev` for evaluating accounts without an OAuth app. Setting this **turns on accounts mode** on its own — anonymous pushes then fail and the CLI needs a personal access token. **Never on a public host.** |
| `MCAHUB_ADOPT_UNOWNED` | (off) | Let the first authenticated push to a **pre-existing** unowned world claim it. Off by default so a signed-up user can't take over a legacy world; turn it on only during a supervised migration. |
| `MCAHUB_DEFAULT_PRIVATE` | `1` (on) | New worlds are **private until you publish them**, so a first push isn't world-readable by surprise. Set `0` for public-by-default on a trusted LAN. (Separately, the world explorer only shows player coordinates/health and sign text to a world's **collaborators**, so a public world doesn't doxx its players.) |
| `MCAHUB_REPORT_EMAIL` | — | Abuse-report address. When set, non-owners see a **"Report this world"** link on each world's page. The operator can take a world down with the master token: `curl -X POST -H "Authorization: Bearer <MCAHUB_TOKEN>" https://<host>/admin/repos/<name>/remove`. (A user can also be suspended — a non-destructive lockout from read/write.) |
| `MCAHUB_MIN_AGE_GATE` | (off) | Require a **13+/parental-consent** confirmation on first sign-in before any page works (logged). Off by default for school/LAN self-hosts; **turn it on for a public launch** (Minecraft skews young). |
| `MCAHUB_MAX_WORLDS_PER_USER` | `0` (∞) | Fair-use cap on how many worlds one account may own (a new push past it gets 403). `0` = unlimited. Distinct from the per-IP/size DoS limits above — this is governance, to stop one user flooding the home page. |
| `MCAHUB_DISCORD_WEBHOOK` | — | *(Deferred during the Rust-engine port — the on-push grief-alert embed is being reimplemented over mcagit's grief output; the var is currently inert.)* |

A public world's pages carry **OpenGraph/Twitter** meta so a pasted link unfurls into its map, and **`/r/<name>/embed`** is a chrome-less, iframe-embeddable map for forums/wikis.

Every page links the built-in **Acceptable Use Policy** (`/aup`) — the agreement that gives the operator a basis for takedown.

A user can **delete their account** (and all worlds they own) from `/account`, and an owner can **delete a world** from its page — GDPR/CCPA erasure, both with a typed confirmation.

When accounts are on, a push to a **genuinely new name** auto-creates and claims it. A world that already
exists on disk with **no owner** (e.g. pushed before accounts were enabled) is *not* claimable by a
regular user — an admin must adopt it, or you can open a supervised migration window with
`MCAHUB_ADOPT_UNOWNED=1`. The hub logs a warning at startup listing any unowned worlds.

### Enable GitHub sign-in (quickstart)

1. **Create an OAuth App** — <https://github.com/settings/developers> → *New OAuth App*:
   - **Homepage URL:** `http://localhost:5080`
   - **Authorization callback URL:** `http://localhost:5080/auth/callback`

   (The callback host must match how you reach the hub — the redirect URI is derived from the request, so
   use your real `https://host` in production.)
2. **Register**, then copy the **Client ID** and *Generate a new client secret*.
3. **Drop them into `.env`** at the hub root (`cp .env.example .env`, then fill in):

   ```sh
   MCAHUB_OAUTH_CLIENT_ID=<your client id>
   MCAHUB_OAUTH_CLIENT_SECRET=<your client secret>
   ```

   `.env` is gitignored and auto-loaded at startup (your shell environment still wins over it).
4. **Run** from the hub root: `dotnet run --project src/McaHub`. The log should read
   `auth: accounts (github OAuth)`. Visit <http://localhost:5080>, click **Sign in**, then mint a token at
   `/account` for `mcagit push`.

Behind a TLS-terminating reverse proxy, register the `https://…/auth/callback` URL and set
`MCAHUB_BEHIND_PROXY=1` so the hub honors `X-Forwarded-Proto/Host/For` and builds an `https` redirect URI.
Those headers are trusted **only from the proxy** — `MCAHUB_TRUSTED_PROXY` (IP or CIDR, default loopback);
keep the app port itself unreachable from clients, or a spoofed `X-Forwarded-Host` could hijack the OAuth
redirect / clone URLs.

## Operating

Running it for real — what to back up, how to upgrade, and how to keep logs from filling the disk.

### Backup and recovery

| State | Path | Durable? |
|---|---|---|
| Hosted worlds | `data/repos` (`MCAHUB_DATA`) | **Yes** — the only copy of pushed history |
| Accounts DB | `data/hub.json` (`MCAHUB_DB`) | **Yes** — users, hashed tokens, ownership, grants |
| Audit log | `data/audit.jsonl` (`MCAHUB_AUDIT`) | **Yes** (compliance) |
| World cache | `data/cache` (`MCAHUB_CACHE`) | No — re-materialized on demand |
| Map cache | `data/maps` (`MCAHUB_MAPS`) | No — re-rendered on demand |

**Back up `data/repos` + `data/hub.json` (+ `data/audit.jsonl`); skip the caches** — they rebuild
themselves. Both durable stores are **safe to copy while the hub runs**: `hub.json` is written with an
atomic temp-then-rename (a snapshot is never torn), and `data/repos` holds append-only, atomically-published
packs, so an `rsync`/filesystem snapshot is crash-consistent. For a *fully* consistent backup (no
in-flight push mid-pack), **stop the hub first**. A leftover `hub.json.tmp` after a crash is a harmless
orphan — the live `hub.json` is intact; delete the `.tmp`.

**Restore:** stop the hub, drop the backed-up `data/repos` + `data/hub.json` back in place, start it; the
caches refill on first view. **Migrate to a new machine:** copy `data/repos`, `data/hub.json`, and your
`.env`, then update the OAuth app's callback URL to the new host.

### Upgrading

The hub and the engine version independently now — the engine is the `mcagit` **binary**, not a submodule:

```sh
# 1. stop the hub   2. back up hub.json
git pull && dotnet build src/McaHub -c Release    # the hub (C#)
# update the mcagit binary too (cargo build --release in the mcagit repo); keep MCAGIT_BIN on the new one
# 3. start the hub
```

Keep the `mcagit` binary roughly in step with the hub: the hub parses mcagit's `--json` output, so a
breaking CLI/JSON change on the mcagit side needs a matching hub update — pin a known-good pair. An
incompatible `hub.json` from a *newer* build refuses to start with an actionable message (see the
schema-version guard) rather than corrupting your data.

### Logging

The hub logs to stdout. On a busy host, bound it: under systemd/journald set `journalctl`'s
`SystemMaxUse=`, or `logrotate` a redirected log file. To silence per-request access logs in production,
set `Logging__LogLevel__Microsoft.AspNetCore=Warning` (env var). `GET /health` is unauthenticated and
rate-limit-exempt for liveness probes — don't block it at the proxy.

## How it works

The C# layer is the web/auth/accounts shell; all world logic is the Rust **`mcagit`** engine, driven
out-of-process via `RustEngine` (`src/McaHub/RustEngine.cs` — shells the binary + parses `--json`/PNG):

- `RepoStore` — hosts bare mcagit repos at `<data>/<name>` (blake3/zstd format); repo names are validated
  so they can't escape the data dir. Listing/branches/commit-times come from `mcagit log` + `cat-file`.
- `Transport` — keeps mcahub's auth/accounts/throttle/audit gate, then **reverse-proxies** the transport
  protocol (`/r/{repo}/{info-refs,have,objects,refs/heads}`) to a co-located **`mcagit serve`** sidecar
  (started by `Program` on a loopback port). The sidecar speaks mcagit's object/ref protocol (fast-forward
  guard + blake3 hash-verify on store); a first push auto-creates + claims the world.
- `Pages` — server-rendered HTML (no SPA): the repo list, a repo's timeline, a backup view, a
  compare-any-two-backups view, a world explorer, and the time-machine scrubber (`/r/{repo}/timeline`).
  The diff + grief summary come from `mcagit diff --json` + `mcagit where-changed --json` (a backup's
  materialized world vs its parent's); the explorer from `mcagit players/find --json`. Backup data is
  embedded as `System.Text.Json` and captions set via `textContent`, so commit messages can't inject script.
- `WorldCache` — materializes a backup to `cache/<repo>/<commit>` once via `mcagit checkout` (commits are
  immutable) so the dir-based queries/renders read a real world without re-checking-out per page view.
- `MapRenderer` ⇒ **`mcagit render`** + `MapCache` — the top-down surface map per backup is produced by the
  Rust renderer and cached per immutable commit; a cold render shows a "Generating map…" spinner and reveals
  the image once it loads (the scrubber re-shows it each step). Renders run as background jobs off the
  request thread.
- `Auth` + `HubDb` — identity and the tiny JSON account store (unchanged by the engine swap). `Auth` wires the framework's cookie + OAuth
  handlers (no third-party package), splits web identity (cookie) from CLI identity (Bearer PAT), and holds
  the shared `CanRead`/`CanWrite` rules used by both the web pages and the transport. `HubDb` keeps users,
  **hashed** tokens (the plaintext is shown once and never stored), per-repo owner/visibility, collaborator
  grants, and teams + team grants. `HubDb.RoleOf` resolves a user's effective role on a repo
  (owner > admin > maintain > write > read > none) by folding the owner, any direct collaborator grant, and
  any team grant the user inherits — `CanRead`/`CanWrite`, the `CanManageSettings`/`CanManagePeople`
  capability checks, and the UI all go through it, so they can't drift. State-changing POSTs are validated
  with ASP.NET antiforgery tokens (issued via a hidden field, validated manually so the Bearer-authenticated
  transport POSTs are never touched).

## Status & roadmap

Shipped: hosting + browse + per-backup diff + grief forensics, **compare any two backups**, a
**world explorer** (players + find an entity / block entity / sign, backed by a materialize-once world
cache), **rendered maps** + a **time-machine scrubber**, **one-click restore**, **accounts** (OAuth
sign-in, per-user tokens, public/private worlds, collaborators, teams), multi-provider sign-in (Microsoft,
Minecraft, Discord), COPPA age gate, AUP page, abuse-report link + operator takedown, user suspension,
account/world deletion (GDPR/CCPA erasure), per-user world quota, and an **audit log** (`/r/<name>/audit`).
The hub is now **fully ported to the Rust `mcagit` engine** — no in-process .NET core, no submodule.
Natural next steps:

- Re-add the on-push **Discord grief alert** over mcagit's grief output (deferred during the engine port).
- Surface more of mcagit's world tooling in the UI — a coordinate `inspect` page (block + properties +
  biome + block-entity), region heatmaps, per-player inventory views.
- Map thumbnails on the backup timeline, and a focusable region/coordinate jump in the map.

## License

GPL-3.0 (matching mcagit). See [LICENSE](LICENSE).
