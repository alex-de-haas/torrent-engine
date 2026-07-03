# Consumer Integration

Status: Implemented
Created: 2026-07-03
Updated: 2026-07-03

## Description

The engine exists to be driven by another Hosty app. This doc describes how a
consumer wires it as a cross-app dependency, discovers it at runtime, shares
downloads mounts with it, and tolerates its absence. [Media Server](https://github.com/alex-de-haas/media-server)
is the reference consumer: it declares this app as a **required** dependency and
delegates **all** downloading to it over the control API + SSE via
`RemoteTorrentEngine`; its former in-process MonoTorrent engine has been removed.

## Declaring the dependency

The consumer wires the engine by its `control` endpoint in its own manifest:

```json
"dependencies": [
  {
    "id": "com.haas.torrent-engine",
    "required": true,
    "endpoints": [{ "key": "control", "as": "torrent-engine" }]
  }
]
```

## Discovery

Core injects the resolved base URL into the consumer, named after the endpoint
alias — for Media Server, `HOSTY_DEPENDENCY_TORRENT_ENGINE_URL`. The consumer points
its HTTP client at that value; no address, port, or origin is hard-coded. The client
then speaks the [Control API](control-api.md): `POST /downloads` to add,
`GET /downloads[/{infoHash}]` to poll, the pause/resume/stop/remove verbs to
control, and `GET /events` to consume progress and transitions as they happen.

## Sharing downloads mounts

For the post-download move to be zero-copy, the consumer and engine must write on
the same filesystem. The operator binds each of the engine's `downloads` mounts to
the **same host path and the same label** as the consumer's matching catalog root;
the consumer then sends that label as `mountLabel` on `POST /downloads`. The engine
resolves the relative `savePath` against the root under that label, so the download
lands on the filesystem the consumer will move it from. The label is the only key
shared across the two apps — Hosty configures each app's mounts independently. See
[Downloads mounts](downloads-mounts.md) for the full contract.

## Tolerating absence

`required: true` is **advisory** in Hosty: it raises the severity of the
missing-dependency notification but does **not** block the consumer's start or
guarantee the engine is present. A consumer should therefore degrade gracefully:

- Keep the rest of its surface working when
  `HOSTY_DEPENDENCY_TORRENT_ENGINE_URL` is unset — Media Server keeps its library,
  identify/probe/enrich, and Jellyfin serving alive and only disables downloading.
  It uses a `DisabledTorrentEngine` fallback behind the same `ITorrentEngine`
  interface so the rest of the code is unaware.
- Gate readiness on the engine while the tunnel comes up: poll `GET /healthz`
  (liveness) and `GET /vpn` (`connected`), and hold off adding downloads until the
  tunnel is up. Seed VPN state from `GET /vpn` on connect, then track `vpn` SSE
  events (see [VPN isolation](vpn-isolation.md)).

## Re-driving off remote events

A consumer that previously ran an in-process engine reworks its coordinator into a
thin client: instead of subscribing to in-process engine callbacks, it subscribes to
the SSE stream and re-drives its own pipeline off `metadata-received` / `completed` /
`errored` (and reads live progress from `progress`). Download progress is **not**
persisted by the engine — the consumer treats snapshots as ephemeral and persists
only the state transitions it cares about.

## Current caveat — non-public endpoint

The `control` endpoint is **non-public**, but Hosty cross-app `dependencies` today
resolve a *public*, host-reachable endpoint. Reaching a non-public endpoint across
containers needs the planned shared cross-app docker network, and real multi-tenant
use additionally needs the Hosty app-identity token mechanism for auth. Until those
land, this is a **trusted single-tenant** deployment with no auth on the control API.
Do not expose the control port publicly.

## Testing Expectations

The cross-app wiring (dependency resolution, the injected URL, mount-label sharing)
is validated at the Hosty runtime level on the consumer side, not by this app's unit
tests. On the engine side, the control API and mount-label contracts consumers rely
on are covered by [Control API](control-api.md) and
[Downloads mounts](downloads-mounts.md) tests.
