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

## Status

Implemented:
- App manifest (`manifest.json`) — docker service with `NET_ADMIN` + `/dev/net/tun`
  (requires docker-host capabilities/devices support, alex-de-haas/docker-host#58),
  `.ovpn` + credentials as secret settings, a shared downloads mount, and the control
  port.
- Container (`Dockerfile` + `docker/entrypoint.sh`) — OpenVPN bring-up with a default-deny
  **killswitch** so torrent traffic can only leave via the tunnel, while the control API
  stays reachable on the docker bridge.
- **MonoTorrent engine** (`src/TorrentEngine.Api/Torrents`) — ported from media-server,
  decoupled from its DB/pipeline: DHT/PEX/LSD, protocol encryption, fast-resume/metadata
  cache, per-torrent limits, runs as a hosted service on the configured `TORRENT_PORT`.
- **Control API + SSE** (`src/TorrentEngine.Api/Api`, `.../Realtime`) — add/list/inspect/
  pause/resume/stop/remove downloads, and `GET /events` streaming progress + metadata/
  completed/errored transitions.

TODO (next chunks):
- Leak-test the killswitch in a real VPN environment.
- Wire media-server to consume this app as a dependency (docker-host#59) and share the
  downloads mount so completed files move on one filesystem.
- Secure cross-app calling — see Open questions (trusted single-tenant: no auth for now).

## Control API

```text
POST   /downloads            { magnet | torrentBase64, savePath?, maxDownloadRate?, maxUploadRate?, autoStart? } -> descriptor
GET    /downloads
GET    /downloads/{infoHash}
GET    /downloads/{infoHash}/files
POST   /downloads/{infoHash}/pause|resume|stop
DELETE /downloads/{infoHash}?deleteFiles=
GET    /events               (SSE: progress, metadata-received, completed, errored, vpn)
GET    /vpn                  { connected, tunnelInterface, tunnelAddress, exitIp, exitCountry, checkedAt }
GET    /healthz
```

`GET /vpn` reports the OpenVPN tunnel the engine runs behind: `connected` (tunnel interface up with an
assigned address) is the primary signal, and `exitIp`/`exitCountry` are a best-effort proof that peer
traffic egresses through the VPN — a cached outbound check over the tunnel (disable with
`VPN_EXIT_IP_CHECK=false`, or point it elsewhere with `VPN_EXIT_IP_CHECK_URL`). The tunnel interface
watched defaults to `tun0`; override it with `VPN_INTERFACE` if the tunnel comes up under a different
name. The same status is pushed on the SSE stream as a `vpn` event whenever it changes.

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
