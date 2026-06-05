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

6. **MEDIUM — sibling coupling floats to core main** (`McadiffHub.csproj:15`, `ci.yml:88-90`): `ProjectReference` to `../../../mca-git/src/McaDiff/McaDiff.csproj`; CI checks out `BangRocket/mcadiff@main` with no ref pin. A breaking core API change silently breaks hub builds at next CI run. Supply-chain: CI executes code from the core's main branch — currently acceptable (same owner, read-only token), but no explicit pin strategy exists.

7. **LOW — actionlint installer fetched at runtime** (`ci.yml:65`): `bash <(curl ...)` fetches the install script from GitHub at a commit SHA of the script file, then installs a hardcoded version `1.7.12`. The script SHA is pinned but the binary it downloads is not — it fetches from GitHub releases, not a fixed digest. Not a code-execution risk in practice (GitHub releases are content-addressed by version), but inconsistent with the SHA-pinning discipline elsewhere.

8. **LOW — error messages may echo exception text** (`Transport.cs:98`): `catch (Exception e) { return Results.Text(e.Message, statusCode: 400); }` returns the core's exception message verbatim. Core exceptions currently contain repo-internal paths only when something goes very wrong (e.g. a temp file path in an IOException). No stack trace is exposed (ASP.NET default developer exception page is not enabled in production). Low risk, but worth auditing which exception messages contain filesystem paths.

9. **INFO — 404 vs 403 discipline on read routes** (`Transport.cs:32-35, 42`): `Readable()` returning false yields `Results.NotFound()` on all read routes, which correctly hides repo existence from unauthenticated callers. Verified correct.

## Sibling coupling verdict

The `ProjectReference` floating to core main is a deliberate signal-capture strategy (breaks here rather than at local build). It is acceptable while both repos share an owner. The risk is: a core API change (renamed method, changed signature) will break hub CI immediately and visibly — which is the intended behavior. There is no stealth version-skew because the coupling is compile-time. Supply-chain risk is limited to the read-only CI token and same-owner trust.

Pin strategy if owner trust changes: add `ref: <sha>` to the `mca-git` checkout step. This trades break-signal for supply-chain isolation. A middle ground: pin to a tag (`ref: v1.2.3`) and use Dependabot's `nuget` ecosystem (NuGet package instead of ProjectReference) to get automated bumps with code review.
