---
name: known-drift-patterns
description: Recurring doc-vs-code gaps found in the 2026-06-05 audit — patterns to watch for in future passes
metadata:
  type: project
---

## Findings from 2026-06-05 audit (updated after second pass)

**Why:** Comprehensive accuracy audit of README, SECURITY.md, CLAUDE.md against actual source.

### Patterns fixed 2026-06-05

1. **MCAHUB_MAPS missing from README** — fixed; "Map cache" row added to README config table.
2. **README "sibling" default language** — fixed; defaults now read `data/cache`, `data/hub.json`, `data/audit.jsonl` (accurate: derived from parent of dataDir, not from old sibling-repo concept).
3. **SECURITY.md trust-boundary table missing new endpoints** — fixed; added rows for `/auth/age-gate`, `/account/delete`, `/r/{repo}/delete`, `/admin/repos/{repo}/remove`.
4. **SECURITY.md item #1 stale — claimed "no rate limiting"** — fixed; updated to describe what IS bounded and what remains unbounded.
5. **Audit log listed as roadmap** — fixed in README status section; audit log shipped, removed from todo list. Added other recent features (multi-provider sign-in, COPPA gate, AUP, abuse report, GDPR/CCPA erasure, per-user world quota) to the "Shipped" sentence.
6. **CLAUDE.md line count** — fixed; updated from "~1900 lines" to "~3150 lines across 19 files".
7. **CLAUDE.md missing new subsystems** — fixed; added AuditLog.cs, AgeGate.cs, AuthThrottle.cs, StartupGuard.cs, ForwardedProxies.cs, MinecraftAuth.cs to architecture section.

### Remaining known drift (not fixed — intentional or out-of-scope)

- SECURITY.md trust-boundary table still omits team management and individual account token routes — these are routine CSRF-protected POSTs covered by the "Web — actions" catch-all row. Only endpoints with unusual gate logic warrant their own row.
- Token format: SECURITY.md "30 random bytes" is spirit-correct; the `mcahub_` prefix and base64url encoding are implementation detail, not a doc claim that would mislead a reviewer.
- Auth mode log string: README quickstart says log reads `auth: accounts (github OAuth)` but actual log includes the dataDir prefix. Low severity; a reader checking the log will still recognize the auth mode string.
- CLAUDE.md inline env list defers to README deliberately — do not expand it to full table.

### How to apply
When any of these file categories change — Program.cs (env vars / limits), Auth.cs (new predicates), Pages.cs (new routes), HubDb.cs (new flags/methods) — cross-check all three docs before closing the PR. The env var table and the SECURITY.md "Where I'd look" section are the highest-drift surfaces. New routes with unusual gate logic (not just standard CSRF+capability) need a row in the trust-boundary table.
