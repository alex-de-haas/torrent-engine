![Torrent Engine](../assets/icon.svg)

# Torrent Engine

A **VPN-isolated BitTorrent engine** (MonoTorrent) delivered as a Hosty runtime app. It
runs entirely **inside an OpenVPN tunnel** and exposes an HTTP/SSE **control API** so
other Hosty apps can drive downloads over a cross-app dependency.

The first consumer is **Media Server**, which delegates all downloading to this app and
ships no in-process engine of its own. Running the engine here solves two problems at
once:

- **VPN-only-for-torrent** — only this container's traffic egresses through the VPN; the
  consumer app and its HTTP/streaming surface stay on the direct connection.
- **Throughput + exposure** — the torrent client gets its own network namespace, so the
  consumer's bridge networking and port exposure are untouched.

## What it does

- **Killswitch** — a default-deny firewall means torrent traffic can only leave via the
  tunnel interface, while the control API stays reachable on the docker bridge. DNS is
  repointed at a tunnel-reachable resolver so lookups don't leak.
- **MonoTorrent engine** — DHT/PEX/LSD, protocol encryption, fast-resume and metadata
  cache, and per-torrent limits, running as a hosted service on the configured port.
- **Control API + SSE** — add, list, inspect, pause, resume, stop, and remove downloads,
  plus `GET /events` streaming progress and metadata/completed/errored transitions.
- **Downloads mounts** — one labelled host path per catalog filesystem, selected per
  download by `mountLabel`, so the post-download move stays on one filesystem.

## Configuration

The VPN comes from a folder of your own OpenVPN profiles, bound read-only into the
app's `vpn` mount: one `.ovpn` per profile, a `<id>.auth` (username, password) beside a
profile that needs one, and any certificates a profile references. Several profiles can
live there; the active one is chosen at start (`VPN_PROFILE`, else the only or first
one) and can be switched at runtime through the control API. Traffic is not routed
until the tunnel is up.

## Using it

Install from the marketplace and add it as a required dependency from a consumer app
(e.g. Media Server). The control endpoint is non-public — reached over Hosty's intra-app
service discovery, not exposed to the browser. Requires host support for the `NET_ADMIN`
capability and `/dev/net/tun`.
