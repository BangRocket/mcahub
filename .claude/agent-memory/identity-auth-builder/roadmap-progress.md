---
name: roadmap-progress
description: Identity layer roadmap — what has been reviewed, what is planned, severity and effort for each item.
metadata:
  type: project
---

## Design Review Completed (2026-06-05)

Full design review of Auth.cs, HubDb.cs, Pages.cs (account/teams/collab/token routes), Transport.cs, Program.cs, and README was completed. See findings below.

## Open Design Findings

| ID | Finding | Severity | Status |
|----|---------|----------|--------|
| §2.4 | Dev login has no production host guard (env var only) | Medium | Open |
| §1.1 | Dual Rank() functions — Auth.Rank vs HubDb.Rank not shared | Low | Open |
| §1.3 | Admins can grant/revoke other admins (design decision, not bug) | Design | Documented |
| §5.1 | Logout is GET — CSRF-logout possible | High | Open (roadmap R1) |
| §5.2 | No server-side session store — can't "sign out everywhere" | Medium | Open (roadmap R7) |
| §2.1 | Master token blast radius — no userId, no per-repo scope | High | Design decision |
| §2.2 | Open→accounts claim race on first push | Medium | Open (roadmap R9) |
| §3.1 | No token expiry | Medium | Open (roadmap R3) |
| §3.2 | No token scoping | Medium | Open (roadmap R5) |
| §4.4 | No audit trail | Medium | Open (roadmap R2) |
| §6.1 | No rate limiting on auth attempts | Medium | Open (roadmap R8) |

## Roadmap Items

| ID | Feature | Severity | Effort | Status |
|----|---------|----------|--------|--------|
| R1 | Logout as POST+CSRF | Security must-have | Low | Not started |
| R2 | Audit log (role/visibility/ref changes) | Security must-have | Moderate | Not started |
| R3 | Token expiry (ExpiresAt field on TokenRecord) | Security must-have | Low | Not started |
| R4 | Ownership transfer (repo + team) | Correctness must-have | Moderate | Not started |
| R5 | Token scoping (read/write/admin + optional repo list) | Security must-have | Moderate | Not started |
| R6 | Per-repo deploy tokens | Nice-to-have | Moderate | Not started |
| R7 | Sign out all sessions (server-side session store) | Nice-to-have | High | Not started |
| R8 | Rate limiting on auth attempts | Security must-have (internet) | Low | Not started |
| R9 | Pre-claim migration tool for open→accounts transition | Correctness must-have | Moderate | Not started |
| R10 | 2FA via provider | Already works for OAuth users | None | Documented |
| R11 | Team ownership transfer | Quality-of-life | Low | Not started |

## Conventions Established

- All new state goes through HubDb locked atomic writes (tmp+rename).
- All new permission logic goes through RoleOf / Can* predicates — never inline rank comparisons at call sites.
- Antiforgery: web POSTs only, never transport/bearer endpoints.
- New roadmap items that add identity features should extend HubDb.Db record and call Save() inside _lock.
- AuditEntry (R2) should be append-only — no Remove/mutation methods on the audit list.
