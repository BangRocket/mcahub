---
name: project-architecture
description: Role ladder, full route map, trust boundaries, and key file locations for mcadiff-hub
metadata:
  type: project
---

## Auth modes
- **open**: no MCAHUB_TOKEN, no OAuth — unauthenticated reads+writes
- **token**: MCAHUB_TOKEN set — bearer master token gates all writes
- **accounts**: OAuth or MCAHUB_DEV_LOGIN — full role-based authz via HubDb.RoleOf

## Role ladder (Auth.cs:216-224)
owner(5) > admin(4) > maintain(3) > write(2) > read(1) > anonymous(0)

- CanRead: needs rank >= 1, or public repo, or open mode (Auth.cs:226-232)
- CanWrite: needs rank >= 2 (Auth.cs:234-242)
- CanManageSettings: needs rank >= 3 (Auth.cs:244)
- CanManagePeople: needs rank >= 4 (Auth.cs:246)

## HubDb.RoleOf folding logic (HubDb.cs:149-163)
Folds: owner check first, then max(direct collab grant, any team grant for user's teams). Returns "owner"|"admin"|"maintain"|"write"|"read"|null.

**Key invariant:** RoleOf does NOT return "owner" from the rank switch — owner is checked explicitly before the rank switch. The rank switch goes up to 4=admin. So owner is never a collab role, always a repo OwnerId check.

## Routes

### Transport (no CSRF required — Bearer only)
- GET  /r/{repo}/info/refs       — Readable check → 404 on private
- POST /r/{repo}/have            — Readable check → 404 on private
- GET  /r/{repo}/objects/{hash}  — Readable check → 404 on private
- POST /r/{repo}/objects/{hash}  — Write gate (auth + CanWrite)
- POST /r/{repo}/pack            — Write gate
- POST /r/{repo}/refs/heads/{branch} — Write gate

### Pages (GET = cookie-authed read, POST = CSRF required)
- GET  /                         — Home, filters to readable repos
- GET  /r/{repo}                 — CanSee → 404 on private
- GET  /r/{repo}/commit/{hash}   — CanSee
- GET  /r/{repo}/compare/{a}/{b} — CanSee
- GET  /r/{repo}/world/{reff}    — CanSee
- GET  /r/{repo}/map/{reff}.png  — CanSee (returns image/png, no HTML wrapper)
- GET  /r/{repo}/timeline        — CanSee

### Account/admin routes (accounts mode only)
- GET  /account
- POST /account/tokens           — CSRF
- POST /account/tokens/revoke    — CSRF
- POST /r/{repo}/settings        — CSRF, needs CanManageSettings (rank>=3)
- POST /r/{repo}/collaborators   — CSRF, needs CanManagePeople (rank>=4)
- POST /r/{repo}/collaborators/remove — CSRF, needs CanManagePeople
- GET  /teams
- POST /teams                    — CSRF
- GET  /teams/{name}
- POST /teams/{name}/members     — CSRF, team owner only
- POST /teams/{name}/members/remove — CSRF, team owner only
- POST /teams/{name}/delete      — CSRF, team owner only
- POST /r/{repo}/teams           — CSRF, needs CanManagePeople
- POST /r/{repo}/teams/remove    — CSRF, needs CanManagePeople

### Auth routes
- GET /auth/login
- GET /auth/logout
- GET /auth/dev  (DevLogin only)
- POST /auth/dev (DevLogin only, CSRF)

## Key security invariants
- Private repos → 404 not 403 (existence hidden)
- All authz through HubDb.RoleOf / Auth.Can* predicates
- Token plaintext never stored; SHA-256 lookup; master token FixedTimeEquals
- Every cookie POST validates CSRF; Bearer transport POSTs exempt
- Repo names validated before any filesystem path built (RepoStore.IsValidName)
- NBT/packs parsed only via core's SafeInflate / NbtDepthGuard / PathGuard.Confine

## File locations
- Auth: src/McadiffHub/Auth.cs
- HubDb: src/McadiffHub/HubDb.cs
- Transport: src/McadiffHub/Transport.cs
- Pages: src/McadiffHub/Pages.cs
- RepoStore: src/McadiffHub/RepoStore.cs
- MapRenderer: src/McadiffHub/MapRenderer.cs
- MapCache: src/McadiffHub/MapCache.cs
- WorldCache: src/McadiffHub/WorldCache.cs
- Program: src/McadiffHub/Program.cs
- Html helpers: src/McadiffHub/Html.cs

**Why:** Needed to keep authz matrix complete as routes/roles evolve.
**How to apply:** When a new route or role appears, cross-reference this map and expand the test matrix.
