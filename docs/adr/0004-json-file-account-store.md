# ADR-0004: A single JSON file is the account store

- **Status:** Accepted
- **Date:** 2026-06-05

## Context

A self-hostable tool should not require a database to stand up. The hub's account state is
small and low-churn: users, SHA-256 token hashes, per-repo ownership/visibility,
collaborators, and teams. World data itself is already bare repos on the filesystem
(`RepoStore`) plus derived caches (`WorldCache` / `MapCache`).

## Decision

We will persist account state as a **single JSON file** (`hub.json`) via `HubDb`, guarded by
an **in-process lock** *and* a **cross-process advisory file lock** (`hub.json.lock`) with
**atomic tmp-file + rename** writes. Each write reloads the store under the lock before mutating
(so a second instance never clobbers the first's committed change); reads reload only when the
file changed underneath them. `HubDb.RoleOf` is the one source of truth for authorization,
consumed by the web pages and the transport alike. No database, no ORM.

## Consequences

- **Positive:** zero-config — there is nothing to install, migrate, or operate beyond the
  process and a data directory; writes are crash-consistent (atomic rename); backup is
  "copy the data directory" (see the operability runbook work).
- **Multi-instance safety (resolved, #41).** Two instances *can* share `hub.json` during a rolling
    deploy: the cross-process lock serializes the read-modify-write so a concurrent writer can never tear
    or clobber the store, and reload-before-write keeps the retiring instance from overwriting the new
    one's commits with a stale copy. Reads reload-if-changed, so a revoked token / changed grant is seen
    across instances on the next read. The original blocker noted here and in
    [ADR-0005](0005-modular-monolith-over-microservices.md) is closed.
- **Negative:**
  - **Last-writer-wins on a true write conflict.** If both instances mutate the *same* field within the
    overlap window, one logical update can be lost (the store stays well-formed). Acceptable given the
    retiring instance writes little; not suited to thousands of concurrent writers.
  - **No schema version** — *resolved (#32):* `hub.json` carries a `SchemaVersion` and a newer file is
    refused rather than silently downgraded.
- **Neutral / follow-ups:** revisit a real database only at genuine multi-tenant SaaS scale,
  driven by measured contention — not in anticipation.
