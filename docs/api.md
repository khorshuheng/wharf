# Wharf API — v1 (draft)

A drop point for files: one authoritative server, many client-only replicas
(PWA browsers). The server is the only writer. Clients upload, list, and
download; they never reconcile.

**Status:** draft contract. No implementation yet. Review this before the
server or PWA is written.

---

## 1. Scope

Wharf is the server side of a file drop box:

- A single authoritative namespace of paths.
- Immutable, content-addressed blobs as the storage unit.
- A mutable path index in SQLite as the only mutable state.
- Cursor-based manifest deltas for clients.
- Each client keeps a full replica: the manifest in IndexedDB and file bodies
  in OPFS.
- A client that detects a server reset automatically uploads its replica back.

### Non-goals (v1)

- Peer-to-peer replication between drop points.
- Client-side conflict resolution (LWW, tombstones, vector clocks).
- Offline writes (a replica serves reads offline, but uploads need the
  server).
- Multipart/resumable uploads beyond the session protocol in §7.
- Sharing, permissions per path, or multi-tenant namespaces.

---

## 2. Conventions

### Base path and versioning

All routes are under `/api/v1`. A breaking change bumps the version; additive
fields and endpoints do not.

The PWA is served from the same origin as the API (`/` for the app, `/api/v1`
for the API). No CORS is required in production. Development servers may allow
one explicit origin; wildcard origins are not permitted.

### Authentication

Every `/api/v1` request requires:

```http
Authorization: Bearer <token>
```

Missing or invalid credentials return `401` with
`WWW-Authenticate: Bearer realm="wharf"`.

Tokens may carry a `scope` claim:

| Scope   | Grants                                              |
| ------- | --------------------------------------------------- |
| `read`  | manifest, entries, downloads, events                |
| `write` | everything in `read`, plus uploads and deletions    |

A token without a scope is treated as `read write`.

Health endpoints (§10) are unauthenticated and should be bound internally.

### Content types

- JSON bodies: `application/json; charset=utf-8`.
- File bodies: the file's own media type, or `application/octet-stream`.
- Upload session chunks: `application/offset+octet-stream`.
- Server-sent events: `text/event-stream`.

JSON responses always use UTF-8. Clients must ignore unknown fields.

### Timestamps

- `mtime`: Unix epoch milliseconds, UTC, integer.
- `updated_at`: RFC 3339 / ISO 8601, UTC, with `Z`.

The server never infers `mtime` from wall-clock time when the client supplies
`X-File-Mtime`; it does so only when the header is absent.

### Paths

Paths are the user-visible namespace, distinct from blob hashes.

- Relative, `/`-separated, UTF-8, no leading or trailing slash.
- Each segment is percent-encoded in URLs. `%2F` is **not** a valid segment
  character; the slash is the separator.
- Forbidden segments: empty, `.`, `..`, and anything containing NUL.
- Maximum 255 bytes per segment, 4096 bytes per path.
- Normalized to Unicode NFC on ingest. Invalid UTF-8 is rejected (`400`).
- Case-sensitive by default. If the server is configured case-insensitive,
  a path that collides only by case returns `409` with
  `details.conflict_path`.
- The prefix `.wharf/` is reserved for server use and rejected in requests.

### Cursors

Every mutation advances a single monotonic integer, `seq`. Cursors are `seq`
values and are opaque to clients: persist and return them as integers, never
do arithmetic on them or assume they are contiguous.

### Errors

All non-2xx JSON responses use one envelope:

```json
{
  "error": {
    "code": "conflict",
    "message": "Path already exists",
    "details": { "conflict_path": "inbox/report.pdf" }
  }
}
```

`message` is human-readable and may change. Branch on `code`.

| Code                    | HTTP | Meaning                                      |
| ----------------------- | ---- | -------------------------------------------- |
| `bad_request`           | 400  | Malformed path, JSON, or header              |
| `unauthorized`          | 401  | Missing or invalid token                     |
| `forbidden`             | 403  | Valid token, insufficient scope              |
| `not_found`             | 404  | No such entry, blob, or upload session       |
| `conflict`              | 409  | Path or upload-offset conflict               |
| `cursor_expired`        | 410  | Client cursor is older than retention        |
| `session_expired`       | 410  | Upload session expired or already completed  |
| `precondition_failed`   | 412  | `If-Match` / `If-None-Match` failed          |
| `payload_too_large`     | 413  | Body exceeds the per-file limit              |
| `range_not_satisfiable` | 416  | `Range` outside the blob                     |
| `unsupported_media_type`| 415  | Body type not accepted for this endpoint     |
| `quota_exceeded`        | 507  | Storage quota would be exceeded              |
| `internal`              | 500  | Unexpected server error                      |

### Idempotency and retries

Uploads are safe to retry: identical bytes deduplicate to the same blob, and
a retried `PUT` converges to the same path. A retry does advance `seq`, so
clients may observe a duplicate manifest delta; that is harmless.

---

## 3. Data model

### Entry

An entry is one path in the namespace.

```json
{
  "path": "inbox/report.pdf",
  "type": "file",
  "blob": "sha256:9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
  "size": 48213,
  "mtime": 1729450000000,
  "seq": 1042,
  "deleted": false,
  "content_type": "application/pdf",
  "updated_at": "2025-10-21T12:00:00Z"
}
```

| Field          | Notes                                                        |
| -------------- | ------------------------------------------------------------ |
| `path`         | Canonical path (§2)                                          |
| `type`         | `file` or `directory`                                        |
| `blob`         | `sha256:<64 lowercase hex>`, or `null` for directories       |
| `size`         | Bytes; `0` for directories                                   |
| `mtime`        | Unix ms; for directories, the creation/update time           |
| `seq`          | Last mutation sequence number                                |
| `deleted`      | `true` for tombstones in a delta; never `true` in a full sync|
| `content_type` | Stored media type; `application/octet-stream` if unknown     |
| `updated_at`   | Server time of the last index mutation                       |

An entry's HTTP `ETag` is `"<seq>"`, quoted. This is stable across reads and
changes on any mutation.

### Blob

A blob is immutable content addressed by its SHA-256 digest. Identical content
uploaded at two paths is stored and served once. A blob has no name, type, or
mtime of its own; those belong to entries.

JSON represents a blob as `"sha256:<hex>"`. In URLs it is split into a
scheme and a digest: `/api/v1/blobs/sha256/<hex>`.

### Cursor

`seq` of the last mutation the client has applied. `since=0` means "from the
beginning" and yields a full live snapshot.

---

## 4. Manifest

### `GET /api/v1/manifest?since=<seq>&limit=<n>`

Returns entries changed after `since`, ordered by `seq` ascending.

Query parameters:

| Name    | Default | Limits        | Meaning                              |
| ------- | ------- | ------------- | ------------------------------------ |
| `since` | `0`     | `>= 0`        | Last applied cursor                  |
| `limit` | `500`   | `1..5000`     | Maximum entries in this page         |

`since=0` is a **full sync**: the response contains the complete set of live
entries, excluding tombstones. Any path the client already knows but which does
not appear in a complete full sync is gone and must be dropped.

`since>0` is a **delta**: the response contains every mutation after that
cursor, including tombstones (`"deleted": true`). Clients apply them in `seq`
order.

Response:

```json
{
  "instance_id": "fb02d5608d294718346b05fedb8a444a",
  "entries": [ { "...": "..." } ],
  "next": 1042,
  "has_more": false
}
```

- `instance_id` — identity of the database, generated when it is created. A
  client persists it and treats any change as a server reset, even when the
  cursor is numerically still valid.
- `next` — the cursor to persist after applying this page. It is the maximum
  `seq` in the page, or `since` when the page is empty.
- `has_more` — `true` when more entries exist at or after `next`; repeat the
  request with `since=next` until it is `false`.

A full sync is complete only after every page has been fetched; do not apply
"absent means deleted" until the last page has `has_more: false`.

If `since` is older than the server's retention window, **or is ahead of the
server's current sequence** (which can only happen after a reset or rollback),
respond `410`:

```json
{
  "error": {
    "code": "cursor_expired",
    "message": "Cursor is older than the retention window",
    "details": { "current": 5000 }
  }
}
```

The client then restarts with a full sync. If it holds a replica, it re-seeds
instead (see §11). An `instance_id` change is handled the same way even when
the request succeeds.

Headers: `Cache-Control: no-store`.

---

## 5. Events (optional push)

### `GET /api/v1/events`

An SSE stream that signals changes. It is a **poke only**: it carries the new
cursor, never the entries themselves. Clients always re-read the manifest, so
a lost event cannot lose data.

```http
GET /api/v1/events HTTP/1.1
Accept: text/event-stream
Last-Event-ID: 1042
```

On connect the server immediately emits the current cursor, so a client can
detect changes missed while disconnected:

```text
retry: 3000

id: 1042
event: change
data: {"seq":1042}

: ping
```

- `id` is the cursor; clients should send it back as `Last-Event-ID` on
  reconnect.
- `event: change` means "the cursor advanced to `data.seq`; fetch the delta".
- A keepalive comment (`: ping`) is sent at least every 15 s.
- If `Last-Event-ID` is too old to serve a delta from, emit `reset` instead:

```text
event: reset
data: {"seq":5000,"reason":"cursor_expired"}
```

The client then performs a full sync from `since=0`.

Polling the manifest remains a valid alternative to SSE. The server does not
require either.

---

## 6. Entries and directories

### `GET /api/v1/entries/<path>`

Returns the single entry.

```http
ETag: "1042"
```

`404` if the path does not exist or is deleted. `Cache-Control: no-cache`.

### `DELETE /api/v1/entries/<path>?recursive=true|false`

Soft-deletes a path. Idempotent: deleting an already-deleted path returns
`204`. The entry remains visible in deltas as a tombstone until the server's
retention window expires; the PWA simply drops it from view.

- Files: always allowed.
- Directories: `409` if non-empty and `recursive` is not `true`.
- `If-Match: "<seq>"` is honored; a mismatch returns `412`.

Response: `204 No Content`.

### `PUT /api/v1/directories/<path>`

Creates an empty directory. Idempotent: returns `200` if it exists and is a
directory, `201` if created, `409` if the path exists as a file.

Directories are also created implicitly when a file is uploaded beneath a
path that does not yet have directory entries; those implicit parents appear in
the manifest as `type: "directory"` entries.

```json
{ "entry": { "path": "inbox", "type": "directory", "...": "..." } }
```

---

## 7. Uploads

Two mechanisms: a single-request `PUT` for ordinary files, and an upload
session for large files and resumption. Both commit atomically: content is
written to a temporary file, flushed, renamed into the blob store, and only
then is the index updated. A dropped connection leaves no entry behind.

### 7.1 Single-request upload

#### `PUT /api/v1/files/<path>`

The request body is the raw file content, not multipart.

```http
PUT /api/v1/files/inbox/report.pdf HTTP/1.1
Authorization: Bearer ...
Content-Type: application/pdf
Content-Length: 48213
X-File-Mtime: 1729450000000
Content-Digest: sha-256=:3q2+7w...:
If-None-Match: *
```

Headers:

| Header          | Required | Meaning                                          |
| --------------- | -------- | ------------------------------------------------ |
| `Content-Type`  | no       | Stored as the entry's `content_type`             |
| `Content-Length`| no       | If absent, the body is read until EOF            |
| `X-File-Mtime`  | no       | Unix ms; server uses now if absent               |
| `Content-Digest`| no       | RFC 9530 digest; if present, verified on commit  |
| `If-Match`      | no       | `"<seq>"`; replaces only if the entry is unchanged|
| `If-None-Match` | no       | `*`; creates only, `412` if the path exists      |

Query parameters:

| Name          | Values                    | Default   |
| ------------- | ------------------------- | --------- |
| `on_conflict` | `replace`, `rename`, `fail` | `replace` |

- `replace` — overwrite the existing entry (subject to `If-Match`).
- `rename` — if the path exists, atomically assign `name (2).ext`,
  `name (3).ext`, … and create that instead.
- `fail` — return `409`.

Responses:

- `201 Created` — new path. `Location: /api/v1/entries/<path>`.
- `200 OK` — replaced. `Location` as above.
- The body is `{ "entry": { ... } }`. With `on_conflict=rename`, `entry.path`
  is the assigned path; clients must use it.

```json
{
  "entry": {
    "path": "inbox/report (2).pdf",
    "type": "file",
    "blob": "sha256:9f86...",
    "size": 48213,
    "mtime": 1729450000000,
    "seq": 1043,
    "deleted": false,
    "content_type": "application/pdf",
    "updated_at": "2025-10-21T12:00:01Z"
  }
}
```

`ETag: "<seq>"` is set on the response.

### 7.2 Upload sessions (large files, resumable)

#### `POST /api/v1/uploads`

```json
{
  "path": "inbox/video.mp4",
  "length": 4294967296,
  "content_type": "video/mp4",
  "blob": "sha256:<expected hex>",
  "mtime": 1729450000000,
  "on_conflict": "rename"
}
```

`length` and `blob` are optional. If `length` is omitted the session has no
declared size and completion is signalled by `PATCH ...?complete=true`.

Response `201`:

```json
{
  "id": "up_7f3c1a...",
  "path": "inbox/video.mp4",
  "offset": 0,
  "length": 4294967296,
  "chunk_size": 8388608,
  "expires_at": "2025-10-22T12:00:00Z"
}
```

`chunk_size` is a server hint; chunks larger than this may be rejected with
`413`.

#### `PATCH /api/v1/uploads/<id>`

Appends a chunk at the current offset. Only one writer may have a session open;
a mismatched offset is a `409`.

```http
PATCH /api/v1/uploads/up_7f3c1a... HTTP/1.1
Content-Type: application/offset+octet-stream
Upload-Offset: 8388608
Content-Length: 8388608

<bytes>
```

- `204 No Content` with `Upload-Offset: <new offset>` after a partial chunk.
- `200 OK` with `{ "entry": { ... } }` when the upload is complete (declared
  `length` reached, or `complete=true`). The session is then closed.
- `409` — offset mismatch; body includes the server's current `offset`.
- `410` — session expired or already completed.
- `413` — chunk or file exceeds the configured limit.
- `507` — quota would be exceeded.

#### `HEAD /api/v1/uploads/<id>`

Returns `Upload-Offset`, `Upload-Length` (if declared), and `Upload-Expires`.
Used to resume after a reload. `Cache-Control: no-store`.

#### `DELETE /api/v1/uploads/<id>`

Aborts the session and discards temporary content. `204`.

Sessions expire after a server-configured idle period (default 24 h); expired
sessions and their temporary files are removed.

---

## 8. Downloads

### `GET /api/v1/files/<path>`

Resolves the entry and streams its blob, with headers that make it usable
directly from an `<a download>` or `fetch`.

- `Content-Type`: the entry's stored type.
- `Content-Disposition: attachment; filename="<sanitized name>"`.
- `ETag: "<blob hex>"`.
- `Cache-Control: no-cache` (the URL is path-based and may change).
- `Accept-Ranges: bytes`.

`404` for a missing or deleted path. `Directory` entries return `409`; list
them via the manifest instead.

### `GET /api/v1/blobs/sha256/<hex>`

The data plane: immutable, cacheable content.

- `Content-Type: application/octet-stream` by default.
- `ETag: "<hex>"`.
- `Cache-Control: public, max-age=31536000, immutable`.
- `Accept-Ranges: bytes`.

`404` if the blob has been garbage-collected.

Both download endpoints honor `Range` (single range; `206` with
`Content-Range`), `If-None-Match` (`304`), `If-Modified-Since`, and `If-Range`.
An unsatisfiable range returns `416` with `Content-Range: bytes */<size>`.
`HEAD` is supported on both.

Clients that want a human filename should use `/files/<path>`. Clients that
want maximum cacheability may use `/blobs/...` and supply their own filename.

### Cross-site content safety

Uploaded content is untrusted. Both download endpoints send:

```http
X-Content-Type-Options: nosniff
Content-Security-Policy: sandbox
```

`/files/<path>` always uses `Content-Disposition: attachment`, so uploaded
HTML, SVG, or PDF cannot execute in the PWA's origin. The blob endpoint
defaults to `application/octet-stream`. Never serve user content inline on the
app's origin.

---

## 9. Quotas and limits

Server-configured, reported per deployment (not negotiated per request):

| Limit                    | Default    | Enforced as                              |
| ------------------------ | ---------- | ---------------------------------------- |
| Max single file          | 10 GiB     | `413` mid-stream, temp file discarded    |
| Max manifest page        | 5000       | `400` if `limit` exceeds                 |
| Max path length          | 4096 bytes | `400`                                    |
| Max segment length       | 255 bytes  | `400`                                    |
| Total storage            | configurable | `507` on commit                        |
| Upload session idle TTL  | 24 h       | `410`, temp content removed              |
| Tombstone retention      | 30 days    | cursor expiry, blob GC                   |

Clients should surface `507` as "storage full", not as a generic failure.

---

## 10. Health

### `GET /healthz`

`200 OK` when the process is alive. No authentication.

### `GET /readyz`

`200 OK` when the database is open and the blob store is writable; `503`
otherwise. No authentication.

---

## 11. Client sync algorithm

This is the whole client contract. The PWA keeps a full replica, so it never
resolves conflicts; the server is the only writer during normal operation.

```text
state (all persisted):
  instance    server instance_id seen on the last successful sync
  cursor      last applied seq
  entries     path -> entry            (IndexedDB)
  blobs       sha256 -> bytes          (OPFS, immutable)

sync():
    page = GET /manifest?since=cursor

    if 410 or page.instance_id != instance:
        reset()
        return

    if cursor == 0: apply_snapshot(page)   # replace entries with the live set
    else:           apply_delta(page)      # upsert live, delete tombstoned

    cursor   = last page.next
    instance = page.instance_id

    download_missing()   # every live file's blob into OPFS
    gc_blobs()           # delete blobs no live entry references

reset():
    if this is a first run (no local entries):
        full_sync()      # cursor = 0, replace the local manifest
        return

    # Automatic re-seed: the client has the bytes, the new server does not.
    server = full manifest from since=0     # what survived / arrived meanwhile

    for each local directory missing on server:
        PUT /directories/<path>

    for each local file:
        if server has the same path and blob: skip
        else: PUT /files/<path>?on_conflict=rename
              Content-Digest: sha-256 of the local blob

    full_sync()          # adopt the server's authoritative view
    report paths whose local blob was missing

apply_snapshot: replace the local entries with the live entries from every
                page; anything absent is gone.
apply_delta:    upsert live entries, delete tombstoned entries, and remove a
                blob when no remaining entry references it.

download(path): read OPFS; if absent, GET /blobs/sha256/<hex> and store it.
upload(file):   PUT /files/<path>?on_conflict=rename, then store the returned
                blob locally so the replica stays complete.
```

Re-seed is idempotent and resumable: each file is an independent `PUT`, and a
path the server already has with the same blob is skipped. `on_conflict=rename`
means a server that acquired different content at the same path during the
downtime keeps both versions rather than losing one.

The client is a full replica. That is what makes automatic re-seed possible;
it also means storage is proportional to the server's file set and is subject
to browser quotas and eviction. A client that never downloaded a file cannot
restore it.

---

## 12. Security notes

- TLS is mandatory in production; the PWA requires a secure context anyway.
- Same origin for the app and API removes CORS from the threat model.
- Bearer tokens are sent in headers, never query strings, so they do not land
  in logs or `Referer`.
- Path inputs are canonicalized and rejected if they escape the namespace; the
  server never joins a client path onto the filesystem without that check.
- Uploaded content is never rendered inline on the app origin (see §8).
- The PWA persists the bearer token in `localStorage` so a replica can sync
  unattended; a strict CSP and XSS discipline are therefore required.

---

## 13. Future (not v1)

- Presigned URLs or a redirect to object storage for the blob data plane.
- Adopting tus instead of the custom session protocol.
- Replication between drop points, at the blob level.
- Per-path sharing and scoped tokens.
- OIDC authorization-code flow replacing static bearer tokens.

---

## 14. Changelog

| Version | Date       | Change        |
| ------- | ---------- | ------------- |
| v1-draft| 2025-10-21 | Initial draft |
