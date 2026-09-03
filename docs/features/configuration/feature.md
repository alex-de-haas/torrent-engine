# Configuration

Created: 2026-07-03
Updated: 2026-09-03

## Description

All configuration is environment-driven and resolved once at startup — the app
hard-codes no ports or paths. Two layers read it: `TorrentEngineSettings.FromConfiguration`
(`Torrents/TorrentEngineSettings.cs`) for the engine and VPN-monitor knobs, and
`docker/entrypoint.sh` for the tunnel/killswitch. Values come from Hosty settings
(the manifest), Hosty-injected platform variables, and, in docker, `ASPNETCORE_URLS`.

## Hosty-injected platform environment

Set by Core, not by the operator:

| Variable | Read by | Purpose |
| --- | --- | --- |
| `HOSTY_APP_DATA_DIR` | entrypoint + engine | App data dir; fast-resume + magnet-metadata cache live under `torrent-engine/`, the VPN profile selection under `vpn/active-profile`. Falls back to `{contentRoot}/data` when unset. |
| `HOSTY_MOUNT_DOWNLOADS` | engine | Comma-joined `label=path` downloads mounts, parsed into the label→root map. See [Downloads mounts](../downloads-mounts.md). |
| `HOSTY_MOUNT_VPN` | entrypoint + engine | The `vpn` mount (`label=path`, first binding): the operator's OpenVPN profiles folder. Absent → no profiles, `GET /vpn/profiles` empty. See [VPN profiles](../vpn-profiles/feature.md). |
| `HOSTY_PORT_TORRENT` | engine | Fallback source for the torrent listen port when `TORRENT_PORT` is unset. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` (+ other `OTEL_*`) | engine | Presence switches on OTLP export; absence = no telemetry. See [Hosty runtime app](../hosty-runtime-app/feature.md#telemetry). |
| `ASPNETCORE_URLS` | entrypoint + engine | Container listen URL (`http://+:8080`); the entrypoint reads the port from here to open the killswitch for the actual control port. |

## Operator settings (manifest)

Defaults come from `manifest.json`; the operator sets them through the Hosty Shell.

### Torrent engine

| Variable | Default | Meaning |
| --- | --- | --- |
| `TORRENT_PORT` | `6881` | Raw L4 listen port (TCP + UDP). Under the VPN, the port bound inside the tunnel. |
| `TORRENT_BIND_ADDRESS` | (IPv4, all interfaces) | Bind the listen + DHT endpoint to one address (e.g. the VPN tun address). Unset → IPv4 `Any` only (the engine deliberately doesn't solicit IPv6). Set → binds **only** that address's family. |
| `TORRENT_ENABLE_PORT_MAPPING` | `false` | UPnP / NAT-PMP automatic port mapping. Off by default (irrelevant behind a VPN). |
| `TORRENT_ENABLE_DHT` | `true` | Peer discovery over the DHT. `false` binds no DHT endpoint **and** sets `AllowDht` false per torrent, leaving trackers, PEX and Local Peer Discovery. A malformed value falls back to the default. |
| `TORRENT_MAX_DOWNLOAD_SPEED` | `0` | Global max download rate, bytes/sec (`0` = unlimited). Per-download `maxDownloadRate` overrides it. |
| `TORRENT_MAX_UPLOAD_SPEED` | `0` | Global max upload rate, bytes/sec (`0` = unlimited). Per-download `maxUploadRate` overrides it. |
| `TORRENT_MAX_ACTIVE` | `0` | Max concurrently-registered torrents (`0` = unlimited). An add beyond the cap is a `409`. |

### VPN tunnel (profiles + monitor)

The profiles themselves are files in the `vpn` mount, not settings — see
[VPN profiles](../vpn-profiles/feature.md). Credentials go in a `<id>.auth` beside
the profile that needs them.

| Variable | Default | Meaning |
| --- | --- | --- |
| `VPN_PROFILE` | — | Profile id (file name without `.ovpn`/`.conf`) to start with when no selection was made through the API. Empty → the only profile, or the first by name. |
| `VPN_STATE_DIR` | `/run/vpn` | Where the entrypoint's supervisor publishes its `status` file (and keeps the OpenVPN pid and the credentials copy). Overridable so tests use a temp dir; not a manifest setting. |
| `VPN_INTERFACE` | `tun0` | Tunnel interface the killswitch confines traffic to and the monitor watches. |
| `VPN_DNS` | `1.1.1.1` | Tunnel-reachable resolver `resolv.conf` is pointed at once the tunnel is up (so lookups don't leak or break). |
| `VPN_EXIT_IP_CHECK` | `true` | Whether the monitor performs the best-effort exit-IP check (an outbound call over the tunnel). |
| `VPN_EXIT_IP_CHECK_URL` | `https://ipinfo.io/json` | Endpoint for the exit-IP check; a JSON body with `ip`/`country` is preferred, a plain-text IP is accepted. |

## Precedence notes

- The torrent listen port is `TORRENT_PORT`, then `HOSTY_PORT_TORRENT`, then `6881`.
- Per-download `maxDownloadRate` / `maxUploadRate` (on `POST /downloads`) override
  the `TORRENT_MAX_*_SPEED` engine defaults; both use `0` for unlimited.
- Numeric/boolean settings that fail to parse fall back to their defaults rather
  than erroring at startup.
- The active VPN profile is the persisted selection (`vpn/active-profile` under the
  app data dir), then `VPN_PROFILE`, then the only profile, then the first by name.

## Testing Expectations

`TorrentEngineSettingsTests` (xUnit) cover the resolution rules: port precedence
(`TORRENT_PORT` over `HOSTY_PORT_TORRENT` over default), the boolean/int fallbacks,
the downloads-mount parsing (delegated to the cases in
[Downloads mounts](../downloads-mounts.md)), and the `HOSTY_MOUNT_VPN` /
`VPN_STATE_DIR` resolution.
