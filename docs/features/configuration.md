# Configuration

Status: Implemented
Created: 2026-07-03
Updated: 2026-07-03

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
| `HOSTY_APP_DATA_DIR` | engine | App data dir; fast-resume + magnet-metadata cache live under `torrent-engine/`. Falls back to `{contentRoot}/data` when unset. |
| `HOSTY_MOUNT_DOWNLOADS` | engine | Comma-joined `label=path` downloads mounts, parsed into the label→root map. See [Downloads mounts](downloads-mounts.md). |
| `HOSTY_PORT_TORRENT` | engine | Fallback source for the torrent listen port when `TORRENT_PORT` is unset. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` (+ other `OTEL_*`) | engine | Presence switches on OTLP export; absence = no telemetry. See [Hosty runtime app](hosty-runtime-app.md#telemetry). |
| `ASPNETCORE_URLS` | entrypoint + engine | Container listen URL (`http://+:8080`); the entrypoint reads the port from here to open the killswitch for the actual control port. |

## Operator settings (manifest)

Defaults come from `manifest.json`; the operator sets them through the Hosty Shell.

### Torrent engine

| Variable | Default | Meaning |
| --- | --- | --- |
| `TORRENT_PORT` | `6881` | Raw L4 listen port (TCP + UDP). Under the VPN, the port bound inside the tunnel. |
| `TORRENT_BIND_ADDRESS` | (IPv4, all interfaces) | Bind the listen + DHT endpoint to one address (e.g. the VPN tun address). Unset → IPv4 `Any` only (the engine deliberately doesn't solicit IPv6). Set → binds **only** that address's family. |
| `TORRENT_ENABLE_PORT_MAPPING` | `false` | UPnP / NAT-PMP automatic port mapping. Off by default (irrelevant behind a VPN). |
| `TORRENT_MAX_DOWNLOAD_SPEED` | `0` | Global max download rate, bytes/sec (`0` = unlimited). Per-download `maxDownloadRate` overrides it. |
| `TORRENT_MAX_UPLOAD_SPEED` | `0` | Global max upload rate, bytes/sec (`0` = unlimited). Per-download `maxUploadRate` overrides it. |
| `TORRENT_MAX_ACTIVE` | `0` | Max concurrently-registered torrents (`0` = unlimited). An add beyond the cap is a `409`. |
| `CONTROL_API_TOKEN` | — | Optional shared secret (secret field). Set → every request except `/healthz` must present it in `X-Api-Token`, else `401`. Unset → the API is open. |

### VPN tunnel (secrets + monitor)

| Variable | Default | Meaning |
| --- | --- | --- |
| `OPENVPN_CONFIG` | — (**required**) | The `.ovpn` contents, raw **or** base64. Prefer base64 for a single-line secret field: `base64 -w0 client.ovpn` (or <code>base64 -i client.ovpn &#124; tr -d '\n'</code> on macOS). |
| `OPENVPN_USERNAME` | — | VPN auth username (optional). |
| `OPENVPN_PASSWORD` | — | VPN auth password (optional). |
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

## Testing Expectations

`TorrentEngineSettingsTests` (xUnit) cover the resolution rules: port precedence
(`TORRENT_PORT` over `HOSTY_PORT_TORRENT` over default), the boolean/int fallbacks,
and the downloads-mount parsing (delegated to the cases in
[Downloads mounts](downloads-mounts.md)).
