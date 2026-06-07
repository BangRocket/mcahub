---
name: smoke
description: End-to-end smoke test of the hub — build it, launch it isolated, push a real world at it with the mcadiff CLI, walk the web pages, verify the map renders, tear down. Use when asked to verify the hub works, smoke-test a change, or confirm a feature end-to-end before pushing.
---

# /smoke — end-to-end hub verification

Run the full loop a real user runs: serve, push, browse, render. Every step asserts; report a
pass/fail table at the end. Total runtime is a couple of minutes (the cold map render of the
~2,600-chunk sample world takes several seconds on its own).

## Prerequisites

- The `mca-git` submodule initialized at `<repo>/mca-git`. If empty, stop and say so
  (`git submodule update --init` fixes it) — nothing builds without it.
- The real sample world `<repo>/mca-git/compare-worlds/New_World_Older` (1.21.1, ~2,600 chunks).

## Setup — isolation matters

1. Create a scratch dir (e.g. `$env:TEMP/hub-smoke-<random>`). Everything lives under it:
   `data/`, `cache/`, `maps/`, `hub.json`, the CLI repo, and both published binaries.
2. **Publish, don't `dotnet run`.** Publish once each from the repo root — the hub
   (`dotnet publish src/McaHub -c Release -o <scratch>/hub-bin`) and the core CLI
   (`dotnet publish mca-git/src/McaDiff -c Release -o <scratch>/cli-bin`). The CLI gets invoked
   repeatedly; `dotnet run` pays MSBuild evaluation every time and its working-directory
   behavior has already caused one phantom 0-chunk bug here.
3. **Launch the hub from inside the scratch dir** (run_in_background), with:
   - `ASPNETCORE_URLS=http://localhost:5099` — never 5080, a dev instance may be running.
   - `MCAHUB_DATA=<scratch>/data/repos`, `MCAHUB_CACHE`, `MCAHUB_MAPS`, `MCAHUB_DB` likewise.
   - cwd = scratch dir. This is load-bearing: `Program.cs` auto-loads `.env` from the *current
     directory*, and the repo root's `.env` may hold OAuth creds that would flip the hub into
     accounts mode and 401 every anonymous push. From the scratch dir there is no `.env`, so the
     hub starts in open mode — confirm the startup log says `auth: open push`.
4. Poll `http://localhost:5099/` until it answers (or ~15s timeout).

## The gauntlet

Use the published CLI (`<scratch>/cli-bin/mcadiff`) and curl/Invoke-WebRequest. Assert each step:

1. **init + commit**: `mcadiff init <scratch>/smoke.mcagit --worktree <repo>/mca-git/compare-worlds/New_World_Older`,
   set `config user.name` / `user.email`, `commit -m "smoke backup"` — exit 0 each.
2. **push auto-creates**: `mcadiff -C <scratch>/smoke.mcagit push http://localhost:5099/r/smoke main`
   — exit 0. This exercises the whole transport write path (`info/refs`, `have`, `objects`, `pack`,
   `refs/heads/main`).
3. **repo list**: GET `/` → 200, body contains `smoke`.
4. **repo page**: GET `/r/smoke` → 200, body contains the commit message. Extract the commit hash
   from the page (or `mcadiff -C ... log`).
5. **backup view**: GET `/r/smoke/commit/<hash>` → 200 (diff + grief summary render).
6. **world explorer**: GET `/r/smoke/world/main` → 200 (this forces a WorldCache materialize —
   the slow, security-interesting path).
7. **map**: GET `/r/smoke/map/main.png` with a generous timeout (60s — cold render) → 200,
   body starts with the PNG signature `89 50 4E 47`, and is well over the ~100-byte empty-map
   size (the sample world renders 816×816).
8. **timeline**: GET `/r/smoke/timeline` → 200.
9. **clone round-trip** (proves reads, not just writes): `mcadiff clone http://localhost:5099/r/smoke
   <scratch>/clone.mcagit` → exit 0.
10. **404 shape**: GET `/r/does-not-exist` → 404 (and on the transport: `/r/does-not-exist/info/refs` → 404).

## Teardown — always, even on failure

Kill the hub process, then delete the scratch dir. Never leave a server bound to 5099.

## Report

A table: step → expected → actual → ✅/❌. On any failure include the hub's console output (it
runs with a captured log — read it before killing). If the failure smells like a trust-boundary
issue, suggest a follow-up with the hub-security-adversary agent rather than diagnosing inline.
