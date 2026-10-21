# Wharf

A drop point for files: one authoritative server and browser clients that keep
a full local replica. The client renders from its replica, works offline, and
can re-seed a wiped server.

The contract is in [`docs/api.md`](docs/api.md). This repository is the
implementation of that contract.

## Layout

```
src/Wharf/           Blazor Web App host: SSR, minimal API, SQLite, static assets
src/Wharf.Client/    Interactive WebAssembly components (the file manager)
src/Wharf/wwwroot/   PWA manifest, icons, service worker, replica JS modules
docs/api.md          the API contract
Wharf.slnx           solution
```

The server and the app share one origin, so there is no CORS in production.

## Run

```sh
dotnet run --project src/Wharf/Wharf.csproj
# open http://localhost:5080
```

`Properties/launchSettings.json` selects the Development environment and port
5080. The first run creates `src/Wharf/data/` (override with `WHARF_DATA_DIR`).

Publish for a deployment:

```sh
dotnet publish src/Wharf/Wharf.csproj -c Release -o publish
WHARF_DATA_DIR=/workspace/.wharf ./publish/wharf --urls http://0.0.0.0:5080
```

`0.0.0.0` is what Gitpod/Ona's port proxy needs. On a local port-forward
(`ssh -L 5080:localhost:5080`) the default loopback listener is enough.

> Development static assets (`_framework/*`) are served by `MapStaticAssets`.
> Run through `dotnet run` (which uses the Development profile) or publish;
> a Production build started with `--no-build` does not have the development
> static-asset manifest.

## Authentication and origin

The app implements no authentication. Put it behind a trusted proxy (the
Gitpod/Ona port forward, a tunnel, or a local SSH forward) or keep it on
loopback. The API still supports an optional bearer token for other
deployments, but the Blazor client has no token UI, so leave `Wharf:Token`
empty when using it.

The client is a full replica, and browser storage is keyed by **origin**. For
an installed PWA to survive a recreated workspace, use a stable origin:

- a tunnel with a fixed hostname, or
- a fixed local port-forward such as `http://localhost:5080`.

A platform forwarded URL changes when a workspace is recreated, and the
installed app and its OPFS replica stay behind on the old origin.

## Configuration

Configuration comes from `src/Wharf/appsettings.json`, environment variables
(`Wharf__Token`), and the command line (`--Wharf:Token=...`):

| Key | Default | Meaning |
| --- | --- | --- |
| `Wharf:DataDirectory` | `data` | Database, blobs, and temp files |
| `Wharf:Token` | empty | Full-access bearer token; empty disables auth |
| `Wharf:ReadToken` | empty | Optional read-only token |
| `Wharf:MaxFileBytes` | 10 GiB | Per-file limit |
| `Wharf:MaxPageSize` | 5000 | Manifest page limit |
| `Wharf:TombstoneRetentionDays` | 30 | How long deletions stay in the delta |
| `Wharf:SessionTtlHours` | 24 | Resumable upload session lifetime |
| `Wharf:GcIntervalMinutes` | 60 | Tombstone/blob/session sweep interval |
| `Wharf:ChunkSize` | 8 MiB | Chunk hint returned by upload sessions |
| `Wharf:DevOrigin` | empty | Optional dev CORS origin |

`WHARF_DATA_DIR` overrides the data directory. URLs come from `--urls` or
`ASPNETCORE_URLS`; the default is `http://127.0.0.1:5080`.

## What the server does

- Blobs are immutable and content-addressed: `objects/sha256/<shard>/<hex>`.
- A SQLite index maps paths to blob hashes and carries a monotonic `seq`.
- `GET /api/v1/manifest?since=N` returns the changes after `N`; `since=0` is a
  full live snapshot. Clients keep one integer cursor.
- `PUT /api/v1/files/<path>` streams the raw body; `on_conflict=rename`
  yields `name (2).ext` instead of overwriting.
- Large uploads use `POST /api/v1/uploads` plus `PATCH` with `Upload-Offset`.
- Downloads support `Range`, `ETag`, and immutable caching on the blob route.
- `GET /api/v1/events` is an SSE poke (cursor only).
- GC purges expired tombstones, advances the minimum valid cursor, deletes
  unreferenced blobs, and drops expired upload sessions.

## What the client does

- The Blazor Web App server-renders the shell and a skeleton; the file manager
  is an Interactive WebAssembly component that hydrates and then reads the
  replica. The server never renders the file list, so there is one source of
  truth.
- The replica engine (`wwwroot/js/store.js` and `sync.js`) keeps the manifest
  in IndexedDB and file bodies in OPFS, keyed by content hash. Blazor reaches
  it through `wwwroot/js/interop.js`; file bytes never cross the interop
  boundary.
- Files dragged into the app go to `inbox/`; the server assigns a unique name
  on collision and the dropped bytes are written into the local replica.
- Downloads come from OPFS, so they work offline.
- `instance_id` (or a cursor ahead of the server) detects a reset and
  automatically re-uploads the replica, skipping blobs the new server already
  has. Conflicts are kept as `name (2).ext`.
- The service worker caches the shell and framework assets on first use;
  `/api/*` is network-only.
- The install button appears when the browser offers `beforeinstallprompt`;
  iOS gets a Share → Add to Home Screen hint.

## Smoke test

```sh
B=http://127.0.0.1:5080

curl -s $B/healthz
curl -s "$B/api/v1/manifest"
printf 'hello\n' | curl -s -X PUT --data-binary @- \
  "$B/api/v1/files/inbox/hello.txt?on_conflict=rename"
curl -s "$B/api/v1/manifest"
curl -s -o - "$B/api/v1/files/inbox/hello.txt"
curl -s -X DELETE "$B/api/v1/entries/inbox/hello.txt"
```

If `Wharf:Token` is set, add `-H 'Authorization: Bearer <token>'` to each API
call.

## Known gaps

- No TLS and no quota enforcement; put it behind a TLS proxy and a request
  limit before exposing it. `507` is reserved but nothing returns it.
- Blazor WebAssembly ships a sizable first-load payload. The service worker
  makes repeat loads cheap, but the first visit is heavier than the previous
  vanilla-JS client.
- Replication is subject to browser storage quotas and eviction. The client
  asks for persistent storage, but iOS can still evict it; a client that never
  downloaded a file cannot restore it.
- The service worker precaches the published shell and framework assets on
  install, so the very first visit must be online; after that the app loads
  offline.
- No replication between drop points.
- The SQLite index is single-node by design.
- The server is still a single failure domain. A full client replica helps
  only while at least one replica survives; back up `wharf.db` and `objects/`.

## License

MIT. See [LICENSE](LICENSE).
