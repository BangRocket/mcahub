---
name: project-deployment-shape
description: Deployment shape, submodule coupling (post ADR-0006), default paths, auth modes, and operator-relevant env vars for mcahub
metadata:
  type: project
---

## Submodule coupling (per ADR-0006, superseding ADR-0003)
- `src/McaHub/McaHub.csproj` has a `ProjectReference` to `../../mca-git/src/McaDiff/McaDiff.csproj`
- The mcadiff core is a git submodule at `./mca-git` of the hub repo, gitlink-pinned
- Build failure without it (i.e., when a plain `git clone` leaves the submodule empty): ~26 CS0246 errors (Repository, RemoteService, etc. not found) — no helpful error message; fix is `git submodule update --init` or re-clone with `--recurse-submodules`
- `McaHub.slnx` also references `../mca-git/src/McaDiff/McaDiff.csproj`
- CI uses `actions/checkout` with `submodules: recursive`; README leads with `git clone --recurse-submodules` and CLAUDE.md restates the requirement

## Default paths (Program.cs lines 21-25)
- `MCAHUB_DATA` → `data/repos` (relative to cwd at startup)
- `MCAHUB_CACHE` → parent-of(dataDir)/cache = `data/cache` (README misleadingly says "sibling cache/")
- `MCAHUB_MAPS` → parent-of(dataDir)/maps = `data/maps` (MISSING from README table and .env.example)
- `MCAHUB_DB` → parent-of(dataDir)/hub.json = `data/hub.json` (README misleadingly says "sibling hub.json")

## Auth modes
- Open (default): anonymous, no env vars set
- Token: MCAHUB_TOKEN set — gates writes only
- Accounts: MCAHUB_OAUTH_CLIENT_ID + MCAHUB_OAUTH_CLIENT_SECRET both set — real users, PATs
- MCAHUB_DEV_LOGIN=1: enables insecure /auth/dev endpoint, activates accounts mode without OAuth

## Key operational notes
- No /health or readiness endpoint exists
- No appsettings.json — all config via env vars or .env file
- hub.json has NO schema version field — silent breakage risk on upgrade
- hub.json Save() (HubDb.cs:287-293) does atomic tmp+rename but has NO exception handling — disk-full throws IOException uncaught, potentially crashing the process mid-request
- data/repos contains bare .mcagit repos — the system of record for world history
- data/cache and data/maps are derived/reconstructible but can be very large
- Single process-wide lock (_lock in HubDb) serializes all reads and writes to hub.json
- WorldCache and MapCache: unbounded growth, no eviction, no quota
- No log rotation configured; ASP.NET Core defaults to stdout only
- Transport.cs:101-106 buffers entire push body into memory (MemoryStream) before processing
