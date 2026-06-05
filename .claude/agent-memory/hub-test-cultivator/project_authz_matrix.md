---
name: project-authz-matrix
description: Full authz matrix of routes x roles with coverage gaps noted
metadata:
  type: project
---

## Role ladder
anonymous(0) < read(1) < write(2) < maintain(3) < admin(4) < owner(5) < master-token(admin bypass)

## Transport matrix (accounts mode)
| Endpoint | anonymous | read-collab | write-collab | maintain-collab | admin-collab | owner | master-token |
|---|---|---|---|---|---|---|---|
| GET /r/{private}/info/refs | 404 | 200 | 200 | 200 | 200 | 200 | 200 |
| GET /r/{public}/info/refs  | 200 | 200 | 200 | 200 | 200 | 200 | 200 |
| POST /r/{repo}/have        | 404 (private) | 200 | 200 | 200 | 200 | 200 | 200 |
| GET /r/{repo}/objects/{h}  | 404 (private) | 200 | 200 | 200 | 200 | 200 | 200 |
| POST /r/{repo}/pack        | 401 | 403 | 200 | 200 | 200 | 200 | 200 |
| POST /r/{repo}/objects/{h} | 401 | 403 | 200 | 200 | 200 | 200 | 200 |
| POST /r/{repo}/refs/heads  | 401 | 403 | 200 | 200 | 200 | 200 | 200 |

## Pages matrix (accounts mode)
| Endpoint | anonymous | read-collab | write(any role) | maintain | admin | owner |
|---|---|---|---|---|---|---|
| GET /r/{private}           | 404 | 200 | 200 | 200 | 200 | 200 |
| GET /r/{public}            | 200 | 200 | 200 | 200 | 200 | 200 |
| GET /r/{private}/commit    | 404 | 200 | 200 | 200 | 200 | 200 |
| GET /r/{private}/map       | 404 | 200 | 200 | 200 | 200 | 200 |
| POST /r/{repo}/settings    | redirect | redirect | redirect | 200(redirect) | 200(redirect) | 200(redirect) |
| POST /r/{repo}/collaborators | redirect | redirect | redirect | redirect | 200(redirect) | 200(redirect) |
| POST /r/{repo}/collaborators/remove | redirect | redirect | redirect | redirect | 200 | 200 |
| POST /r/{repo}/teams       | redirect | redirect | redirect | redirect | 200 | 200 |
| POST /r/{repo}/teams/remove | redirect | redirect | redirect | redirect | 200 | 200 |

## Known 404-not-403 invariants to test
- Private repo: any transport read endpoint → 404 for anonymous AND for wrong-account bearer
- Private repo: /r/{repo} page → 404 for anonymous AND for non-collaborator logged-in user
- Private repo: /r/{repo}/commit → 404
- Private repo: /r/{repo}/map → 404 (no 403, no 200)

## Coverage gaps (all uncovered as of initial audit 2026-06-05)
- ALL of the above — no integration tests exist
- HubDb.RoleOf team-grant folding path: write-via-team vs write-via-collab vs owner
- CSRF: missing token → 400, wrong token → 400, valid token → success
- Open-redirect guard (Auth.Local): absolute URL, protocol-relative URL, valid relative URL
- Token resolution: valid token → uid, invalid token → null (badToken=true), master token → admin=true

**Why:** These are the trust boundaries. Missing cells are not oversights — they are uncovered claims.
**How to apply:** When any route or role changes, update this matrix and add the corresponding test cell.
