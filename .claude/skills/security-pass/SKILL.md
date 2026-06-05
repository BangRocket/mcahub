---
name: security-pass
description: Walk the current diff (or the whole repo) against SECURITY.md's trust-boundary table and "Where I'd look" list, invariant by invariant, delegating the adversarial review to the hub-security-adversary agent. Use before merging anything that touches auth, transport, parsing, rendering, or path handling — or on request for a periodic sweep.
---

# /security-pass — invariant-by-invariant review

SECURITY.md is this repo's contract with reviewers; this skill makes checking against it a
repeatable command instead of a vibe. An argument may name a diff range; otherwise review the
working tree plus the current branch against `merge-base HEAD main`. If that diff is empty, do a
whole-repo sweep of the "Where I'd look" list instead.

## Steps

1. **Collect the diff** (`git diff <range>` + untracked files) and the list of touched files.
2. **Re-read SECURITY.md** — do not work from memory; the trust-boundary table and soft-spot
   list are the checklist, and they get updated.
3. **Map touched files to boundaries.** Auth.cs / Transport.cs / HubDb.cs / RepoStore.cs /
   Pages.cs / MapRenderer.cs / MapCache.cs / WorldCache.cs / Program.cs each have a row in
   SECURITY.md's "Start here" list. A diff touching none of them still gets step 5's quick pass
   (new files can create boundaries).
4. **Delegate the adversarial review** to the `hub-security-adversary` agent: give it the diff,
   the touched-file→boundary map, and ask for findings against the invariants. Don't skip this
   even when the diff looks benign — the agent's whole job is the plausible-looking change.
5. **Verify the findings yourself.** Read the code at each finding; an unverified finding is a
   rumor. Drop anything that doesn't hold up, and say you dropped it.
6. **Soft-spot delta.** For each of SECURITY.md's numbered "Where I'd look" items (resource
   exhaustion, untrusted NBT→image, materialize path handling, claim-on-first-push, master
   token, plaintext JSON store, CSRF edges, proxy header trust): did this change widen, narrow,
   or not touch it? One line each.

## The invariants (fail the pass if any regresses)

- Private repos return **404, never 403** — web pages *and* transport.
- Every authz decision routes through `HubDb.RoleOf` / the `Auth.Can*` predicates — flag any
  ad-hoc check, even a "fast path".
- Token plaintext never stored, logged, or echoed; master-token compare stays constant-time.
- Every cookie-authenticated state-changing POST validates the antiforgery token; Bearer-only
  transport POSTs stay **out** of that path.
- No filesystem path built from a name that hasn't passed `RepoStore.IsValidName`.
- Untrusted world data only enters through the core's guards (`SafeInflate`, `NbtDepthGuard`,
  `PathGuard.Confine`) — flag anything that reaches around them.
- No attacker-sized allocation in the render path before the span cap applies.

## Report

A table: invariant → touched by this diff? → verdict (holds / regressed / new risk), then the
soft-spot delta list, then verified findings with `file:line` references and concrete fixes.
End with a one-word overall verdict: **pass** or **fail**. If SECURITY.md itself is now stale
(the change added a boundary the doc doesn't describe), say so and suggest `/sync-docs`.
