---
name: seam-structure
description: Complete mapping of hub↔core protocol routes, guard delegation, and file locations as of 2026-06-05 audit
metadata:
  type: project
---

## Route → RemoteService delegation map

All routes live in `src/McadiffHub/Transport.cs`. Each request constructs a per-request `RemoteService(store.Open(repo), allowWrite)` — exactly the same handler `mcadiff serve` (RepoServer) uses. No hub-side guard reimplementation was found on any route; all guards live in the core.

| Route | Method | Hub handler | Core call |
|---|---|---|---|
| `/r/{repo}/info/refs` | GET | `Transport.cs:23` | `s.ListRefs()` |
| `/r/{repo}/have` | POST | `Transport.cs:31` | `s.Missing(want)` |
| `/r/{repo}/objects/{hash}` | GET | `Transport.cs:39` | `s.GetObject(hash)` |
| `/r/{repo}/objects/{hash}` | POST | `Transport.cs:48` | `s.PutObject(hash, bytes)` |
| `/r/{repo}/pack` | POST | `Transport.cs:52` | `s.PutPack(pack, idx)` |
| `/r/{repo}/refs/heads/{branch}` | POST | `Transport.cs:60` | `s.UpdateRef(branch, old, new, force)` |

## Core guard locations

- **Fast-forward check**: `RemoteProtocol.cs:RemoteService.UpdateRef` — compares old, checks `Transfer.IsAncestor`
- **Pack hash verification**: `ObjectStore.cs:ImportRaw` + `PackTransfer.cs:ImportInto` — SHA-256 of decompressed content must equal hash
- **SafeInflate / decompression bomb**: `ObjectStore.cs:InflateBounded` (private, 512 MB cap) + `SafeInflate.cs` (public API, same cap) + `Packfile.cs:ReadPayload` (per-entry 512 MB cap)
- **NbtDepthGuard**: `Nbt/NbtDepthGuard.cs` — pre-parse non-recursive depth scan, 512 levels max
- **PathGuard.Confine**: `PathGuard.cs` — canonicalized prefix check; called from `Repository.BranchPath`, `TagPath`, `WriteRemoteTracking`, `ReadRemoteRef`
- **ObjectStore.IsValidHash**: 64 lowercase hex chars only — guards `PathFor()` from path traversal via hash param

## Key findings from 2026-06-05 audit (ranked)

1. **CRITICAL — unbounded body buffer in hub** (`Transport.cs:101-106`): `Bytes()` reads the entire request body into a `MemoryStream` with no size cap. `RepoServer` (standalone `mcadiff serve`) caps at 256 MB (`MaxBody = 256L * 1024 * 1024`). Hub has no equivalent cap. A 10 GB POST to `/r/{repo}/pack` or `/r/{repo}/objects/{hash}` OOMs the process before the core's inflate guard runs.

2. **HIGH — `have` body read before auth check** (`Transport.cs:31-35`): The POST body is deserialized before `Readable()` is called. An unauthenticated caller on a private repo can consume server CPU and memory parsing a large JSON array before being rejected.

3. **HIGH — ownership takeover on pre-accounts repos** (`Transport.cs:94`, `Auth.cs:CanWrite`): `CanWrite` returns true when `db.GetRepo(repo)` is null (unowned). Any repo that existed before accounts were enabled is unowned in the DB. Any authenticated user can push to it and claim ownership via `EnsureRepo`. This is the expected migration affordance, but it is a sharp edge: it means enabling OAuth on an existing open hub hands every authenticated user a window to claim any repo.

4. **HIGH — simultaneous first-push race** (`HubDb.cs:EnsureRepo`, `Transport.cs:93-94`): `store.Create(repo)` and `db.EnsureRepo(repo, uid)` are two separate operations with no lock spanning both. Two simultaneous first-pushes to the same name can both pass the `!store.Exists(repo)` check, one will lose `Repository.Init` (it throws `InvalidOperationException` — caught as 400), but `EnsureRepo` is idempotent only within the DB lock. The winner of the filesystem race becomes owner; the loser gets a 400. Net: ownership is consistent but the loser gets a confusing error.

5. **MEDIUM — branch name reaches PathGuard only via core** (`Transport.cs:60-65`): The `{branch}` route parameter is passed directly to `s.UpdateRef(branch, ...)`, which calls `repo.WriteBranch(branch, ...)`, which calls `BranchPath(branch)`, which calls `PathGuard.Confine`. The hub does not validate `branch` before passing it. This is correct delegation (the core guards it), but any change to hub routing that extracts or uses `{branch}` before calling the core would miss the guard. The hub also never validates `{hash}` — `ObjectStore.IsValidHash` is the sole guard, called inside `PathFor()`.

6. **LOW — submodule coupling pins core via gitlink** (`McadiffHub.csproj:15`, `.gitmodules`, ADR-0006): `ProjectReference` to `../../mca-git/src/McaDiff/McaDiff.csproj`; core is vendored as a git submodule at `./mca-git`, gitlink-pinned. CI uses `submodules: recursive`. Core API changes only land via an explicit `git submodule update --remote mca-git` + commit — visible as a one-line gitlink change in PR diffs. No silent core drift; supply-chain narrowed to deliberate bumps. Risk shifted: a submodule bump that lands a breaking core change is now a reviewable PR-time event, not a CI surprise.

7. **LOW — actionlint installer fetched at runtime** (`ci.yml:65`): `bash <(curl ...)` fetches the install script from GitHub at a commit SHA of the script file, then installs a hardcoded version `1.7.12`. The script SHA is pinned but the binary it downloads is not — it fetches from GitHub releases, not a fixed digest. Not a code-execution risk in practice (GitHub releases are content-addressed by version), but inconsistent with the SHA-pinning discipline elsewhere.

8. **LOW — error messages may echo exception text** (`Transport.cs:98`): `catch (Exception e) { return Results.Text(e.Message, statusCode: 400); }` returns the core's exception message verbatim. Core exceptions currently contain repo-internal paths only when something goes very wrong (e.g. a temp file path in an IOException). No stack trace is exposed (ASP.NET default developer exception page is not enabled in production). Low risk, but worth auditing which exception messages contain filesystem paths.

9. **INFO — 404 vs 403 discipline on read routes** (`Transport.cs:32-35, 42`): `Readable()` returning false yields `Results.NotFound()` on all read routes, which correctly hides repo existence from unauthenticated callers. Verified correct.

## Submodule coupling verdict

The core is a git submodule at `./mca-git` (see [ADR-0006](../../../docs/adr/0006-mcadiff-submodule.md),
superseding [ADR-0003](../../../docs/adr/0003-sibling-mcadiff-core-coupling.md)). The gitlink in the
parent repo's tree pins the exact commit; local and CI builds resolve against the same SHA by
construction. Core API changes land at the hub's pace, via an explicit `git submodule update --remote
mca-git` + commit, shown as a one-line gitlink change in PR diffs. Compile-time coupling is preserved
(a core breakage at bump time is loud), but stealth drift is eliminated. Supply-chain risk further
narrowed: CI no longer fetches the core's `main` at run time.

End-state per ADR-0003 / ADR-0006: publish the core as a versioned NuGet and replace the submodule
with a `PackageReference` once the core API stabilizes. Until then, the submodule is the bridge.
