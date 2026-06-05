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
a **process-wide lock** with **atomic tmp-file + rename** writes. `HubDb.RoleOf` is the one
source of truth for authorization, consumed by the web pages and the transport alike. No
database, no ORM.

## Consequences

- **Positive:** zero-config — there is nothing to install, migrate, or operate beyond the
  process and a data directory; writes are crash-consistent (atomic rename); backup is
  "copy the data directory" (see the operability runbook work).
- **Negative:**
  - **Single-process write model.** Two instances cannot safely share `hub.json` without
    cross-process file locking; this is the one real blocker to naive horizontal scaling and
    is addressed deliberately in [ADR-0005](0005-modular-monolith-over-microservices.md).
  - **No schema version** yet — a future field rename could silently drop data on upgrade
    (tracked as an issue).
  - Not suited to thousands of concurrent writers.
- **Neutral / follow-ups:** revisit a real database only at genuine multi-tenant SaaS scale,
  driven by measured contention — not in anticipation.
