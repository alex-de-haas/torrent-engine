# Torrent Engine (VPN-isolated)

A standalone [Hosty](https://github.com/alex-de-haas/docker-host) runtime app: a
BitTorrent engine (MonoTorrent) that runs **inside an OpenVPN tunnel** and exposes an
HTTP/SSE **control API** for other Hosty apps to drive downloads over a dependency.

The first consumer is **Media Server**, which today runs the engine in-process. Moving
it here solves two problems at once:

1. **VPN-only-for-torrent** — only this container's traffic egresses through the VPN; the
   consumer app (and its HTTP/Jellyfin surface) stays on the direct connection.
2. **Throughput + exposure** — the torrent client gets its own network namespace, so the
   consumer's bridge networking and port exposure are untouched, and tunnelling all peer
   connections through a single VPN flow sidesteps the docker bridge NAT throttle.

See the originating design note in media-server:
`docs/ideas/torrent-engine-app.md`.

## Status: skeleton

Implemented:
- App manifest (`manifest.json`) — docker service with `NET_ADMIN` + `/dev/net/tun`
  (requires docker-host capabilities/devices support, alex-de-haas/docker-host#58),
  `.ovpn` + credentials as secret settings, a shared downloads mount, and the control
  port.
- Container (`Dockerfile` + `docker/entrypoint.sh`) — OpenVPN bring-up with a default-deny
  **killswitch** so torrent traffic can only leave via the tunnel, while the control API
  stays reachable on the docker bridge.
- Control API stub (`src/TorrentEngine.Api`) — health endpoint and the documented
  request surface (returns `501` until the engine is ported).

TODO (next chunks):
- Port MonoTorrent engine + coordinator from media-server (`MonoTorrentEngine.cs`).
- Implement the control API + SSE event stream against it.
- Secure cross-app calling (media-server → this app) via Hosty app identity — see
  Open questions.
- Wire media-server to consume this app as a dependency and share the downloads mount.

## Control API (planned)

```text
POST   /downloads            { source (magnet|torrent), savePath, limits, keepSeeding } -> { infoHash }
GET    /downloads
GET    /downloads/{infoHash}
POST   /downloads/{infoHash}/pause|resume|stop
DELETE /downloads/{infoHash}?deleteFiles=
GET    /events               (SSE: progress, metadata-received, completed, errored)
GET    /healthz
```

## Networking model

```
consumer app (bridge)  ──HTTP/SSE──►  control API (bridge, control port)
                                       │
                                       torrent-engine container
                                       │  OpenVPN client (tun0) + killswitch
                                       └─ peer traffic ──► VPN tunnel only
shared downloads volume (one filesystem, zero-copy move by the consumer)
```

## Open questions

- **Cross-app auth/routing:** how the consumer reaches this control API securely.
  Hosty cross-app `dependencies` resolve another app's *public* endpoint; the control
  API should not be public. Needs the Hosty app-identity token mechanism (and possibly a
  platform notion of an internal cross-app endpoint).
- **Killswitch hardening:** the iptables rules in `docker/entrypoint.sh` are a first cut
  and need validation in a real VPN environment (leak tests on tunnel drop).
- **Downloads mount contract:** the consumer configures this app's `downloads` mount to
  the same host path as its catalog staging dir so the post-download move stays
  same-filesystem.
