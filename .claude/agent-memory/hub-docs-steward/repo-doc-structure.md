---
name: repo-doc-structure
description: Section layout of README.md, SECURITY.md, CLAUDE.md — where features, env vars, trust boundaries, and invariants live
metadata:
  type: reference
---

## README.md

- **Feature list ("What it does")** — lines 11-25: bullet list of shipped features
- **Run it / quickstart** — lines 27-44: dotnet run command + mcadiff client commands
- **Configuration table (core)** — lines 49-55: MCAHUB_DATA, MCAHUB_CACHE, MCAHUB_DB, MCAHUB_TOKEN, ASPNETCORE_URLS
- **Auth modes** — lines 58-70: open / token / accounts narrative + role ladder
- **Accounts env var table** — lines 72-78: OAuth vars + MCAHUB_DEV_LOGIN
- **GitHub OAuth quickstart** — lines 83-105
- **How it works** — lines 107-138: per-subsystem prose (RepoStore, Transport, Pages, WorldCache, MapRenderer, Auth/HubDb)
- **Status & roadmap** — lines 140-151

## SECURITY.md

- **Trust boundary table** — lines 21-28: 6 rows (network-read, network-write, world-data, web-session, web-actions, config)
- **Start here (files that matter)** — lines 30-49: per-file bullet list with key symbols named
- **Controls already in place** — lines 51-65
- **Where I'd look** — lines 67-98: 8 numbered soft-spot items
- **By-design / out of scope** — lines 100-105

## CLAUDE.md

- **Hard dependency** — lines 11-21: submodule requirement (was sibling checkout pre-ADR-0006)
- **Commands** — lines 23-36: build/run/render + env var enumeration
- **Architecture** — lines 38-73: per-file subsystem notes
- **Security invariants** — lines 75-87: 6 bullet invariants

## Source files
- `src/McaHub/Auth.cs` — CanRead, CanWrite, CanManageSettings, CanManagePeople, CsrfField, CsrfOk, Identify, Local (open-redirect guard)
- `src/McaHub/HubDb.cs` — RoleOf, EnsureRepo, token hashing
- `src/McaHub/RepoStore.cs` — IsValidName regex
- `src/McaHub/Transport.cs` — all /r/{repo}/* network protocol routes
- `src/McaHub/Pages.cs` — all web UI routes including /teams, /account
- `src/McaHub/Program.cs` — LoadDotEnv, MCAHUB_BEHIND_PROXY, all env var reads, MCAHUB_MAPS
- `src/McaHub/MapCache.cs`, `MapRenderer.cs`, `WorldCache.cs` — untrusted-data path
