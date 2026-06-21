# ADR-0003: Reference the mcagit core as a sibling project

- **Status:** Superseded by [ADR-0006](0006-mcagit-submodule.md)
- **Date:** 2026-06-05

## Context

The hub's value is rendering the *real* `WorldDiff` / `GriefReport` / `RemoteService`
structures from the mcagit core, not scraping CLI text. The core is a separate repository
(`BangRocket/mcagit`). We need its rich types in-process.

## Decision

We will reference the core **in-process via a sibling `ProjectReference`** —
`..\..\..\mcagit\src\mcagit\mcagit.csproj` — checked out as a sibling directory named
`mcagit`. The csproj carries no version constraint, so a local build resolves against
whatever sibling checkout is present; **CI pins the core to a specific commit SHA** for
reproducible builds, bumped deliberately alongside a core change.

## Consequences

- **Positive:** the rich diff/query/repo/render types are available directly, with no CLI
  scraping or serialization boundary; a breaking change in the core surfaces here at
  **compile time**, immediately and loudly, rather than at runtime later.
- **Negative:**
  - **Build-time coupling.** A missing or differently-named sibling fails the build with ~40
    cryptic `CS0246` errors (the real cause is an MSBuild `MSB4019` warning above them). This
    is the single biggest onboarding footgun.
  - **Unscanned transitive deps.** The hub ships the core's transitive NuGet packages
    (fNbt, LZ4, AWS/Azure SDKs) but the hub's own Dependabot/CodeQL never see them.
  - **Local/CI version skew.** The csproj has no version constraint, so a local build can
    diverge from CI's pinned core SHA; bumping the CI pin is a manual, deliberate step.
- **Neutral / follow-ups:** the intended end-state is to **package the core as a versioned
  NuGet** and replace this project reference with a pinned package reference once the core
  API stabilizes — at which point this ADR is superseded. The supply-chain and CS0246-UX
  gaps are tracked as issues.
