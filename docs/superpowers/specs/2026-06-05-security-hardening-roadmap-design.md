# Security hardening roadmap — design (2026-06-05 audit)

Campaign-level design for addressing the 17 security issues surfaced by the 2026-06-05 multi-agent
audit (tracking issue [#29]). Each issue ships as its **own PR** that `Closes #NN`, in red-team
priority order, with the **test harness (#19) built first** so every fix lands with a regression test.

This document is the living roadmap. The bigger subsystems (#11, #18, #16) get their own focused
design when reached; this doc plans the campaign and fully specifies the test foundation.

## Scope

The "17 security issues" = every `security`-labelled issue plus the two renderer-hardening issues
(#14, #15) that #29 folds into the DoS tier:

`#1, #2, #3, #4, #5, #6, #7, #8, #9, #10, #11, #12, #13, #14, #15, #16, #18`

Out of scope for this campaign: #17 (ownership transfer — identity/product), #20–#28, #30 (tests
beyond the harness, product, distribution, UX, docs). #19 (test harness) is in scope as the
foundation, #20 (trust-boundary test suites) is satisfied incrementally as each fix adds its tests.

## Delivery model

- **One PR per issue.** Branch `harden/NN-slug` off `main`; PR body `Closes #NN`.
- **Tests first (TDD).** Write the failing test through the harness, implement, prove green.
- **Verification before completion.** `dotnet test` + `dotnet build` must pass and be shown before
  any "done" claim or PR.
- **Per-issue workflow:** branch → failing test → implement → green → commit → push → PR → back to
  `main`.

## PR sequence (red-team priority order)

| # | Issue | One-line approach | Test idea |
|---|---|---|---|
| **0** | **#19** test harness + CI | xUnit + `WebApplicationFactory<Program>`; `HubFactory` boots the hub against temp dirs in a chosen mode; CI checks out the sibling core pinned to a known commit | smoke: `GET /` → 200 in open mode |
| 1 | **#1** existence oracle | transport **write** to an unowned-but-private / non-readable repo returns **404**, matching reads; never 403-leak existence | private repo: anon `POST /r/x/pack` → 404, not 403 |
| 2 | **#2** push body caps | enforce a max request-body size on `objects`/`pack`/`refs` (per-endpoint limit + streaming guard) | oversize body → 413 |
| 3 | **#3** `/have` auth-before-read | move `Readable` check **before** `ReadFromJsonAsync` so unauthenticated bodies aren't deserialized | unauth `/have` on private → 404 with body never read |
| 4 | **#4** render lock/timeout/cap | replace the global `MapCache` lock with per-commit work + a bounded concurrency gate + a render timeout | concurrent map requests don't serialize; slow render times out |
| 5 | **#14** renderer alloc bounds | bound per-chunk allocation; cap region reads to the configured ceiling before allocating from attacker coords | crafted region with huge span doesn't OOM |
| 6 | **#15** renderer clamp Y / `.mcc` | clamp section `Y` to valid range; bound external `.mcc` reads | negative/huge `Y` and oversized `.mcc` handled |
| 7 | **#5** cache disk quota | quota + eviction (LRU by commit) for `WorldCache`/`MapCache`; fail-safe when over quota | quota exceeded → oldest evicted / new write refused |
| 8 | **#10** rate limiting | `AddRateLimiter`: partitioned limits on auth, push, render endpoints | burst over limit → 429 |
| 9 | **#6** claim-on-first-push | close ownership-takeover during the accounts migration (gate claim, or require explicit adopt) | second user can't claim an existing unowned repo |
| 10 | **#9** fail-closed startup | guards that refuse to start open-mode / dev-login on a public bind without explicit opt-in | dev-login on non-loopback bind → startup fails |
| 11 | **#7** forwarded-header spoof | don't blanket-clear `KnownProxies`; require explicit trusted-proxy config | spoofed `X-Forwarded-Proto` from non-proxy ignored |
| 12 | **#8** headers + cookies | security response headers (CSP/X-CTO/frame/referrer) + `Secure` cookies + HSTS behind TLS | responses carry headers; cookies `Secure` |
| 13 | **#13** minor hardening | error-message leak, logout `GET`→`POST`, backslash redirect, null-bang NREs | each: targeted regression test |
| 14 | **#11** master-token lifecycle | scoping/rotation/revocation for `MCAHUB_TOKEN` (**own design when reached**) | rotated token invalidates old |
| 15 | **#18** token lifecycle | PAT expiry, scoping, rotation, session revocation (**own design when reached**) | expired PAT rejected; revoked session logged out |
| 16 | **#12** supply chain | pin/vendor the core dependency; dependency scanning that sees transitive NuGet deps | CI scan present; pinned ref |
| 17 | **#16** audit log | append-only log of role/visibility/ownership/ref/token changes (**own design when reached**) | privileged change writes an audit entry |

Within the DoS tier, per-endpoint caps (#2, #3) precede the global rate limiter (#10); the renderer
cluster (#4, #14, #15) lands together.

## Test harness (#19) — detailed design

The foundation every other PR builds on. Lives in `tests/McadiffHub.Tests/`.

### Components

- **Project** `tests/McadiffHub.Tests/McadiffHub.Tests.csproj`, `net10.0`, references the hub project
  and `Microsoft.AspNetCore.Mvc.Testing` (brings `WebApplicationFactory<TEntryPoint>`), **xUnit**.
  Added to `McadiffHub.slnx`.
- **Entry point exposure:** append `public partial class Program;` to `Program.cs` so
  `WebApplicationFactory<Program>` can locate the entry point (top-level-statements requirement).
- **`HubFactory`** — the central helper. Boots the hub with:
  - a **fresh temp directory** per instance for `DataDir`/`CacheDir`/`MapDir`/`DbPath` (cleaned on
    dispose), so tests never touch the dev's real `data/`;
  - a **chosen auth mode** — `Open` (default), `Token(masterToken)`, or `Accounts` (dev-login on) —
    selected via `WebApplicationFactory.WithWebHostBuilder` + `UseSetting(...)`;
  - helpers to mint a signed-in cookie client (dev-login) and a Bearer/PAT client.
  - returns a configured `HttpClient`.

### Testability refactor (in scope for #19)

`Auth.Read` reads `MCAHUB_DEV_LOGIN` and `Program` reads `MCAHUB_BEHIND_PROXY` **env-only**, which
makes auth-mode and proxy behaviour impossible to drive hermetically (env is process-wide and races
across parallel tests). Make both **config-first then env**, matching how `DataDir`, `PushToken`, and
the OAuth keys already read (`c[key] ?? Environment.GetEnvironmentVariable(env)`). ~2 lines; lets the
factory pick mode via `UseSetting` without mutating process env. No behaviour change for real
deployments (env still works; config is only set by tests).

### CI

`.github/workflows/ci.yml`:

1. checkout the hub into a subdir;
2. checkout **`BangRocket/mcadiff` pinned to commit `41f6f2f`** into the sibling path the build
   expects (`../mca-git`), so `../mca-git/src/McaDiff/McaDiff.csproj` resolves;
3. setup .NET 10;
4. `dotnet test`.

Pinning the core (vs. tracking its `main`) gives reproducible CI and intentional bumps; it also
seeds the supply-chain work in #12.

### Acceptance

- `dotnet test` green locally and in CI.
- A smoke test: open-mode `GET /` → 200, and a private-repo read → 404, proving the harness can
  exercise both transport and pages across modes.

## Subsystems flagged for their own design

When reached, these pause for a short focused design before implementation rather than guessing:

- **#11** master-token scoping/rotation/revocation
- **#18** PAT expiry/scoping/rotation + session revocation
- **#16** audit log (storage format, what's logged, retention)
- policy calls inside **#5** (eviction policy/quota defaults) and **#10** (limit values/partitions)

[#29]: https://github.com/BangRocket/mcahub/issues/29
