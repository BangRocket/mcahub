---
name: env-var-cross-reference
description: Authoritative cross-reference of all MCAHUB_ env vars: code location, default, and doc coverage
metadata:
  type: reference
---

Source of truth: `src/McadiffHub/Program.cs` lines 21-39 and `src/McadiffHub/Auth.cs` lines 33-48.

Last verified: 2026-06-05

| Var | Code default | README table | .env.example | CLAUDE.md list |
|---|---|---|---|---|
| MCAHUB_DATA | `"data/repos"` | `data/repos` ✓ | `data/repos` ✓ | listed ✓ |
| MCAHUB_CACHE | `data/cache` (parent-of-dataDir + "cache") | `data/cache` ✓ (fixed 2026-06-05) | `data/cache` ✓ | listed ✓ |
| MCAHUB_MAPS | `data/maps` | `data/maps` ✓ (added 2026-06-05) | `data/maps` ✓ | not in inline list, not required |
| MCAHUB_DB | `data/hub.json` | `data/hub.json` ✓ (fixed 2026-06-05) | `data/hub.json` ✓ | listed ✓ |
| MCAHUB_AUDIT | `data/audit.jsonl` | `data/audit.jsonl` ✓ (fixed 2026-06-05) | `data/audit.jsonl` ✓ | listed ✓ |
| MCAHUB_TOKEN | none | documented ✓ | documented ✓ | listed ✓ |
| MCAHUB_TOKEN_SHA256 | none | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_BEHIND_PROXY | off | documented ✓ | documented ✓ | in narrative (ForwardedProxies.cs entry) ✓ |
| MCAHUB_TRUSTED_PROXY | loopback | documented ✓ (README line ~162) | documented ✓ | in narrative (ForwardedProxies.cs entry) ✓ |
| MCAHUB_OAUTH_CLIENT_ID | none | documented ✓ | documented ✓ | listed as MCAHUB_OAUTH_* ✓ |
| MCAHUB_OAUTH_CLIENT_SECRET | none | documented ✓ | documented ✓ | listed ✓ |
| MCAHUB_OAUTH_PROVIDER | `"github"` | documented ✓ | documented ✓ | listed ✓ |
| MCAHUB_OAUTH_AUTH_URL | GitHub's | documented ✓ | documented ✓ | listed ✓ |
| MCAHUB_OAUTH_TOKEN_URL | GitHub's | documented as `_TOKEN_URL` ✓ | documented ✓ | listed ✓ |
| MCAHUB_OAUTH_USER_URL | GitHub's | documented as `_USER_URL` ✓ | documented ✓ | listed ✓ |
| MCAHUB_OAUTH_SCOPE | `"read:user"` | documented ✓ | documented ✓ | listed ✓ |
| MCAHUB_OAUTH_GITHUB_CLIENT_ID/_SECRET | none | documented ✓ | documented ✓ | under MCAHUB_OAUTH_* |
| MCAHUB_OAUTH_MICROSOFT_CLIENT_ID/_SECRET/_TENANT | none/common | documented ✓ | documented ✓ | under MCAHUB_OAUTH_* |
| MCAHUB_OAUTH_MINECRAFT_CLIENT_ID/_SECRET | none | documented ✓ | documented ✓ | under MCAHUB_OAUTH_* |
| MCAHUB_OAUTH_DISCORD_CLIENT_ID/_SECRET | none | documented ✓ | documented ✓ | under MCAHUB_OAUTH_* |
| MCAHUB_DEV_LOGIN | off | documented ✓ | documented ✓ | listed ✓ |
| MCAHUB_I_KNOW_OPEN_MODE_IS_PUBLIC | off | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_ADOPT_UNOWNED | off | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_DEFAULT_PRIVATE | on | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_REPORT_EMAIL | none | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_MIN_AGE_GATE | off | documented ✓ | documented ✓ | in AgeGate.cs entry ✓ |
| MCAHUB_MAX_WORLDS_PER_USER | 0 | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_MAX_PUSH_BYTES | 256 MiB | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_CACHE_MAX_GB | 10 | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_MAX_WORLDS_PER_REPO | 10 | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_MAP_CACHE_MAX_GB | 2 | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_MAX_MAPS_PER_REPO | 100 | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_MAX_MANIFEST_ENTRIES | 100000 | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_MAX_RENDER_CONCURRENCY | 3 | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_MAX_RENDER_CHUNKS | 10000 | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_RENDER_TIMEOUT_SECONDS | 30 | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_RATELIMIT_AUTH | 20 | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_RATELIMIT_WRITE | 60 | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_RATELIMIT_RENDER | 30 | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_RATELIMIT_READ | 300 | documented ✓ | documented ✓ | not in inline list |
| MCAHUB_AUTH_MAX_FAILURES | 5 | documented ✓ | documented ✓ | in AuthThrottle.cs entry ✓ |
| MCAHUB_AUTH_LOCKOUT_SECONDS | 30 | documented ✓ | documented ✓ | in AuthThrottle.cs entry ✓ |

## Notes
- CLAUDE.md's inline env list (`MCAHUB_DATA`, `MCAHUB_CACHE`, `MCAHUB_DB`, `MCAHUB_TOKEN`, `MCAHUB_OAUTH_*`, `MCAHUB_DEV_LOGIN`, `MCAHUB_BEHIND_PROXY`) is intentionally representative, not exhaustive — it defers to README for the full table.
- "sibling" in Program.cs means parent-of-dataDir (e.g., if dataDir=`data/repos`, sibling=`data/`), so defaults are `data/cache`, `data/maps`, `data/hub.json`, `data/audit.jsonl`. The README previously called these "sibling cache/" etc., which was a false cognate with the old sibling-repo model. Fixed 2026-06-05.
