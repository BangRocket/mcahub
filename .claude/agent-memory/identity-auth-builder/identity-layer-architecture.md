---
name: identity-layer-architecture
description: Complete architecture of Auth.cs, HubDb.cs, and the role model — signatures, predicates, PKCE flow, web/CLI split, and antiforgery boundary.
metadata:
  type: project
---

## Auth.cs — Key Signatures

- `Auth.Config` record: `(bool Accounts, bool Oauth, bool DevLogin, string Provider, string? ClientId, string? ClientSecret, string AuthUrl, string TokenUrl, string UserUrl, string Scope, string? MasterToken)`
- `Auth.Read(IConfiguration)` — builds Config from env vars / appsettings.
- `Auth.AddAuth(WebApplicationBuilder, Config, HubDb)` — wires cookie + OAuth schemes. PKCE enabled via `o.UsePkce = true`. Only active when `cfg.Accounts` is true.
- `Auth.MapAuth(WebApplication, Config, HubDb)` — registers `/auth/login`, `/auth/logout`, `/auth/callback`, `/auth/dev` (dev-login POST).
- `Auth.Current(HttpContext) → HubUser?` — reads the web cookie identity (ClaimTypes.NameIdentifier).
- `Auth.Identify(HttpRequest, Config, HubDb, out bool badToken) → (string? userId, bool admin)` — CLI bearer token identity. Master token → (null, admin:true). PAT → (userId, false). No token → (null, false).
- `Auth.CanRead`, `CanWrite`, `CanManageSettings`, `CanManagePeople` — all `Can*` predicates delegate to `HubDb.RoleOf`.
- `Auth.CsrfField(HttpContext) → string` — issues antiforgery hidden field; sets cookie on the response.
- `Auth.CsrfOk(HttpContext) → Task<bool>` — validates antiforgery; called manually on web POSTs only.
- `Auth.Local(string?) → string` — open-redirect guard: accepts only paths starting with `/` and not `//`. Default: `/account`.
- `Auth.Rank(string?) → int` (private): `owner=5, admin=4, maintain=3, write=2, read=1, _=0`.

## HubDb.cs — Key Signatures

- Lock object: `private readonly object _lock = new()` (single object, all mutations serialize through it).
- Token hash: `private static string Sha(string s)` = SHA-256 hex, UTF-8 input.
- Atomic write: `private void Save()` — writes JSON to `_path + ".tmp-" + Guid[..8]`, then `File.Move(tmp, _path, overwrite: true)`.
- In-memory token index: `private readonly Dictionary<string, TokenRecord> _byHash` — O(1) token lookup; updated in sync with every token add/revoke.
- `HubDb.RoleOf(string repo, string? userId) → string?` — effective role: owner-check first, then max of direct collab + team grants. Returns `owner|admin|maintain|write|read|null`.
- `HubDb.IsRole(string role) → bool` — allowlist: `read|write|maintain|admin` (does NOT include `owner`).
- `HubDb.Rank(string?) → int` (private, static): `admin=4, maintain=3, write=2, read=1` — NOTE: does NOT include `owner`. Different from Auth.Rank.
- `HubDb.CreateToken(string userId, string label) → string` — returns plaintext once; stores only hash+prefix.
- `HubDb.ResolveToken(string secret) → string?` — stamps LastUsedAt on every call, calls Save().
- `HubDb.EnsureRepo(string name, string ownerId, bool isPrivate) → HubRepoMeta` — no-op if repo already owned.
- Token prefix: `secret[..14]` (7 literal "mcahub_" + 7 chars base64url). Used for display and revocation key; not for auth.

## Role Model

Ladder: `owner > admin > maintain > write > read > (none)`
- owner: computed from `HubRepoMeta.OwnerId`, never stored as a collab/team role.
- admin (rank 4): manage collaborators + team grants.
- maintain (rank 3): change visibility.
- write (rank 2): push.
- read (rank 1): browse/clone private repos.

Effective access = `max(owner-check, direct-collab-rank, max-team-grant-rank)`.
`CanWrite` requires rank ≥ 2. `CanManageSettings` requires ≥ 3. `CanManagePeople` requires ≥ 4.

## Web/CLI Identity Split

- **Web (cookie)**: OAuth PKCE flow → `OnCreatingTicket` upserts user via `HubDb.UpsertUser` → cookie issued. Antiforgery applied to all web POSTs.
- **CLI (bearer PAT)**: `Auth.Identify` called at every transport endpoint. Master token → `admin:true, userId:null`. PAT → `userId`, roles from `RoleOf`.
- Antiforgery is deliberately NOT applied to transport endpoints (bearer-authenticated, not subject to CSRF). Applied manually on all web form POSTs via `Auth.CsrfOk`.

## PKCE / OAuth Flow Steps

1. `/auth/login` → `Results.Challenge(properties { RedirectUri = Local(returnUrl) }, ["oauth"])`.
2. Framework generates `code_verifier`, `code_challenge` (S256), `state`; stores in session.
3. Redirect to provider's `AuthUrl` with PKCE + state params.
4. Provider calls back `/auth/callback`. Framework validates state, exchanges code+verifier for access token.
5. `OnCreatingTicket`: hub fetches user info from `UserUrl` with access token; upserts `HubUser`; adds claims to ticket.
6. Cookie issued for `mcahub_session` (SameSite=Lax, HttpOnly, 30-day sliding).

## Trusted Redirect Origin

CallbackPath is relative (`/auth/callback`). Absolute URI built by framework from request host. Under `MCAHUB_BEHIND_PROXY=1`, `ForwardedHeadersMiddleware` trusts `X-Forwarded-Proto/Host` from all sources (KnownIPNetworks + KnownProxies both cleared). Safe only when hub is reachable exclusively via the proxy.

## Open-Redirect Defense

`Auth.Local(string?)` at Auth.cs:270–271: accepts only `url[0]=='/' && (url.Length==1 || url[1]!='/')`. Blocks absolute and protocol-relative URLs.

## Dev Login Guard

Dev login gated on `MCAHUB_DEV_LOGIN` env var only. No code-level production guard. Both `cfg.Oauth` and `cfg.DevLogin` can be true simultaneously (unusual but not blocked). See design review finding §2.4.

## HubDb Schema (Db record)

```
List<HubUser> Users        — id (provider:sub), login, name, avatar, createdAt
List<TokenRecord> Tokens   — hash (SHA-256 hex), prefix (14 chars), userId, label, createdAt, lastUsedAt?
List<HubRepoMeta> Repos    — name, ownerId, private, createdAt
List<Collab> Collabs       — repo, userId, role (read|write|maintain|admin)
List<Team> Teams           — name, ownerId, Members (List<string> userIds), createdAt
List<TeamGrant> TeamGrants — repo, teamName, role (read|write|maintain|admin)
```

No AuditEntry list yet (roadmap R2).

## Known Dual-Rank Defect

`Auth.Rank` and `HubDb.Rank` are duplicated without sharing. `Auth.Rank` includes `owner=5`; `HubDb.Rank` does not (max is `admin=4`). Latent maintenance hazard; no current exploit path because `IsRole` blocks `"owner"` from being stored as a collab/team role.
