# Porting mcahub's core to the Rust mcagit engine

mcahub was built against the **.NET** mcagit core (vendored at `mcagit/`, SHA-256/zlib
objects, in-process `WorldDiff`/`GriefReport`/`MapRenderer`). mcagit is now **Rust** (blake3/zstd,
a different object format), so the .NET core can neither store nor render Rust-format worlds. This
is the plan to swap mcahub's engine to the Rust `mcagit` binary while keeping mcahub's web/auth/
accounts layer intact.

## What the Rust engine already provides (done, upstream in mcagit)

| Need | mcagit command |
|------|----------------|
| Transport (host repos) | `mcagit serve <dir> [--addr]` — `/r/<name>/` hub protocol, push auto-creates, token-gated, FF-guarded |
| Clone/push/fetch client | `clone`/`push`/`pull`/`fetch`/`ls-remote http://…/r/<name>` |
| Materialize a backup | `mcagit -C <repo> checkout <commit> <out>` |
| Semantic diff | `mcagit diff A B --json` → `{files:[{path,status,chunks:[{x,z,status,changes,blockEdits}],nodeChanges}]}` |
| Grief forensics | `mcagit where-changed <old> <new> --json` → `[{x,y,z,old,new}]` (→ destroyed/placed/replaced) |
| World explorer | `mcagit players|find|inspect … --json` |
| Top-down map | `mcagit render <world> -o map.png [--dim] [--max-chunks]` |

## The bridge (done, this branch)

`src/McaHub/RustEngine.cs` (`namespace McaHub.Rust`) shells the `mcagit` binary and parses its
`--json`/PNG output into C# DTOs: `Checkout`, `RevParse`, `Log`, `Diff`→`DiffResult`,
`WhereChanged`→`BlockChange[]`, `Grief`→`GriefSummary` (computed in C# from block changes),
`Players`, `Find`, `Render`→`byte[]`. Binary path via `MCAGIT_BIN` (default `mcagit` on PATH).
Compiles additively alongside the .NET core (namespaced to avoid type collisions).

## File-by-file rewire (remaining)

Order matters: do storage + transport first (so Rust-format repos exist), then rendering.

1. **`RepoStore.cs`** — repos become Rust bare repos (dirs). `Open`/`Create` → `mcagit init`;
   `Exists` → dir is a mcagit repo (has `objects/` + `refs/`); `List`/`RepoSummary` →
   `RustEngine.Log` (last message/when) + branch count from `refs/heads`. Drop the `Repository`
   type. The transport `RemoteService` moves out (see #2).
2. **`Transport.cs`** — keep the auth/accounts/throttle gate; replace `RemoteService` with a
   **reverse proxy** of `/r/{repo}/{info/refs,have,objects/*,refs/heads/*}` to a co-located
   sidecar `mcagit serve <data>/repos --addr 127.0.0.1:<port>` (started in `Program.cs`). The
   sidecar handles the blake3/zstd object protocol; mcahub forwards the body + relays status.
   (`/pack` can 404 — the Rust client falls back to per-object, which the proxy already covers.)
3. **`WorldCache.cs`** — materialize a commit via `RustEngine.Checkout(repoDir, commit, outDir)`
   instead of the in-process checkout. Keep the existing on-disk materialization cache/quota.
4. **`Pages.cs`** —
   - `CommitDiff`/`RefDiff`: checkout both sides (WorldCache) → `RustEngine.Diff` → `DiffResult`.
   - `GriefReport.Analyze` → `RustEngine.Grief` → `GriefSummary` (`Destroyed/Placed/Replaced/Box/
     TopDestroyed`). Update `RenderGrief`/`RenderDiff` to the new DTO field names
     (`FileDiff.Path/Chunks/NodeChanges`, `ChunkDiff.X/Z/Changes/BlockEdits`).
   - World explorer + upload preview → `RustEngine.Players/Find/Render`.
5. **`MapRenderer.cs` / `MapCache.cs`** — delete the .NET renderer; `MapCache` calls
   `RustEngine.Render`. (The Rust renderer is a faithful port of the same palette + north-shading.)
6. **`DiscordWebhook.cs`** — grief alert fields from `RustEngine.Grief`.
7. **Remove** the `mcagit` `ProjectReference` from `McaHub.csproj` + drop the `mcagit`
   submodule once nothing references it.

## Tests

The test suite references `RepoStore`/`mcagit` types heavily. After the rewire, the transport
tests exercise the proxy against a real `mcagit serve`; the render/diff/grief tests assert on the
new DTOs (build a synthetic Rust repo via the `mcagit` CLI in a fixture helper, not in-process).
Plan a `RustEngineTests` that round-trips a tiny world: `init → commit → checkout → diff/render`.

## Open questions

- **Sidecar vs in-proc transport:** the reverse proxy is simplest and keeps mcahub language-pure.
  An alternative is a thin FFI `cdylib`, deferred unless proxy latency matters.
- **Diff performance:** `diff`/`grief` checkout both backups to temp. Cache materialized worlds
  (WorldCache already does) and key the diff result by `(repo, a, b)`.
