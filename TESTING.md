# Testing guide

mcadiff-hub has effectively no tests yet beyond a single CI render-smoke. This document is
the **strategy** we're building toward, not a description of what exists. The test project is
stood up in its own issue; the suites below are filed against it. It's written now so that
every test added grows in the same direction.

The hub accepts untrusted input from two directions — the network (anyone who can
`mcadiff clone/fetch/push`) and the world data itself (attacker-controlled NBT) — and its
correctness lives in trust boundaries (authz, CSRF, path validation) and byte-exact surfaces
(the wire protocol, the hand-rolled PNG encoder). That shape dictates the testing approach.

## Principles

1. **Pick the test type for the bug shape.** The taxonomy and picker are below.
2. **Pin the bug, not the happy path.** A regression guard must *fail if the fix is reverted*.
   If `git revert`-ing the fix leaves the test green, it's a happy-path test, not a guard.
3. **Tighten assertions.** `== 1` beats `>= 1`; the exact HTTP status beats "not 200";
   **byte-exact** beats "length > 0". (Today's render-smoke only checks "non-empty PNG / >0
   chunks" — that's a length tautology; a real test asserts the PNG signature + IHDR bytes.)
4. **Don't trust fixture data.** Don't hard-code a commit hash or a rendered PNG size — derive
   it from the synthesized fixture, or assert by relationship.
5. **Name the test by what it asserts**, and keep one seam per test.
6. **Cover one feature from several angles.** Each layer catches a different bug class;
   skipping a layer because "the next one up will catch it" is the exact failure this prevents.

## The angles we use

Mapped to the hub's real surfaces. The mcadiff **core** has its own test suite — don't test
the core from here; test the hub's *use* of it.

| Angle | Hub surface | Pins |
|---|---|---|
| **Unit** | `RoleOf`, `IsValidName`, `BlockStateDecoder`, the PNG encoder helpers, `LoadDotEnv`, the OIDC `OnCreatingTicket` mappers, XErr handling | pure logic |
| **Byte-exact (wire-format)** | the hand-rolled PNG encoder (signature / chunk framing / CRC32 / IHDR) and the mcadiff transport frames a real CLI must accept | exact bytes, never "length ≥ N" |
| **Store-state guard** | `HubDb` against a temp file: `RoleOf` folding (owner vs direct-collab vs team grant), `EnsureRepo` idempotency, token hash/resolve, collaborator/team grants | the JSON-store invariants |
| **Response-shape** | the authz **status matrix** (every role × endpoint), **404-not-403** for private repos, CSRF reject, security headers | which response to which (role, request) |
| **Round-trip** | `mcadiff push` → `clone` end-to-end through `WebApplicationFactory` + the core's `RemoteOps`, incl. auto-create-and-claim on first push | the assembled transport pipeline |
| **Concurrency** | `HubDb` writes under concurrent push; the simultaneous-first-push race; the render cache lock | TOCTOU / lost updates / races |
| **Adversarial / malformed** | hostile NBT to the renderer (negative section-Y, malformed palette, huge spans); oversized / chunked / truncated push bodies; malformed protocol requests | bounds & graceful failure under hostile input |
| **Negative-log guard** | when a silent `catch{}` becomes a structured log (e.g. `HubDb.Save` disk-full, bad-token, render failure), pin the log so a revert is caught | silent error-swallow reverts |
| **Client-replay** *(later)* | record a real `mcadiff push` byte stream, replay it against a fresh hub, diff observable behaviour | protocol drift vs the floating core |

## Choosing a test type

| If you're testing… | Use… |
|---|---|
| A pure function / state machine (`RoleOf`, `IsValidName`, a decoder) | Unit |
| A byte producer/consumer (the PNG encoder, a transport frame) | Byte-exact wire-format |
| A `RoleOf` / grant / visibility / token invariant in `HubDb` | Store-state guard (temp-file `HubDb`) |
| Which HTTP status / headers / body a role+request yields | Response-shape |
| An invariant that spans two endpoints (the 404-vs-403 leak only shows in the matrix) | Response-shape / authz matrix |
| `push` / `clone` correctness end-to-end | Round-trip harness |
| A race (concurrent push, first-push, render lock) | Concurrency guard |
| Hostile NBT / oversized body / malformed protocol | Adversarial test (seeded, reproducible) |
| Promoting a silent error-swallow to a queryable log | Negative-log guard |

## One feature, multiple angles

A new transport endpoint or authorization rule lands with **all** of:

1. a **unit** test on the `RoleOf` predicate it depends on,
2. a **response-shape** test for every role on the ladder, including the **404-not-403** cells,
3. a **round-trip** test proving `mcadiff push/clone` still works,
4. a **concurrency** guard if it touches `HubDb` or a cache,
5. and a **negative-log** guard if it added an error path.

## The harness

Integration tests run on `Microsoft.AspNetCore.Mvc.Testing`'s `WebApplicationFactory<Program>`
(requires `public partial class Program {}` in `Program.cs`). Two pieces make authz testable
without a live OAuth provider:

- **A per-test temp data dir + `HubDb`.** Override `DataDir`/`DbPath`/`CacheDir`/`MapDir` to
  Guid-suffixed temp paths in the factory; seed users/repos/roles by constructing `HubDb`
  directly. No mocks of `RoleOf`/`CanRead` — a stubbed predicate only tests the stub.
- **A header-driven fake auth handler.** A tiny `AuthenticationHandler` reads identity from a
  test header and emits a `ClaimsPrincipal` with the hub's claim names
  (`NameIdentifier`/`Name`/`avatar`). Register it **under the same scheme name as the real
  cookie scheme** so `[Authorize]`/the access checks are satisfied; then a request sets one
  header to act as any role on the read→write→maintain→admin→owner ladder. (Pattern adapted
  from the mcadiff core's `FakeAuthenticationHandler`.)

Synthesize fixtures in **code**, never commit binaries — build worlds/chunks with a
`SyntheticChunk` builder (the core's TestAnvil approach), the way the renderer suite needs.

## Gotchas (adopted from the mcadiff core's hard-won list)

- **Revert-the-fix-must-fail.** Reproduce the bug shape, not the happy path.
- **Byte-exact, not length.** A "length ≥ N" assertion on a serializer is trivially true.
- **Assert by relationship, not by magic constant.** Re-derive baselines at runtime.
- **Skip-reason clarity.** A skipped test must say *why* — for the hub, distinguish
  "`mca-git` submodule not initialized" from other build failures (the CS0246 trap; run
  `git submodule update --init` to fix).
- **One-time tokens get a replay test** — present the same token twice, expect success then
  rejection.
- **No PR/issue numbers or line-numbers in source comments** — describe the invariant;
  provenance lives in the PR/issue.
- **Don't mock the core or `RoleOf`.** Use a real temp-file `HubDb` and a real bare repo.
- **Don't pin HTML snapshots.** Assert status + structural elements, not prose.

## Status

There is no test project yet. Standing one up (xUnit + `WebApplicationFactory` + the
`HubDb`/fake-auth harness + a CI job) is the unblocking step; the trust-boundary suites
(IsValidName corpus, RoleOf folding, authz matrix, CSRF, transport round-trip, renderer
hostile-chunks) are filed against it. See the open `tests`-labelled issues.
