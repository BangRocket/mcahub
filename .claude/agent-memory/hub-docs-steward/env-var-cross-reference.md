---
name: env-var-cross-reference
description: Authoritative cross-reference of all MCAHUB_ env vars: code location, default, and doc coverage
metadata:
  type: reference
---

Source of truth: `src/McadiffHub/Program.cs` lines 21-39 and `src/McadiffHub/Auth.cs` lines 33-48.

| Var | Code default | README table | .env.example | CLAUDE.md list |
|---|---|---|---|---|
| MCAHUB_DATA | `"data/repos"` | `data/repos` ✓ | `data/repos` ✓ | listed ✓ |
| MCAHUB_CACHE | `Path.Combine(sibling, "cache")` where sibling=parent of dataDir = `data/` → so default is `data/cache` | says "sibling `cache/`" — ambiguous, but .env.example says `data/cache` which matches | `data/cache` ✓ | listed ✓ |
| MCAHUB_MAPS | `Path.Combine(sibling, "maps")` = `data/maps` | **MISSING** from README table | **MISSING** from .env.example | **MISSING** from CLAUDE.md list |
| MCAHUB_DB | `Path.Combine(sibling, "hub.json")` = `data/hub.json` | says "sibling `hub.json`" — ambiguous, .env.example says `data/hub.json` | `data/hub.json` ✓ | listed ✓ |
| MCAHUB_TOKEN | none | documented ✓ | documented ✓ | listed ✓ |
| MCAHUB_BEHIND_PROXY | off | documented ✓ (README line 105) | documented ✓ | NOT in CLAUDE.md list — but listed in narrative |
| MCAHUB_OAUTH_CLIENT_ID | none | documented ✓ | documented ✓ | listed as MCAHUB_OAUTH_* ✓ |
| MCAHUB_OAUTH_CLIENT_SECRET | none | documented ✓ | documented ✓ | listed ✓ |
| MCAHUB_OAUTH_PROVIDER | `"github"` | documented ✓ | documented ✓ | listed ✓ |
| MCAHUB_OAUTH_AUTH_URL | GitHub's | documented ✓ | documented ✓ | listed ✓ |
| MCAHUB_OAUTH_TOKEN_URL | GitHub's | documented as `_TOKEN_URL` ✓ | documented ✓ | listed ✓ |
| MCAHUB_OAUTH_USER_URL | GitHub's | documented as `_USER_URL` ✓ | documented ✓ | listed ✓ |
| MCAHUB_OAUTH_SCOPE | `"read:user"` | documented ✓ | documented ✓ | listed ✓ |
| MCAHUB_DEV_LOGIN | off | documented ✓ | documented ✓ | listed ✓ |

## Key finding
`MCAHUB_MAPS` is a live env var that controls the map cache directory. It is absent from README, .env.example, and CLAUDE.md — the only reference outside code is `.claude/skills/smoke/SKILL.md`.

## Default confusion
README says MCAHUB_CACHE defaults to "sibling `cache/`" and MCAHUB_DB defaults to "sibling `hub.json`". The word "sibling" in README context normally means sibling to the repo root (i.e., `../cache`), but the code computes sibling as parent-of-dataDir = `data/`, so the actual defaults are `data/cache` and `data/hub.json`. The .env.example correctly shows `data/cache` and `data/hub.json`. This is a README wording defect.
