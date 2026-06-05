# mcadiff-hub

**GitHub, for Minecraft worlds.** A self-hostable platform that hosts [`mcadiff`](https://github.com/BangRocket/mcadiff)
repositories (version-controlled worlds) over HTTP and gives them a web face: push a world, browse its
backup timeline, and see the **semantic diff** of any backup — which chunks, blocks, and entities
changed, and *where the griefing happened* — none of which a generic git host can show.

If mcadiff is git for worlds, this is the hub you push them to.

## What it does

- **Hosts worlds over HTTP** — `mcadiff clone | fetch | push http://<hub>/r/<name>` works against it.
  Pushing to a new name **auto-creates** the world.
- **Web UI** — a repo list, each world's branches + backup timeline, a backup view that combines the
  file/chunk/block diff with a "what happened here" grief summary (destroyed / placed / replaced, the
  destruction bounding box, the most-destroyed blocks), a **compare-any-two-backups** view (the same
  forensics between arbitrary backups), and a **world explorer** (players + find an entity / block entity /
  sign).
- **In-process** — references the mcadiff core directly, so it renders the real `WorldDiff` / `GriefReport`
  structures instead of scraping CLI text.
- **Accounts (optional)** — OAuth sign-in (GitHub by default, any OAuth2 provider via config), per-user
  **personal access tokens** for the CLI, and **public/private** worlds. Off by default; the hub stays a
  zero-config local tool until you hand it an OAuth app.

## Run it

Needs the **.NET 10 SDK** and the `mcadiff` repo checked out as a sibling directory (the project
references `../mca-git/src/McaDiff`).

```sh
dotnet run --project src/McadiffHub          # serves http://localhost:5080
```

Then point a client at it:

```sh
mcadiff init MyWorld.mcagit --worktree path/to/world
mcadiff -C MyWorld.mcagit commit -m "first backup"
mcadiff -C MyWorld.mcagit push http://localhost:5080/r/myworld main   # auto-creates "myworld"
```

Open <http://localhost:5080> to browse it.

### Configuration

| Setting | Env var | Default | Purpose |
|---|---|---|---|
| Data dir | `MCAHUB_DATA` | `data/repos` | Where hosted `<name>.mcagit` repos live. |
| World cache | `MCAHUB_CACHE` | sibling `cache/` | Materialized worlds for the explorer (one checkout per immutable backup). |
| Account DB | `MCAHUB_DB` | sibling `hub.json` | Users, hashed tokens, repo ownership/visibility. |
| Push token | `MCAHUB_TOKEN` | (none) | A shared/master token. In open mode it gates writes; in accounts mode it's an admin bypass. |
| Bind URL | `ASPNETCORE_URLS` | `http://localhost:5080` | Put it behind a reverse proxy for TLS. |

### Auth modes

The hub runs in one of three modes, chosen by what you configure:

- **Open** *(default — nothing set)*: anonymous, every world public, open push. Great for a trusted LAN.
- **Token** *(`MCAHUB_TOKEN` set)*: reads anonymous, writes need `--token <that token>`.
- **Accounts** *(OAuth configured)*: real users sign in via OAuth; each gets personal access tokens for the
  CLI; worlds can be **private**. The CLI can't run a browser redirect, so `mcadiff push/clone` against a
  private world uses a personal access token (mint one at `/account`) — exactly how GitHub handles `git push`.

| Accounts env var | Default | Purpose |
|---|---|---|
| `MCAHUB_OAUTH_CLIENT_ID` / `MCAHUB_OAUTH_CLIENT_SECRET` | — | Your OAuth app credentials. Setting both turns accounts on. |
| `MCAHUB_OAUTH_PROVIDER` | `github` | Label namespacing user ids (`github:1234`). |
| `MCAHUB_OAUTH_AUTH_URL` / `_TOKEN_URL` / `_USER_URL` | GitHub's | Override to use GitLab, Gitea, or any OAuth2 provider. |
| `MCAHUB_OAUTH_SCOPE` | `read:user` | Scopes requested at sign-in. |
| `MCAHUB_DEV_LOGIN` | (off) | ⚠ Insecure local login at `/auth/dev` for evaluating accounts without an OAuth app. **Never on a public host.** |

Register the OAuth app's callback as `http(s)://<your-host>/auth/callback`. When accounts are on, the first
authenticated push to an unowned world claims ownership of it (this is how worlds pushed before you enabled
accounts get adopted).

## How it works

- `RepoStore` — hosts bare mcadiff repos under the data dir; repo names are validated so they can't
  escape it.
- `Transport` — maps the mcadiff HTTP protocol (`/r/{repo}/info/refs`, `/objects`, `/pack`,
  `/refs/heads/{branch}`, `/have`) onto a per-request `RemoteService` — the same handler `mcadiff serve`
  uses. Writes are token-gated; the fast-forward check and pack hash-verification happen server-side in
  the mcadiff core.
- `Pages` — server-rendered HTML (no SPA): the repo list, a repo's timeline, a backup's
  `RepoDiffer`-computed diff + `GriefReport` summary, a compare-any-two-backups view, and a world
  explorer (players + `WorldQuery` find).
- `WorldCache` — materializes a backup to `cache/<repo>/<commit>` once (commits are immutable) so the
  dir-based `WorldQuery` reads a real world without re-checking-out on every page view.
- `Auth` + `HubDb` — identity and the tiny JSON account store. `Auth` wires the framework's cookie + OAuth
  handlers (no third-party package), splits web identity (cookie) from CLI identity (Bearer PAT), and holds
  the shared `CanRead`/`CanWrite` rules used by both the web pages and the transport. `HubDb` keeps users,
  **hashed** tokens (the plaintext is shown once and never stored), and per-repo owner/visibility.

## Status & roadmap

Shipped: hosting + browse + per-backup diff + grief forensics, **compare any two backups**, a
**world explorer** (players + find an entity / block entity / sign, backed by a materialize-once world
cache), and **accounts** (OAuth sign-in, per-user tokens, public/private worlds). Natural next steps
(some shared with the mcadiff GUI RFC):

- Collaborators / teams (v1 access control is owner-only beyond public read) and antiforgery tokens.
- Deeper world-state pages — `inspect` a chunk's full NBT, region heatmaps, per-player inventory views.
- One-click **restore** (waits on mcadiff's atomic-swap checkout) and a "preview into a temp folder" view.
- A rendered map thumbnail per backup and a time-machine scrubber.
- Package the mcadiff core as a proper library so the reference isn't a sibling-path coupling.

## License

GPL-3.0 (matching mcadiff). See [LICENSE](LICENSE).
