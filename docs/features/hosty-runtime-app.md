# Hosty Runtime App

Status: Implemented
Created: 2026-07-03
Updated: 2026-07-03

## Description

Torrent Engine is packaged as a [Hosty](https://github.com/alex-de-haas/docker-host)
runtime app (`manifest.json`, `schemaVersion: "app.0.1"`, id
`com.haas.torrent-engine`). Hosty Core owns its lifecycle — install, start/stop,
update, backup/restore, logs — and injects the environment the app reads (data dir,
mounts, ports, and, when enabled, telemetry). The app never hard-codes ports,
origins, or paths. This doc is the reference for the manifest and the platform
contract; the environment variables it produces are enumerated in
[Configuration](configuration.md).

## Manifest anatomy

A single `engine` service with one `docker` runtime profile
(`defaultRuntime: docker`):

| Manifest section | Value / purpose |
| --- | --- |
| `services[].engine.runtimes.docker.image` | `ghcr.io/alex-de-haas/torrent-engine:latest`, `pullPolicy: always`. |
| `…docker.capabilities` | `["NET_ADMIN"]` — required to run OpenVPN and rewrite iptables. |
| `…docker.devices` | `["/dev/net/tun"]` — the TUN device the tunnel needs. |
| `…docker.ports` | `control` → container port `8080`, `http`. |
| `endpoints` | `control` → the `engine` service's `control` port; the consumer-facing HTTP surface. |
| `data` | Enabled; the `engine`'s `/app/data` is the backed-up app data dir, exposed as `HOSTY_APP_DATA_DIR`. |
| `externalMounts.downloads` | `host-path`, `multiple`, `rw`, `required` — one host path per catalog filesystem (see [Downloads mounts](downloads-mounts.md)). |
| `settings` | VPN + torrent knobs (see below). |
| `telemetry` | `{ enabled: true, sampleRatio: 0.1 }` — opt-in observability (see [Telemetry](#telemetry)). |
| `capabilities` | `update`, `restart`, `stop`, `remove`, `backup`, `restore`, `logs`. |

### Elevated container privileges

Running OpenVPN and iptables inside the container needs `--cap-add=NET_ADMIN` and
`--device /dev/net/tun`. These are **privileged** grants, so they are declared
explicitly in the manifest and surfaced at install review rather than assumed. They
depend on Hosty Core's capabilities/devices support
([docker-host#58](https://github.com/alex-de-haas/docker-host/pull/58)); without it
the app cannot bring up the tunnel.

### Settings

All settings are Hosty settings (secrets are stored as secrets), read from the
environment at startup:

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `OPENVPN_CONFIG` | string (secret) | — (**required**) | The `.ovpn` contents, raw or base64. |
| `OPENVPN_USERNAME` | string (secret) | — | Optional VPN auth username. |
| `OPENVPN_PASSWORD` | string (secret) | — | Optional VPN auth password. |
| `TORRENT_PORT` | number | `6881` | Raw L4 listen port (TCP + UDP). |
| `TORRENT_MAX_DOWNLOAD_SPEED` | number | `0` | Bytes/sec, `0` = unlimited. |
| `TORRENT_MAX_UPLOAD_SPEED` | number | `0` | Bytes/sec, `0` = unlimited. |
| `VPN_INTERFACE` | string | `tun0` | Tunnel interface the killswitch/monitor watch. |
| `VPN_DNS` | string | `1.1.1.1` | Tunnel-reachable DNS resolver. |
| `VPN_EXIT_IP_CHECK` | boolean | `true` | Best-effort exit-IP verification over the tunnel. |
| `VPN_EXIT_IP_CHECK_URL` | string | `https://ipinfo.io/json` | Endpoint for the exit-IP check. |

## App data and backups

`HOSTY_APP_DATA_DIR` (`/app/data` in the container) holds MonoTorrent's fast-resume
state and magnet-metadata cache under a `torrent-engine/` subdirectory, so
in-flight downloads and fetched metadata survive a restart. It is in the manifest's
`data` targets, so Core's `backup`/`restore` cover it. Download **payload** does not
live here — it lives on the `downloads` mounts (see
[Downloads mounts](downloads-mounts.md)).

## Endpoints and discovery

The app publishes one endpoint, `control`, over HTTP. A consumer declares this app
as a cross-app dependency and is handed the resolved base URL as an environment
variable (`HOSTY_DEPENDENCY_TORRENT_ENGINE_URL` for Media Server); it points its
HTTP client there and never hard-codes an address. See
[Consumer integration](consumer-integration.md) for the full wiring, including the
current non-public-endpoint caveat.

## Telemetry

The engine instruments itself with OpenTelemetry — ASP.NET Core / `HttpClient` /
.NET-runtime traces and metrics, plus `ILogger` logs — exported over OTLP/HTTP. Export
is **entirely driven by the `OTEL_*` environment Hosty Core injects**
(`src/TorrentEngine.Api/Telemetry/HostyTelemetry.cs`, wired in `Program.cs` via
`AddHostyTelemetry()`): when the operator has enabled observability and the collector
is running (docker runtime), traces/metrics/logs flow to it; otherwise — including any
non-docker run — the endpoint is absent and the app emits nothing. Opt-in is the
`telemetry` block in the manifest (`enabled: true`, `sampleRatio: 0.1`). The
platform-side observability contract is documented in the Hosty Core repo
(`docs/features/observability.md` there), not this one.

## Runtime profile

Only the `docker` profile is defined, and it is the default — the VPN, killswitch,
and elevated privileges only make sense inside the container. The app can still be
run directly with `dotnet run` for local API/engine work; with no VPN and no mount
injected it falls back to a single unlabeled downloads root under the content root
and reports the tunnel as down (see [Build and deployment](build-and-deployment.md)).

## Testing Expectations

Manifest/platform integration (capabilities, devices, mount injection, endpoint
discovery, backups) is validated through Core-managed runtime, not unit tests. The
settings-resolution layer that reads this environment is unit-tested — see
[Configuration](configuration.md) and [Downloads mounts](downloads-mounts.md).
