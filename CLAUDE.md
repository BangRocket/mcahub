# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A self-hostable "GitHub for Minecraft worlds": hosts [`mcadiff`](https://github.com/BangRocket/mcadiff)
world repositories over HTTP (clone/fetch/push) and serves a server-rendered web UI for browsing backup
timelines, semantic diffs, grief forensics, rendered maps, and a world explorer.

## Hard dependency: mcadiff submodule

The single project references the mcadiff core **in-process** via a git submodule at `./mca-git`:
`src/McadiffHub/McadiffHub.csproj` → `..\..\mca-git\src\McaDiff\McaDiff.csproj`. A plain
`git clone` of this repo leaves the submodule empty and the build fails with CS0246 errors
(`Repository`, `RemoteService`, etc. not found). Clone with `--recurse-submodules`, or run
`git submodule update --init` after cloning. To pull upstream mcadiff changes later:
`git submodule update --remote mca-git`.

Types like `Repository`, `RemoteService`, `RepoDiffer`, `GriefReport`, `WorldQuery`, `Checkout`,
`BlockStateDecoder` all come from that core project.

## Commands

```sh
dotnet build src/McadiffHub                          # build (.NET 10 SDK required)
dotnet run --project src/McadiffHub                  # serve http://localhost:5080
dotnet run --project src/McadiffHub -- render <worldDir> <out.png>   # offline map render, no server
```

There is **no test project** in this repo (the mcadiff core has its own tests in its repo).

Configuration is entirely env vars (`MCAHUB_DATA`, `MCAHUB_CACHE`, `MCAHUB_DB`, `MCAHUB_TOKEN`,
`MCAHUB_OAUTH_*`, `MCAHUB_DEV_LOGIN`, `MCAHUB_BEHIND_PROXY`) plus a `.env` file auto-loaded at startup
(real env always wins — see `LoadDotEnv` in `Program.cs`). `.env.example` documents them; the README
has the full table. For exercising accounts locally without an OAuth app: `MCAHUB_DEV_LOGIN=1` then
sign in at `/auth/dev`.

## Architecture

One ASP.NET Core project (`src/McadiffHub/`, ~3150 lines across 19 files, no NuGet packages beyond the
framework — even the PNG encoder is hand-rolled). `Program.cs` wires everything; each file is one subsystem:

- **`Transport.cs`** — maps the mcadiff HTTP protocol (`/r/{repo}/info/refs`, `/objects`, `/pack`,
  `/refs/heads/{branch}`, `/have`) onto a per-request core `RemoteService`. Every GET goes through
  `Readable`, every write through `Write` (Bearer auth + `CanWrite` + auto-create on first push +
  ownership claim-on-first-push in accounts mode). Fast-forward checks and pack hash verification
  happen in the core, not here.
- **`Pages.cs`** — all web UI as server-rendered HTML (no SPA, no JS framework). Repo list, timeline,
  per-backup diff + grief summary, compare-any-two-backups, world explorer, map endpoint
  (`/r/{repo}/map/{ref}.png`), time-machine scrubber, account/teams pages. Embedded JSON uses
  `System.Text.Json`; user text goes through `textContent` to avoid script injection.
- **`Auth.cs`** — the crux of security. Three modes chosen by config: **open** (nothing set),
  **token** (`MCAHUB_TOKEN`), **accounts** (OAuth client id+secret set). Wires framework cookie +
  OAuth handlers directly (no third-party auth package); splits web identity (cookie) from CLI
  identity (Bearer PAT). Holds the access predicates `CanRead`/`CanWrite`/`CanManageSettings`/
  `CanManagePeople` and the CSRF helpers (`CsrfField`/`CsrfOk` — validated manually so Bearer-only
  transport POSTs are exempt) and the open-redirect guard `Local`.
- **`HubDb.cs`** — the account store: a single JSON file (`hub.json`) guarded by a process-wide lock
  with atomic tmp+rename writes. Users, **SHA-256-hashed** PATs (`mcahub_` prefix, plaintext shown
  once), repo owner/visibility, collaborators, teams. **`RoleOf` is the single source of truth for
  authorization** — it folds owner > admin > maintain > write > read across owner, direct
  collaborator grants, and team grants; transport, pages, and UI rendering all route through it so
  they can't drift. Keep it that way: never add an ad-hoc permission check.
- **`RepoStore.cs`** — bare repos under the data dir; `IsValidName`
  (`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`) is the path-traversal guard for repo names — every path is
  built only after validation.
- **`WorldCache.cs` / `MapCache.cs` / `MapRenderer.cs`** — caches are keyed by commit hash and never
  invalidated because **commits are immutable**: a backup is materialized to `cache/<repo>/<commit>`
  once, a map rendered to PNG once. `MapRenderer` scans 1.18+ `sections`/`block_states` top-down for
  the first non-air block, colors + height-shades it, and writes the PNG itself (`ZLibStream` + a
  CRC32). Render span is capped at 160×160 chunks.
- **`AuditLog.cs`** — append-only JSONL trail of role/visibility/ownership/ref/token changes; surfaced
  at `/r/<name>/audit` (owners/admins only).
- **`AgeGate.cs`** — COPPA-style gate: when `MCAHUB_MIN_AGE_GATE=1`, any signed-in user who hasn't
  confirmed 13+/parental consent is bounced to `/auth/age-gate` before any other page serves.
- **`AuthThrottle.cs`** — bad-token lockout: tracks bad Bearer tokens per IP and imposes a doubling
  cooldown once `MCAHUB_AUTH_MAX_FAILURES` is exceeded.
- **`StartupGuard.cs`** — refuses to start if open mode or `MCAHUB_DEV_LOGIN` is exposed on a
  non-loopback interface without an explicit override.
- **`ForwardedProxies.cs`** — applies `X-Forwarded-*` headers when `MCAHUB_BEHIND_PROXY=1`, scoped to
  `MCAHUB_TRUSTED_PROXY` (default loopback) to prevent header spoofing.
- **`MinecraftAuth.cs`** — the Xbox→XSTS→Minecraft Services token chain used in Minecraft OAuth
  `OnCreatingTicket`; intermediate tokens are used transiently and never persisted.

## Security invariants to preserve

`SECURITY.md` is a reviewer's guide to the full trust-boundary map and known soft spots (DoS/resource
exhaustion is the acknowledged big one). When touching code, the invariants that must not regress:

- Private repos return **404, not 403**, on both web pages and transport — existence is hidden.
- All authz decisions go through `HubDb.RoleOf` / the `Auth.Can*` predicates.
- Token plaintext is never stored or logged; lookups are by hash; master token compares in constant time.
- Every cookie-authenticated state-changing POST validates an antiforgery token; Bearer-authenticated
  transport POSTs are intentionally outside that path (the CLI must keep working).
- Repo names are validated before any filesystem path is constructed.
- Untrusted world data (NBT, manifests, packs) is only parsed via the core's guarded paths
  (`SafeInflate`, `NbtDepthGuard`, `PathGuard.Confine`) — never reach around them.
