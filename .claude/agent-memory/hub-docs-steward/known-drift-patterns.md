---
name: known-drift-patterns
description: Recurring doc-vs-code gaps found in the 2026-06-05 audit — patterns to watch for in future passes
metadata:
  type: project
---

## Findings from 2026-06-05 audit

**Why:** Comprehensive accuracy audit of README, SECURITY.md, CLAUDE.md against actual source.

### Drift patterns to watch

1. **New env vars added to Program.cs without updating docs** — MCAHUB_MAPS was added with MapCache but never landed in README, .env.example, or CLAUDE.md. Check Program.cs lines 21-39 and Auth.cs lines 33-48 every time a storage concern is added.

2. **Default path descriptions drift** — README says "sibling cache/" (implying repo-sibling) but code and .env.example both say data/cache. The word "sibling" in README refers to the sibling mcadiff repo checkout concept, creating false-cognate confusion when applied to data directories.

3. **SECURITY.md trust-boundary table omits web-only action endpoints** — team management routes (/teams, /teams/{name}/*) and account mutation routes (/account/tokens, /account/tokens/revoke) are fully CSRF-protected state-changing POSTs but have no row in the trust boundary table.

4. **Token format: SECURITY.md says "30 random bytes" stored SHA-256** — actually the secret is "mcahub_" + base64url(30 random bytes), stored as SHA-256 of that full string. The description is correct in spirit but the actual secret prefix and base64url encoding are not mentioned.

5. **Auth mode log string** — README says log reads "auth: accounts (github OAuth)" but actual code emits "mcadiff-hub serving worlds from {DataDir} · auth: accounts (github OAuth)". Low severity, but the exact log string in the quickstart is slightly wrong.

6. **CLAUDE.md env list** — MCAHUB_BEHIND_PROXY is in the CLAUDE.md narrative but not in the parenthetical env var list on line 33-34.

### How to apply
When any of these file categories change — Program.cs, Auth.cs, Pages.cs (new routes), HubDb.cs (new token format) — cross-check all three docs before closing the PR. The env var table is the highest-drift surface.
