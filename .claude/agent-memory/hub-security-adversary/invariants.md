---
name: invariant-status
description: Per-invariant pass/fail status as of 2026-06-05 full-codebase audit
metadata:
  type: reference
---

## Invariant 1: Private repos return 404, never 403
- **Status: FAIL** — Transport.cs:88 returns 403 "this world belongs to another account"
- Web pages: PASS — CanSee() + NotFound() consistently used
- Transport read endpoints: PASS — Readable() returns bool, caller always returns NotFound()
- Transport write: FAIL — see F1 in findings

## Invariant 2: Every authz decision folds through HubDb.RoleOf
- **Status: PASS with note**
- All repo authz routes through Auth.CanRead/CanWrite/CanManageSettings/CanManagePeople, all of which call db.RoleOf
- Team management uses inline `t.OwnerId == me.Id` check — correct, as teams are not repos and RoleOf is a repo concept
- No ad-hoc repo permission checks found outside the Auth.Can* functions

## Invariant 3: Token plaintext never stored or logged
- **Status: PASS**
- HubDb.CreateToken: returns secret, stores only Sha(secret) — HubDb.cs:64-72
- HubDb.ResolveToken: hashes the presented token before lookup — HubDb.cs:97-99
- Program.cs LogInformation: only logs dataDir and auth mode, no tokens
- PAT displayed once in Account page (fresh token) — intended behavior, after-the-fact display only
- No tokens in error messages or logs found

## Invariant 4: CSRF covers cookie POST, never bearer POST
- **Status: PASS**
- All 13 cookie-authenticated POST endpoints in Pages.cs and Auth.cs call CsrfOk first
- All 4 bearer transport POST endpoints (/have, /objects/{hash}, /pack, /refs/heads/{branch}) have no CSRF check — correct
- /auth/logout is a GET (known limitation, low impact)

## Invariant 5: No filesystem path built from unvalidated names
- **Status: PASS for repo names; boundary concern for object hashes and branch names**
- RepoStore.PathOf: validates with IsValidName before path construction — RepoStore.cs:18-19
- RepoStore.Exists: calls IsValidName internally — RepoStore.cs:23
- WorldCache: repoName reaches Materialize after Exists() check (IsValidName enforced) — WorldCache.cs:17
- MapCache: same path — MapCache.cs:17
- Commit hashes: from core's ResolveRef, expected to be hex hashes; no hub-side validation
- Object hashes in /objects/{hash}: passed directly to core GetObject/PutObject without hub validation
- Branch names in /refs/heads/{branch}: passed to core UpdateRef without hub validation
- These are boundary concerns depending on core's own validation

## Known Soft Spots Status

| Soft Spot | Status | Notes |
|---|---|---|
| Unbounded materialize | OPEN — confessed | No size/count/depth limits in hub layer |
| Disk-filling pushes | OPEN — F2 | No push body size limit |
| Claim-on-first-push takeover | OPEN — F1+F8 | 403 probe + master token ownership gap |
| Forwarded-headers footgun | OPEN — F3 | No KnownProxies restriction |
