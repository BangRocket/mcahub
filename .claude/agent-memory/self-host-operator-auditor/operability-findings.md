---
name: operability-findings
description: All filed operator operability findings for mcahub, with severity and defuser notes
metadata:
  type: project
---

## Net-new findings filed in operability audit (2026-06-05)

### OP-1 hub.json: no schema version, silent breakage on upgrade (BLOCKER)
- HubDb.cs has no SchemaVersion field in its Db record
- Future field additions use C# record defaults (safe), but field removals or renames silently drop data
- Fix: add `public int SchemaVersion { get; init; } = 1;` to Db record + migration note in upgrade docs

### OP-2 hub.json: disk-full throws uncaught IOException, process may crash (BLOCKER)
- HubDb.Save() (lines 287-293) calls File.WriteAllBytes and File.Move with no try/catch
- On disk-full, the tmp file write throws IOException that propagates up through the holding lock
- The live hub.json is safe (atomic rename never completes) but the request fails with 500 and the lock releases
- Fix: wrap Save() in try/catch, log the error, and return a 507/503 to the caller

### OP-3 No /health or readiness endpoint (BLOCKER for proxy/orchestrator)
- No health check endpoint exists anywhere in Program.cs or Pages.cs
- Operators cannot configure nginx upstream health checks, systemd watchdog, or k8s liveness probes
- Fix: app.MapGet("/health", () => Results.Ok("ok")) — one line in Program.cs

### OP-4 No backup/restore/migration runbook for hub.json or data/repos (BLOCKER)
- hub.json is the account/permission store; data/repos is the world history store
- No documentation covers: how to back up, how to restore, how to migrate between versions
- No guidance on whether to stop the hub before backing up data/repos (bare git repos mid-push)
- Fix: add a "Backup and recovery" section to README

### OP-5 CS0246 install failure gives no diagnostic (FRICTION) — partially mitigated by ADR-0006 submodule
- Post-ADR-0006: a plain `git clone` (without `--recurse-submodules`) leaves `./mca-git` empty and `dotnet build` still gives ~26 CS0246 errors with no mention of the missing submodule
- README now leads with `git clone --recurse-submodules` and CLAUDE.md spells out the fix (`git submodule update --init`), which is a real improvement over the pre-ADR-0006 sibling-clone model
- The hard failure mode is narrower (only operators who skip the README) but the error message is still cryptic — a startup/build-time guard with an actionable message ("./mca-git submodule not initialized; run `git submodule update --init`") would close the gap

### OP-6 No log rotation; stdout-only logging with no guidance (FRICTION)
- No appsettings.json exists; ASP.NET Core defaults to console/stdout
- Long-running process will fill /var/log or systemd journal without operator guidance
- Fix: document that operators should configure journald limits or pipe stdout to a rotating logger

### OP-7 entire push body buffered into memory (FRICTION/DoS assist)
- Transport.cs:101-106: `await req.Body.CopyToAsync(ms)` with no size cap before processing
- A 310k-chunk world acknowledged as pegging the host; full body is held in RAM before core processes it
- Worsens the DoS surface noted in #5; makes OOM a direct consequence of a large push
- Fix: cap CopyToAsync at a configurable body size limit; reject with 413 if exceeded

### OP-8 MCAHUB_MAPS missing from README and .env.example (PAPERCUT)
- Program.cs:24 reads MCAHUB_MAPS but it appears in neither the README config table nor .env.example
- An operator who wants to relocate the map cache has no documented way to do it
- Fix: add MCAHUB_MAPS row to README table and a commented line to .env.example (already flagged in #21, but not the MAPS var specifically)

### OP-9 Upgrade path: no documented process for pulling a new version (PAPERCUT)
- No upgrade runbook: stop service → git pull → dotnet build → restart
- No note about whether hub.json is forward-compatible after the upgrade
- Fix: one paragraph in README under a "Upgrading" heading

## Existing issues to strengthen

### Strengthen #5 (disk quota)
- Add: push body is fully buffered in RAM before disk write (Transport.cs:101-106); a request-level body-size cap would protect both memory and disk simultaneously

### Strengthen #21 (undocumented vars / misleading wording)
- Add MCAHUB_MAPS to the fix scope — it is missing from both README and .env.example
- README "sibling cache/" and "sibling hub.json" wording is actively misleading (means parent-of-dataDir, not sibling of the repo root); fix to show literal defaults `data/cache` and `data/hub.json`

### Strengthen #22 (Docker/self-contained binary)
- Add: include a SchemaVersion field in hub.json Db record so a containerized upgrade can detect and migrate the on-disk format rather than silently misreading it
- Add: document which directories to mount as volumes (data/repos, data/hub.json) vs which are reconstructible caches (data/cache, data/maps) — important for image sizing and backup scope
