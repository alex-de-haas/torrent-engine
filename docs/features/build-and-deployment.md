# Build and Deployment

Status: Implemented
Created: 2026-07-03
Updated: 2026-07-03

## Description

The engine ships as a single-service container image built from a Native AOT .NET
binary, published multi-arch to GHCR, and installed by Hosty Core from the manifest.
This doc covers the Dockerfile, the container entrypoint's build-time/runtime split,
CI, image publishing, and local development.

## The image (`Dockerfile`)

A two-stage build from the repo root:

- **Build stage** (`mcr.microsoft.com/dotnet/sdk:10.0`). Native AOT compiles to a
  platform-native binary, so the SDK image also installs a C toolchain (`clang`) and
  `zlib` headers. The Docker build arch (`TARGETARCH`, or the SDK image's own arch
  for a plain `docker build`) is mapped to a .NET RID (`linux-x64` / `linux-arm64`),
  then `dotnet publish -c Release -r <rid>` produces the self-contained binary.
- **Runtime stage** (`mcr.microsoft.com/dotnet/runtime-deps:10.0`). `runtime-deps`
  carries only the native libraries an AOT binary links against (no managed runtime),
  so the image is much smaller than the aspnet base. It adds `openvpn`, `iptables`,
  and `iproute2` for the tunnel + killswitch, copies the binary and
  `docker/entrypoint.sh`, sets `ASPNETCORE_URLS=http://+:8080`, exposes `8080`, and
  runs the entrypoint. See [VPN isolation](vpn-isolation.md) for what the entrypoint
  does before it `exec`s the API.

The container needs `NET_ADMIN` and `/dev/net/tun` at runtime, granted through the
[manifest](hosty-runtime-app.md#elevated-container-privileges).

## Native AOT notes

`PublishAot=true` (`TorrentEngine.Api.csproj`) means no reflection-based
serialization or runtime code-gen:

- JSON goes through the source-generated `AppJsonSerializerContext`, inserted ahead
  of the default resolver in `Program.cs`.
- Minimal-API request delegates are emitted by the Request Delegate Generator
  (auto-enabled with AOT).
- `MonoTorrent.Client` is kept via `TrimmerRootAssembly` because
  `ClientEngine.SaveStateAsync()` serializes `EngineSettings` reflectively; that
  assembly (not the `MonoTorrent` facade) is the one that defines `Serializer` +
  `EngineSettings`, so it is the correct root. Without it, fast-resume/state
  persistence would break under AOT. See [Torrent engine](torrent-engine.md#native-aot-note).

## CI (`.github/workflows/ci.yml`)

On pushes to `main` and on pull requests: restore, `dotnet build --configuration
Release`, and `dotnet test` the `TorrentEngine.Api.Tests` project on
`ubuntu-latest` with the .NET 10 SDK. Superseded runs on the same ref are cancelled.

## Publishing (`.github/workflows/publish.yml`)

On pushes to `main` (→ `:latest`), version tags `v*` (→ `:vX.Y.Z`), and manual
dispatch, the image is built **multi-arch the fast way**: `linux/amd64` and
`linux/arm64` each build on their **own native runner** in parallel (no QEMU — the
emulated arm64 `dotnet publish` is slow and flaky), push blobs by digest, and a final
`merge` job assembles the tagged manifest list from those digests. Images land at
`ghcr.io/alex-de-haas/torrent-engine`. Tags come from `docker/metadata-action`
(`latest` on the default branch, the git tag, and a short-SHA tag).

## Install (docker runtime)

Point Hosty Core at the published manifest — no clone needed, since
`defaultRuntime` is `docker` and the manifest references the GHCR image:

```bash
hosty core start
hosty apps install https://raw.githubusercontent.com/alex-de-haas/torrent-engine/main/manifest.json
hosty apps start com.haas.torrent-engine
```

Before it is functional, configure the required settings through the Shell:

- **`OPENVPN_CONFIG`** (required secret) — the `.ovpn`, raw or base64.
- **Downloads mounts** — bind at least one host path into the `downloads` mount,
  with the same label the consumer uses for its matching catalog root (see
  [Downloads mounts](downloads-mounts.md)).
- Optionally `OPENVPN_USERNAME`/`OPENVPN_PASSWORD` and the `TORRENT_*` / `VPN_*`
  knobs (see [Configuration](configuration.md)).

Swap `main` for a release tag in the manifest URL to pin a specific build.

## Local development (no VPN)

The app runs directly for API/engine work, without the container, VPN, or killswitch:

```bash
# from the repository root
dotnet test src/TorrentEngine.Api.Tests/TorrentEngine.Api.Tests.csproj
dotnet run --project src/TorrentEngine.Api           # http://localhost:5xxx or ASPNETCORE_URLS
```

With no mount injected the engine uses a single unlabeled fallback downloads root at
`{contentRoot}/data/downloads`, and `GET /vpn` reports the tunnel as down (there is
no `tun0`), so the download gate keeps torrents paused unless `VPN_INTERFACE` points
at a real up interface. This is enough to exercise the control API, snapshot
derivation, and mount-label logic; the tunnel/killswitch require the docker runtime.

## Testing Expectations

CI runs the xUnit suite on every push/PR. The AOT publish is exercised by the
`publish` workflow (a build failure there catches trimming/AOT regressions such as a
missing trimmer root). Tunnel/killswitch behavior is validated at the runtime level
(leak tests), not in CI — see [VPN isolation](vpn-isolation.md).
