# mcadiff-hub

**GitHub, for Minecraft worlds.** A self-hostable platform that hosts [`mcadiff`](https://github.com/BangRocket/mcadiff)
repositories (version-controlled worlds) over HTTP and gives them a web face: push a world, browse its
backup timeline, and see the **semantic diff** of any backup — which chunks, blocks, and entities
changed, and *where the griefing happened* — none of which a generic git host can show.

If mcadiff is git for worlds, this is the hub you push them to.

## What it does

- **Hosts worlds over HTTP** — `mcadiff clone | fetch | push http://<hub>/r/<name>` works against it.
  Pushing to a new name **auto-creates** the world.
- **Web UI** — a repo list, each world's branches + backup timeline, and a backup view that combines the
  file/chunk/block diff with a "what happened here" grief summary (destroyed / placed / replaced, the
  destruction bounding box, the most-destroyed blocks).
- **In-process** — references the mcadiff core directly, so it renders the real `WorldDiff` / `GriefReport`
  structures instead of scraping CLI text.

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
| Push token | `MCAHUB_TOKEN` | (none) | If set, writes require `Authorization: Bearer <token>` (clients pass `--token`). Reads stay anonymous. |
| Bind URL | `ASPNETCORE_URLS` | `http://localhost:5080` | Put it behind a reverse proxy for TLS. |

## How it works

- `RepoStore` — hosts bare mcadiff repos under the data dir; repo names are validated so they can't
  escape it.
- `Transport` — maps the mcadiff HTTP protocol (`/r/{repo}/info/refs`, `/objects`, `/pack`,
  `/refs/heads/{branch}`, `/have`) onto a per-request `RemoteService` — the same handler `mcadiff serve`
  uses. Writes are token-gated; the fast-forward check and pack hash-verification happen server-side in
  the mcadiff core.
- `Pages` — server-rendered HTML (no SPA): the repo list, a repo's timeline, and a backup's
  `RepoDiffer`-computed diff + `GriefReport` summary.

## Status & roadmap

v1 is the hosting + browse + diff experience. Natural next steps (some shared with the mcadiff GUI RFC):

- Accounts / per-repo access control (v1 is single shared push token).
- World-state pages — players, `inspect`, `find`, and a `where-changed` view between any two backups.
- One-click **restore** (waits on mcadiff's atomic-swap checkout) and a "preview into a temp folder" view.
- A rendered map thumbnail per backup and a time-machine scrubber.
- Package the mcadiff core as a proper library so the reference isn't a sibling-path coupling.

## License

GPL-3.0 (matching mcadiff). See [LICENSE](LICENSE).
