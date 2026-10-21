# Wharf

A drop point for files: one authoritative server, browser clients that upload
and pull on demand. No peer-to-peer reconciliation, no browser-side replica.

The contract is in [`docs/api.md`](docs/api.md). This repository is the
implementation of that contract.

## Layout

```
server/        .NET 10 minimal API + SQLite
web/           installable PWA (plain HTML/CSS/JS)
docs/api.md    the API contract
data/          runtime state (gitignored): wharf.db, objects/, tmp/
```

The PWA is served from the same origin as the API, so there is no CORS in
production. In development the server finds `web/` next to itself; `WHARF_WEB_ROOT`
overrides that.

## Run

```sh
dotnet run --project server/Wharf.Server.csproj
# open http://127.0.0.1:5080
```

Set a token before exposing the server beyond loopback:

```sh
Wharf__Token=change-me dotnet run --project server/Wharf.Server.csproj
```

With no token configured the API is open and logs a warning. Do not run that
way on a reachable interface.

Configuration comes from `server/appsettings.json`, environment variables
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

`WHARF_DATA_DIR` and `WHARF_WEB_ROOT` override the data and web roots. URLs
come from `--urls` or `ASPNETCORE_URLS`; the default is
`http://127.0.0.1:5080`.

## What the server does

- Blobs are immutable and content-addressed: `objects/sha256/<shard>/<hex>`.
- A SQLite index maps paths to blob hashes and carries a monotonic `seq`.
- `GET /api/v1/manifest?since=N` returns the changes after `N`; `since=0` is a
  full live snapshot. Clients keep one integer cursor.
- `PUT /api/v1/files/<path>` streams the raw body; `on_conflict=rename`
  yields `name (2).ext` instead of overwriting.
- Large uploads use `POST /api/v1/uploads` plus `PATCH` with `Upload-Offset`.
- Downloads support `Range`, `ETag`, and immutable caching on the blob route.
- `GET /api/v1/events` is an SSE poke (cursor only). The PWA currently polls
  every 5 s; SSE is available to other clients.
- GC purges expired tombstones, advances the minimum valid cursor, deletes
  unreferenced blobs, and drops expired upload sessions.

## What the PWA does

- Keeps the app shell in the service worker cache and data in OPFS/IndexedDB.
- Polls the manifest delta and keeps a **full replica**: every live file is
  downloaded into OPFS, keyed by its content hash, with the manifest in
  IndexedDB.
- Drags-and-drops files to `inbox/`, with progress; the server assigns a
  unique name on collision, and the dropped file is written into the local
  replica immediately.
- Serves downloads from OPFS, so they work offline. A missing blob is fetched
  from the server first.
- Detects a server reset by `instance_id` (or a cursor ahead of the server) and
  **automatically re-uploads the replica**, skipping blobs the new server
  already has. Conflicts are kept as `name (2).ext` rather than overwritten.
- Stores the bearer token in `localStorage` so an installed replica can sync
  unattended.

## Smoke test

```sh
B=http://127.0.0.1:5080
A='Authorization: Bearer change-me'

curl -s $B/healthz
curl -s -H "$A" "$B/api/v1/manifest"
printf 'hello\n' | curl -s -X PUT -H "$A" --data-binary @- \
  "$B/api/v1/files/inbox/hello.txt?on_conflict=rename"
curl -s -H "$A" "$B/api/v1/manifest"
curl -s -H "$A" -o - "$B/api/v1/files/inbox/hello.txt"
curl -s -X DELETE -H "$A" "$B/api/v1/entries/inbox/hello.txt"
```

## Known gaps

- No TLS and no quota enforcement yet; put it behind a TLS proxy and a reverse
  proxy limit before exposing it. `507` is reserved but nothing returns it.
- SSE is implemented but the PWA polls.
- Replication is subject to browser storage quotas and eviction. The PWA asks
  for persistent storage, but iOS can still evict it; a client that never
  downloaded a file cannot restore it.
- No replication between drop points.
- The SQLite index is single-node by design.
- The server is still a single failure domain. A full client replica helps
  only while at least one replica survives; back up `wharf.db` and `objects/`
  as well.

## License

MIT. See [LICENSE](LICENSE).
