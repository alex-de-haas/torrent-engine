# Torrent Engine (VPN-isolated)

A standalone [Hosty](https://github.com/alex-de-haas/docker-host) runtime app: a
BitTorrent engine (MonoTorrent) that runs **inside an OpenVPN tunnel** and exposes an
HTTP/SSE **control API** for other Hosty apps to drive downloads over a dependency.

The first consumer is **Media Server**, which now delegates **all** downloading to this app
and no longer ships an in-process engine. Running the engine here solves two problems at once:

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
  stays reachable on the docker bridge. `OPENVPN_CONFIG` accepts the raw `.ovpn` contents
  **or** a base64 encoding of them; prefer base64 when setting it through a single-line
  secret field that would otherwise flatten the newlines OpenVPN requires
  (`base64 -w0 client.ovpn`, or `base64 -i client.ovpn | tr -d '\n'` on macOS). Once the
  tunnel is up, DNS is pointed at a tunnel-reachable resolver (`VPN_DNS`, default `1.1.1.1`)
  so lookups don't leak outside the VPN — and don't break when the host/docker resolver is
  no longer routable through the tunnel.
- **MonoTorrent engine** (`src/TorrentEngine.Api/Torrents`) — ported from media-server,
  decoupled from its DB/pipeline: DHT/PEX/LSD, protocol encryption, fast-resume/metadata
  cache, per-torrent limits, runs as a hosted service on the configured `TORRENT_PORT`.
- **Control API + SSE** (`src/TorrentEngine.Api/Api`, `.../Realtime`) — add/list/inspect/
  pause/resume/stop/remove downloads, and `GET /events` streaming progress + metadata/
  completed/errored transitions.
- **Multiple downloads mounts** — one labelled host path per catalog filesystem, selected
  per download by `mountLabel`, so the post-download move stays on one filesystem.
- **Consumer wiring (Media Server)** — Media Server consumes this app as a **required**
  cross-app dependency and delegates all downloading to it over the control API + SSE
  (`RemoteTorrentEngine`); the in-process engine has been removed there. See
  [Consumer integration](#consumer-integration).

TODO (next chunks):
- Leak-test the killswitch in a real VPN environment (see Open questions).
- Secure cross-app calling — the control endpoint is non-public, so today this is a trusted
  single-tenant deployment with no auth (see Open questions).

## Control API

```text
POST   /downloads            { magnet | torrentBase64, mountLabel?, savePath?, maxDownloadRate?, maxUploadRate?, autoStart? } -> descriptor
GET    /downloads
GET    /downloads/{infoHash}
GET    /downloads/{infoHash}/files
POST   /downloads/{infoHash}/pause|resume|stop
DELETE /downloads/{infoHash}?deleteFiles=
GET    /events               (SSE: progress, metadata-received, completed, errored, vpn)
GET    /vpn                  { connected, tunnelInterface, tunnelAddress, exitIp, exitCountry, checkedAt }
GET    /healthz
```

`mountLabel` selects which downloads mount a relative `savePath` resolves against. The `downloads`
external mount is `multiple`, so the operator binds one host path per catalog filesystem — each with the
**same label** the consumer uses for the matching catalog root (the only key shared across the two apps,
since Hosty configures each app's mounts independently). The label is required when more than one
downloads mount is configured and optional when there is exactly one (or for the standalone fallback
root). An unknown label is a `400` so a download is never written to the wrong filesystem instead of
silently landing off-mount.

`GET /vpn` reports the OpenVPN tunnel the engine runs behind: `connected` (tunnel interface up with an
assigned address) is the primary signal, and `exitIp`/`exitCountry` are a best-effort proof that peer
traffic egresses through the VPN — a cached outbound check over the tunnel (disable with
`VPN_EXIT_IP_CHECK=false`, or point it elsewhere with `VPN_EXIT_IP_CHECK_URL`). The tunnel interface
watched defaults to `tun0`; override it with `VPN_INTERFACE` if the tunnel comes up under a different
name. The same status is pushed on the SSE stream as a `vpn` event whenever it changes.

## Consumer integration

A Hosty app drives this engine by declaring it as a cross-app dependency and calling the
control API. Media Server is the reference consumer:

- **Dependency** (consumer manifest) — wire the engine by its `control` endpoint:
  `"dependencies": [{ "id": "com.haas.torrent-engine", "required": true, "endpoints": [{ "key": "control", "as": "torrent-engine" }] }]`.
- **Discovery** — Core injects the resolved base URL into the consumer as
  `HOSTY_DEPENDENCY_TORRENT_ENGINE_URL` (named after the endpoint alias). The consumer
  points its HTTP client at that value; no address is hard-coded.
- **Downloads mounts** — the consumer binds each of this app's `downloads` mounts to the
  **same host path and the same label** as its matching catalog root, then sends that label
  as `mountLabel` on `POST /downloads`. This keeps the download and the consumer's
  post-download move on one filesystem (zero-copy). See the label contract under
  [Control API](#control-api).
- **Availability** — `required: true` is advisory in Hosty (it raises the severity of the
  missing-dependency notification; it does **not** block the consumer's start or guarantee
  the engine is present). A consumer should therefore tolerate the engine being absent:
  Media Server keeps the rest of its surface (Jellyfin, library, identify/probe/enrich)
  working and only disables downloading when `HOSTY_DEPENDENCY_TORRENT_ENGINE_URL` is unset.
  It can also gate readiness on `GET /healthz` / `GET /vpn` while the tunnel comes up.

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

- **Cross-app auth/routing:** the consumer is now wired (Media Server declares the
  dependency and calls the control API), but the `control` endpoint is **non-public** while
  Hosty cross-app `dependencies` today resolve a *public*, host-reachable endpoint. Reaching
  a non-public endpoint across containers needs the planned shared cross-app docker network
  (and, for real multi-tenant use, the Hosty app-identity token mechanism). Until then this
  assumes a trusted single-tenant deployment.
- **Killswitch hardening:** the iptables rules in `docker/entrypoint.sh` are a first cut
  and need validation in a real VPN environment (leak tests on tunnel drop).
