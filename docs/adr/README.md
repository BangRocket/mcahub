# Architecture Decision Records

This directory records the **load-bearing, non-obvious decisions** behind mcahub —
the ones that would otherwise live only in commit history or a maintainer's head and get
re-litigated without their rationale. Format is [Michael Nygard's ADR template](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)
(see [`TEMPLATE.md`](TEMPLATE.md)).

## Conventions

- Numbered sequentially, `NNNN-kebab-title.md`.
- **Immutable once Accepted.** To reverse or change a decision, write a new ADR that
  supersedes the old one and update both `Status` lines — don't rewrite history.
- Keep them short (~40-70 lines) and honest about the downsides.
- Not every decision needs an ADR — reserve them for the things a new contributor would
  otherwise question, undo, or rebuild.

## Index

| ADR | Title | Status |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-zero-dependency-baseline.md) | Zero-dependency baseline (no NuGet beyond the framework) | Accepted |
| [0003](0003-sibling-mcadiff-core-coupling.md) | Reference the mcadiff core as a sibling project | Superseded by [0006](0006-mcadiff-submodule.md) |
| [0004](0004-json-file-account-store.md) | A single JSON file is the account store | Accepted |
| [0005](0005-modular-monolith-over-microservices.md) | Modular monolith over microservices | Accepted |
| [0006](0006-mcadiff-submodule.md) | Vendor the mcadiff core as a git submodule | Accepted |
