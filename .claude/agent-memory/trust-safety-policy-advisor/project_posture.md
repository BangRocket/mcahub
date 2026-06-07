---
name: project-posture
description: Overall trust-and-safety posture of mcahub — current state, public surfaces, and what's missing before hosted/multi-tenant launch
metadata:
  type: project
---

Current state (as of 2026-06-05 audit): Six-friends-on-a-LAN self-hostable app with an accounts mode (OAuth, PATs, public/private worlds, collaborators, teams) that points directly toward a hosted multi-tenant future.

**Why:** The README roadmap and accounts-mode feature set make the hosted future explicit. Policy gaps identified below are largely fine for a trusted LAN but block a public hosted offering.

**How to apply:** Treat every finding as "acceptable now, must fix before public launch" unless explicitly noted otherwise.

## Auth modes
- Open (default): fully anonymous, all worlds public
- Token: reads anonymous, writes need shared master token
- Accounts (OAuth configured): real users, PATs, public/private worlds

## Key data stored in hub.json (HubDb.cs)
- HubUser: Id (provider:sub), Login, Name, Avatar, CreatedAt
- TokenRecord: Hash (SHA-256 of PAT), Prefix, UserId, Label, CreatedAt, LastUsedAt
- HubRepoMeta: Name, OwnerId, Private (bool), CreatedAt
- Collab: Repo, UserId, Role
- Team: Name, OwnerId, Members (list), CreatedAt
- TeamGrant: Repo, TeamName, Role

## Public surfaces that exist
1. Home page `/` — lists all public worlds (no report/takedown story)
2. Repo page `/r/{repo}` — backup timeline, grief report, owner login shown
3. Commit/backup page `/r/{repo}/commit/{hash}` — full diff, grief summary
4. Compare page `/r/{repo}/compare/{a}/{b}` — diff between any two backups
5. World explorer `/r/{repo}/world/{ref}` — EXPOSES: player coords (X,Y,Z), dimension, health, entity locations, chest item counts, sign text (full content)
6. Map page `/r/{repo}/map/{ref}.png` — top-down rendered map showing base layout
7. Timeline scrubber `/r/{repo}/timeline` — historical maps across all backups

## Critical policy gaps found
1. NO account/data deletion path (HubDb has no DeleteUser, no cascade delete for owned repos)
2. NO abuse report / takedown flow for any public surface
3. Push default is isPrivate:false (Transport.cs:94) — public-by-default
4. World explorer exposes player coords, health, entity/chest locations, sign text with no consent or redaction option
5. No AUP, no per-user quota as governance, no user-ban capability
6. No minor/COPPA considerations — Minecraft skews under-13; public worlds can reveal a kid's location

## Settled decisions (LAN-only at launch)
- Open mode (no auth) is intentionally unauthenticated — by design, not a bug
- MCAHUB_DEV_LOGIN is insecure eval-only, gated off by default — by design

## Code locations for T&S review
- HubDb.cs: full account store — no DeleteUser method exists anywhere in the file
- Transport.cs:94: `db.EnsureRepo(repo, uid, isPrivate: false)` — the public-by-default push line
- Pages.cs:351-352: player coord/health rendering in World() handler
- Pages.cs:368-370: entity, block-entity (chest item counts), sign text rendering
- Auth.cs:102-106: what OAuth data is stored — login, name, avatar_url/picture
