# ADR-0006: Vendor the mcadiff core as a git submodule

- **Status:** Accepted
- **Date:** 2026-06-05

## Context

[ADR-0003](0003-sibling-mcadiff-core-coupling.md) referenced the mcadiff core via a
sibling-path `ProjectReference` (`..\..\..\mca-git\src\McaDiff\McaDiff.csproj`), expecting
the operator to clone `BangRocket/mcadiff` next to the hub repo. In practice the single
biggest onboarding footgun was exactly the one that ADR called out: a plain `git clone`
plus `dotnet build` produces ~26 cryptic `CS0246` errors with the real cause buried in an
`MSB9008` warning. CI also carried the coupling: a separate `actions/checkout` of
`BangRocket/mcadiff` at a manually-bumped commit SHA, while the hub's csproj had no
version constraint, allowing local and CI builds to silently disagree.

The end-state in ADR-0003 — publish the core as a versioned NuGet and replace the project
reference with a pinned package reference — remains the right answer, but the core's API
has not yet stabilized enough to cut a package, so we need a closer-in fix for the
onboarding and version-skew pain.

## Decision

We will vendor the mcadiff core as a **git submodule** at `./mca-git` of the hub repo, and
change the `ProjectReference` to `..\..\mca-git\src\McaDiff\McaDiff.csproj`. The submodule
gitlink in the parent repo's tree is the canonical pin: bumping the core is an explicit
`git submodule update --remote mca-git` followed by a commit, visible in PR diffs. CI uses
`actions/checkout` with `submodules: recursive` and stops separately checking out the core,
so local and CI builds resolve against the same commit by construction.

This is **not** a rejection of the NuGet end-state in ADR-0003 — it is the bridge we use
until the core API stabilizes. A future ADR will supersede this one when the package lands.

## Consequences

- **Positive:** a plain `git clone --recurse-submodules` builds cleanly with no extra
  setup step; the CS0246 onboarding footgun is gone for anyone who follows the README.
  The gitlink is the single source of truth for which core commit ships, so local-vs-CI
  version skew disappears. Core bumps show up as a one-line gitlink change in PR diffs,
  not as a quiet CI-only edit.
- **Negative:**
  - **Submodule UX friction.** Operators who forget `--recurse-submodules` still get the
    CS0246 errors (now with empty `mca-git/`); the error message is no friendlier than
    before, though the README and CLAUDE.md call it out at the top.
  - **Bump cadence is more deliberate.** Floating against upstream `main` is no longer
    free — every bump is an explicit submodule update commit. That is mostly a feature
    (auditability) but slows ad-hoc tracking.
  - **The supply-chain gap is unchanged.** We still ship the core's transitive NuGet deps
    (fNbt, LZ4, cloud SDKs) past the hub's own Dependabot. The CI vulnerability scan over
    the shipped graph (`dotnet list package --vulnerable --include-transitive`) remains
    the compensating control, and the NuGet end-state remains the real fix.
- **Neutral / follow-ups:** the intended end-state from ADR-0003 stands — publish the
  core as a versioned NuGet and replace the submodule with a `PackageReference`. This ADR
  is itself a bridge to be superseded when that lands.
