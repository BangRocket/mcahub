---
name: sync-docs
description: Diff reality against the hub's three load-bearing documents — README.md, SECURITY.md, CLAUDE.md — and fix drift via the hub-docs-steward agent. Use after a feature lands, when env vars / routes / invariants change, or when any doc is suspected stale.
---

# /sync-docs — keep the three documents truthful

The README sells the system, SECURITY.md is a published reviewer's map, CLAUDE.md carries the
invariants. A stale claim in any of them is a bug — in SECURITY.md it's worse than none, because
reviewers trust it. An argument may name a diff range to focus on; default to comparing the
current code against all three docs wholesale (the repo is small enough).

## Extract ground truth from the code first

Don't compare docs to docs — compare each doc to the code:

1. **Env vars**: grep `MCAHUB_` and `ASPNETCORE_URLS` across `src/` and `Program.cs`. Every
   variable must appear, with the right default, in the README's config tables and `.env.example`;
   security-relevant ones (`MCAHUB_TOKEN`, `MCAHUB_DEV_LOGIN`, `MCAHUB_BEHIND_PROXY`, OAuth set)
   also in SECURITY.md.
2. **Routes**: grep `Map(Get|Post)` in `src/McadiffHub/`. New endpoints must be reflected in
   SECURITY.md's trust-boundary table (entry points column) and, if user-facing, the README's
   feature list / how-it-works section.
3. **Invariant symbols**: confirm the names the docs lean on still exist and mean what's claimed —
   `HubDb.RoleOf`, `Auth.CanRead/CanWrite/CanManageSettings/CanManagePeople`, `CsrfField/CsrfOk`,
   `RepoStore.IsValidName`, `SafeInflate`/`NbtDepthGuard`/`PathGuard.Confine` (core side),
   the role ladder, the 404-not-403 rule, the token hashing scheme.
4. **Commands**: the build/run/render commands in README and CLAUDE.md still work as written
   (the sibling must be `../mca-git`; CI checkout layout in `.github/workflows/ci.yml` matches
   what CLAUDE.md says).
5. **Status & roadmap**: anything in the README's "Status & roadmap" that has since shipped moves
   from roadmap to shipped.

## Fix via the steward

Hand the drift list to the `hub-docs-steward` agent to write the actual edits — it owns voice
and cross-document consistency (a new feature lands in README's feature list *and* how-it-works;
a new boundary lands in SECURITY.md's table; a new invariant lands in CLAUDE.md). Review its
edits: factual claims verified against code, no marketing drift, env-var tables consistent
across all three docs and `.env.example`.

## Report

A drift table: claim → where it lives → what the code says → fixed/intentional. Explicitly state
"no drift" per document when clean. If a change created a *new* trust boundary that SECURITY.md
lacks entirely, flag it loudly — that's the highest-severity drift this skill can find.
