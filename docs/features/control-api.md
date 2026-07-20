# Control API

Status: Implemented
Created: 2026-07-03
Updated: 2026-07-20

## Description

The control API is the consumer-facing surface of the engine: an ASP.NET Core
Minimal API (`src/TorrentEngine.Api/Api/TorrentEndpoints.cs`, plus `/healthz` and
`/vpn` in `Program.cs`) over HTTP, with a Server-Sent Events stream for live
progress and state transitions. It is the only way in — the MonoTorrent engine is
never reached directly. Engine records (`TorrentDescriptor`, `TorrentSnapshot`,
`TorrentFileInfo`) are returned on the wire as-is; there is no separate DTO layer.

The API is stateless per request. It is unauthenticated (the endpoint is
non-public; see [Consumer integration](consumer-integration.md)); caller
authentication is deferred to the platform's cross-app auth mechanism — peer
introspection of the app service token, proposed in the Hosty repo as
`docs/ideas/cross-app-auth.md`. An interim `CONTROL_API_TOKEN` shared-secret check
existed through 0.4.x and was removed unused in 0.5.0 (no consumer ever sent the
`X-Api-Token` header, so enabling it could only 401 the one integration that
exists). All JSON is
serialized through a source-generated `System.Text.Json` context
(`Api/AppJsonSerializerContext.cs`) so the app works under Native AOT — no
reflection-based serialization at runtime.

## Endpoints

| Method &amp; path | Purpose | Success | Notable errors |
| --- | --- | --- | --- |
| `POST /downloads` | Add a torrent (magnet or `.torrent`) | `200` `TorrentDescriptor` | `400` bad source/rate/path, `409` already registered / at active limit |
| `GET /downloads` | List all live snapshots | `200` `TorrentSnapshot[]` | — |
| `GET /downloads/{infoHash}` | One live snapshot | `200` `TorrentSnapshot` | `404` unknown hash |
| `GET /downloads/{infoHash}/files` | File list for a torrent | `200` `TorrentFileInfo[]` | `404` unknown hash |
| `POST /downloads/{infoHash}/pause` | Pause a torrent | `204` | — |
| `POST /downloads/{infoHash}/resume` | Resume a torrent | `204` | — |
| `POST /downloads/{infoHash}/stop` | Stop a torrent | `204` | — |
| `DELETE /downloads/{infoHash}?deleteFiles=` | Remove a torrent | `204` | — |
| `GET /events` | SSE stream (all torrents + VPN) | `200` `text/event-stream` | — |
| `GET /vpn` | Current VPN tunnel status | `200` `VpnStatus` | — |
| `GET /healthz` | Liveness | `200` `{ "status": "ok" }` | — |

`infoHash` is the torrent's V1-or-V2 info hash as lowercase hex. The pause/resume/
stop/remove handlers are idempotent: an unknown hash is a `204` no-op (removal
still clears any persisted fast-resume for that hash), not a `404`.

## `POST /downloads`

Body (`AddDownloadRequest`); provide **exactly one** of `magnet` / `torrentBase64`:

| Field | Type | Notes |
| --- | --- | --- |
| `magnet` | string? | Magnet URI. Mutually exclusive with `torrentBase64`. |
| `torrentBase64` | string? | A `.torrent` file, base64-encoded. |
| `mountLabel` | string? | Selects the downloads mount a relative `savePath` resolves against. Required when several mounts are configured; optional with exactly one. See [Downloads mounts](downloads-mounts.md). |
| `savePath` | string? | Save directory relative to the selected mount root. Omitted → the mount root itself. An absolute path or `../` traversal outside the root is a `400`. |
| `maxDownloadRate` | int? | Bytes/sec, `0` = unlimited. Falls back to the engine default (`TORRENT_MAX_DOWNLOAD_SPEED`) when omitted. Negative → `400`. |
| `maxUploadRate` | int? | Bytes/sec, `0` = unlimited. Falls back to `TORRENT_MAX_UPLOAD_SPEED`. Negative → `400`. |
| `autoStart` | bool? | Start immediately (default `true`). `false` adds the torrent stopped. |

The handler validates before it mutates: it inspects the source to read the info
hash up front (a bad magnet/`.torrent` — including valid-base64 but garbage bencode
— is a `400`, not a `500`), rejects a re-add of an already-registered hash with
`409`, rejects an add that would exceed the active-torrent cap
(`TORRENT_MAX_ACTIVE`, `0` = unlimited) with `409`, checks the rates, and resolves
the save directory (an unknown `mountLabel` or an off-mount `savePath` is a `400`) —
only then does it add to the engine. The engine also reserves the info hash
atomically, so two concurrent adds of the same hash yield one `200` and one `409`
rather than a `500`.

The response is a `TorrentDescriptor` — what is known immediately:

| Field | Type | Notes |
| --- | --- | --- |
| `infoHash` | string | Lowercase hex. |
| `name` | string? | Torrent name (present for `.torrent`; may be null for a bare magnet). |
| `totalSize` | long? | Total content size; null before a magnet's metadata arrives. |
| `hasMetadata` | bool | `true` for `.torrent`; `false` for a magnet until metadata downloads. |
| `files` | `TorrentFileInfo[]` | Populated for `.torrent`; empty for a magnet until metadata arrives (watch the `metadata-received` SSE event). |

`TorrentFileInfo` is `{ index, relativePath, length }`, where `relativePath` is
POSIX-separated and relative to the torrent's save directory.

## The per-torrent snapshot

`TorrentSnapshot` is a **live, in-memory** view (never persisted) returned by
`GET /downloads`, `GET /downloads/{infoHash}`, and carried on the SSE `progress`
event. The first ten fields are the original contract (unchanged names/order); the
rest are additive, so existing consumers keep working.

| Field | Type | Meaning |
| --- | --- | --- |
| `infoHash` | string | Lowercase hex. |
| `name` | string? | Torrent name (null before a magnet's metadata). |
| `engineState` | string | MonoTorrent `TorrentState` name (`Downloading`, `Seeding`, `Paused`, `Stopped`, `Hashing`, `Metadata`, `Error`, …). |
| `complete` | bool | Whole torrent downloaded. |
| `percentComplete` | double | `0`–`100`, 2-dp. |
| `downloadRateBytesPerSecond` | long | Current download rate. |
| `uploadRateBytesPerSecond` | long | Current upload rate. |
| `ratio` | double | `uploadedBytes / downloadedBytes` this session, 3-dp (`0` before any download). |
| `peers` | int | Currently connected peer connections (`OpenConnections`). |
| `sizeBytes` | long | Total content size; `0` before a magnet's metadata. |
| `seeds` | int | Connected peers that have the complete torrent. |
| `leeches` | int | Connected peers still downloading. |
| `availablePeers` | int | Peers known from trackers/DHT/PEX but **not** connected. |
| `downloadedBytes` | long | Payload received **this session** (resets on restart); basis for `ratio`. |
| `uploadedBytes` | long | Payload sent this session. |
| `remainingBytes` | long | Content still to download (derived from progress); `0` when complete. |
| `totalPieces` | int | Pieces once metadata is known (`0` before). |
| `completePieces` | int | Verified pieces downloaded. |
| `pieceLengthBytes` | long | Size of one piece (`0` before metadata). |
| `etaSeconds` | long? | Seconds to completion at the current rate; `null` when complete, stalled (rate `0`), or size unknown. |
| `addedAt` | DateTimeOffset | When the torrent was added this session. |
| `elapsedSeconds` | double | Seconds since `addedAt`, server-computed, 1-dp. |

`availablePeers` versus `peers` is the quickest read on why a torrent is slow: many
peers discovered but few connected points at NAT / port-forwarding behind the VPN
rather than a discovery (tracker/DHT) problem. See
[Torrent engine](torrent-engine.md) for how these are derived.

## SSE event stream (`GET /events`)

`GET /events` returns `text/event-stream` (with `Cache-Control: no-cache` and
`X-Accel-Buffering: no`) and streams until the client disconnects. Each frame is a
named event whose `data:` is a JSON `TorrentEvent`:

```
event: progress
data: {"type":"progress","infoHash":"abc…","snapshot":{…},"vpn":null}
```

| Event | When | Payload |
| --- | --- | --- |
| `progress` | Every 1.5s, one frame per torrent | `snapshot` set, `vpn` null |
| `metadata-received` | A magnet's file list becomes available | `snapshot` set |
| `completed` | A torrent finishes (transition to a complete/seeding state; raised once) | `snapshot` set |
| `errored` | A torrent enters the error state | `snapshot` set |
| `vpn` | VPN tunnel status changes | `infoHash` empty, `vpn` set (`VpnStatus`), `snapshot` null |

`TorrentEvent` is `{ type, infoHash, snapshot, vpn }`: `infoHash` is empty for
engine-wide events such as `vpn`; `snapshot` is null for `vpn`; `vpn` is null for
per-torrent events.

The stream is served from an in-memory fan-out hub (`Realtime/TorrentEventStream.cs`):
each subscriber gets its own **bounded** channel (256 events, `DropOldest`), so one
slow reader drops its own oldest frames instead of blocking the broadcaster or other
subscribers. Because `progress` re-broadcasts a full snapshot every tick, a dropped
frame is self-healing — the next tick carries current state. The periodic tick and
the engine-event/VPN forwarding are done by `Realtime/TorrentProgressBroadcaster.cs`.
There is no server→client backfill or replay: a client that connects mid-download
gets the next `progress` tick, and should seed initial state from `GET /downloads`
and `GET /vpn`.

An otherwise-idle stream emits an SSE comment (`: ping`) every ~20s so a stream that
would send zero bytes isn't silently dropped by an intermediary proxy. Clients
ignore comment lines.

## Error envelope

`400`/`409` carry an `ErrorResponse` body,
`{ "error": "<message>" }`. The message is human-readable and safe to surface to an
operator (e.g. an unknown `mountLabel` lists the configured labels). A `404` for an
unknown info hash (`GET /downloads/{infoHash}` / `…/files`) has an **empty** body —
the hash in the path is all the context there is. `/healthz` returns
`{ "status": "ok" }`.

## Testing Expectations

Backend tests use xUnit and Imposter; endpoint tests host `MapTorrentEndpoints` on
an in-memory `TestServer`. Required coverage:

- `POST /downloads`: exactly-one-source validation, invalid magnet/base64 → `400`,
  duplicate add → `409`, negative rate → `400`, unknown `mountLabel` /
  off-mount `savePath` → `400`, `autoStart` honored.
- Snapshot serialization: all fields present and correctly named (contract
  stability for the original ten fields).
- Idempotent pause/resume/stop/remove for an unknown hash (`204` no-op).
- SSE framing: correct `event:`/`data:` lines and `TorrentEvent` shape per event
  type.
