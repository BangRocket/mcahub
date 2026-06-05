# ADR-0005: Modular monolith over microservices

- **Status:** Accepted
- **Date:** 2026-06-05

## Context

The hub aspires to be a "GitHub for Minecraft worlds," and the analogy invites a
microservices architecture — the promise being independent deployability and isolating
in-flight work (a long render or materialize) from a deploy of an unrelated part.

But microservices is fundamentally an **organizational and scale** solution: it pays off
when independent teams must deploy independently and hot paths must scale separately. The
hub is a **small-team, self-hostable single binary** whose number-one adoption lever is
"zero-config, run it on a LAN." Its surfaces (web UI, transport, renderer) form **one
bounded context** — a hosted versioned world — over **one shared state** (repos +
`hub.json` + caches); `HubDb.RoleOf` is consulted by all three. Splitting them would force
either a shared database (a distributed monolith — the worst of both worlds) or duplicated
state (consistency bugs), while imposing the full distributed-systems tax (network calls,
partial failure, versioned contracts, cross-service observability) for benefits that need
org scale to materialize.

Critically, microservices does **not by itself** solve the stated concern: deploying a
"render service" still kills its in-flight renders unless you *also* add graceful draining
and resumable jobs — which are free in a monolith.

## Decision

We will remain a **modular monolith** — one process with clean internal module boundaries
(`Auth` / `Transport` / `Pages` / `HubDb` / `RepoStore` / `Map*`). We achieve the desired
properties without splitting the binary:

- **Graceful shutdown / draining** so a deploy lets in-flight requests finish.
- **A background job queue** for heavy, idempotent, resumable work (render / materialize),
  so the request path never blocks and a restart simply re-picks-up the job.
- **In-process fault isolation** around heavy work (timeouts + a render concurrency
  semaphore + catch-all error handling) so one bad render returns a 500, not a process kill.
- **Multi-instance-safe store locking** so two instances can run behind the reverse proxy
  for zero-downtime rolling deploys.

The one sanctioned future extraction is the **render/materialize worker** as a separate
process (same repo, shared filesystem) — and only when a profiler shows the need, because it
is the single piece with a genuinely independent (CPU/RAM-heavy, batch-shaped) profile.

## Consequences

- **Positive:** preserves the self-host single-binary promise; fastest path to ship a fix;
  no distributed-systems tax; the properties the team actually wants (independent deploy
  feel, no interrupted work) are delivered by drain + resumable jobs.
- **Negative:** a catastrophic in-process fault (e.g. a render OOM) can take the whole app
  down until the worker is extracted and resource bounds are added; the render hot path
  can't scale independently until that extraction.
- **Neutral / follow-ups:** the enabling work (graceful drain, background render/materialize
  queue, multi-instance store locking) and the render-bounding work are tracked as issues.
  Revisit a database + further service boundaries only at real multi-tenant SaaS scale.
