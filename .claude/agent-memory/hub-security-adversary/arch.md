---
name: codebase-architecture
description: Key function signatures, auth flow, invariant gate locations, endpoint registry
metadata:
  type: reference
---

## Key Functions

- `HubDb.RoleOf(repo, userId)` — HubDb.cs:149. Single source of truth for repo permissions. Returns "owner"|"admin"|"maintain"|"write"|"read"|null. All repo authz must route here.
- `RepoStore.IsValidName(name)` — RepoStore.cs:15. Regex `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`. Called by Exists() and PathOf(). Must gate ALL path construction from user-supplied names.
- `Auth.CanRead(cfg, db, repo, viewerId, admin)` — Auth.cs:226. Routes through RoleOf. Returns 404 (not 403) callers.
- `Auth.CanWrite(cfg, db, repo, writerId, admin)` — Auth.cs:234. Routes through RoleOf. Returns false when repo exists and user has rank < 2.
- `Auth.CanManageSettings(db, repo, userId)` — Auth.cs:245. Rank >= 3 via RoleOf.
- `Auth.CanManagePeople(db, repo, userId)` — Auth.cs:248. Rank >= 4 via RoleOf.
- `Auth.Identify(req, cfg, db, out badToken)` — Auth.cs:181. Bearer token resolution. Master token via FixedTimeEquals, PAT via hash lookup.
- `Auth.CsrfOk(ctx)` — Auth.cs:207. Validates antiforgery token (ASP.NET IAntiforgery). Must be called before every cookie-auth state-changing POST.
- `Auth.CsrfField(ctx)` — Auth.cs:198. Emits hidden form field + sets CSRF cookie.
- `Auth.Local(url)` — Auth.cs:270. Open-redirect guard. Allows only same-site relative paths (url[0]=='/' and url[1]!='/').
- `Transport.Readable(repo, req, db, cfg)` — Transport.cs:71. Auth.Identify + Auth.CanRead. Returns bool (caller returns 404 on false).
- `Transport.Write(...)` — Transport.cs:77. Auth gate, CanWrite, store.Create, EnsureRepo, then body action.
- `WorldCache.Materialize(repoName, repo, commit)` — WorldCache.cs:15. Double-checked lock. Path: _root/repoName/commit.
- `MapCache.Png(repoName, repo, commit)` — MapCache.cs:15. Single global lock. Path: _root/repoName/commit.png.
- `HubDb.EnsureRepo(name, ownerId, isPrivate)` — HubDb.cs:120. No-op if repo exists. Claim-on-first-push.
- `HubDb.Save()` — HubDb.cs:287. tmp+rename write inside _lock. Atomic-ish publish.

## Endpoint Registry (auth mode, CSRF posture)

| Endpoint | Method | Auth Mode | CSRF |
|---|---|---|---|
| /r/{repo}/info/refs | GET | Bearer (optional) | N/A |
| /r/{repo}/have | POST | Bearer (optional) | N/A - transport, no CSRF |
| /r/{repo}/objects/{hash} | GET | Bearer (optional) | N/A |
| /r/{repo}/objects/{hash} | POST | Bearer required | N/A - transport |
| /r/{repo}/pack | POST | Bearer required | N/A - transport |
| /r/{repo}/refs/heads/{branch} | POST | Bearer required | N/A - transport |
| /auth/login | GET | N/A | N/A |
| /auth/logout | GET | Cookie | None (low-impact GET) |
| /auth/dev | GET | N/A | N/A |
| /auth/dev | POST | DevLogin mode only | CsrfOk |
| /account | GET | Cookie | N/A |
| /account/tokens | POST | Cookie | CsrfOk |
| /account/tokens/revoke | POST | Cookie | CsrfOk |
| /r/{repo}/settings | POST | Cookie | CsrfOk |
| /r/{repo}/collaborators | POST | Cookie | CsrfOk |
| /r/{repo}/collaborators/remove | POST | Cookie | CsrfOk |
| /teams | GET | Cookie | N/A |
| /teams | POST | Cookie | CsrfOk |
| /teams/{name} | GET | Cookie | N/A |
| /teams/{name}/members | POST | Cookie | CsrfOk |
| /teams/{name}/members/remove | POST | Cookie | CsrfOk |
| /teams/{name}/delete | POST | Cookie | CsrfOk |
| /r/{repo}/teams | POST | Cookie | CsrfOk |
| /r/{repo}/teams/remove | POST | Cookie | CsrfOk |

## Cookie Configuration
- `mcahub_session`: HttpOnly=true, SameSite=Lax, **Secure NOT SET**, ExpireTimeSpan=30 days sliding
- `mcahub_csrf`: HttpOnly=true, SameSite=Lax, **Secure NOT SET**
- Default URL: http://localhost:5080 (HTTP, not HTTPS)
