# DHT Status Reporting

Status: Ready
Created: 2026-08-13
Updated: 2026-08-13

## Goal

Make DHT health visible the way VPN health already is: an operator should be able
to see that DHT is *enabled but not working*, instead of discovering it by noticing
that magnet links without trackers never find peers.

Today that state is invisible — and it is the *current* state of the deployment:
`dht_nodes.cache` is an empty bencoded list, MonoTorrent 3.0.2 ships no bootstrap
routers, so DHT sits at `NotReady` with an empty routing table (see the
[torrent engine](../torrent-engine/feature.md), whose recycling work measured it).

## What MonoTorrent exposes

Verified against MonoTorrent 3.0.2 by reflection probe (2026-08-13). `ClientEngine.Dht`
(`IDht`) is public and AOT-safe to consume — plain property reads and an event, no
reflection:

- `State` (`DhtState`: `NotReady` / `Initialising` / `Ready`) — the primary signal.
  A failed bootstrap manifests as `Initialising` → `NotReady`; no timer heuristics
  are needed to distinguish "still starting" from "could not start".
- `NodeCount` — routing-table size; corroborating detail for display.
- `StateChanged` — event, suitable for pushing over the existing SSE `/events`
  stream the way VPN status changes are pushed.
- `Monitor` (`ITransferMonitor`) — DHT traffic counters, evidence of actual packet
  exchange.

Two more probe findings that constrain the implementation:

- **With DHT disabled (`DhtEndPoint = null`), `ClientEngine.Dht` is still non-null** —
  a `DhtEngineWrapper` null object reporting `State: NotReady`, `NodeCount: 0`. By
  `State` alone, disabled is indistinguishable from broken, so `Enabled` must come
  from `TorrentEngineSettings`, never be inferred from the engine.
- **`IDht` exposes no methods at all** (properties and events only), and
  `ClientEngine` has no DHT-related methods either — there is no public API for
  seeding nodes. This is why bootstrap seeding is not part of this plan (see
  Decisions).

## Target behaviour

Modelled on `VpnStatus` / `GET /vpn`:

- A `DhtStatus` snapshot: `Enabled` (the `TORRENT_ENABLE_DHT` setting), `Running`
  (an engine exists and DHT is started), `State`, `NodeCount`.
- "Enabled but not working" is `Enabled && Running && State == NotReady`. It must key
  off `NotReady` specifically, **not** `State != Ready`: `Initialising` is the normal
  startup state, so the looser predicate would report DHT as broken during every
  bootstrap.
- Three states must not be conflated with "broken":
  - **No engine.** Under [engine recycling](../torrent-engine/feature.md) the engine —
    and therefore DHT — does not exist while no torrents are registered. That is
    `Running: false`, not a failure.
  - **Still starting.** `Initialising` is DHT coming up; it is reported as its own
    state and is exactly what distinguishes a slow start from a failed bootstrap.
  - **`NotReady` can self-heal.** Peers found via trackers/PEX send `PORT` messages
    that seed the routing table, flipping the state to `Ready`. The status reports
    the current state, not a verdict.

## Deliverables

- [ ] `DhtStatus` record and a provider that reads `ClientEngine.Dht`, tolerating an
      absent engine; `Enabled` comes from settings (see probe findings above).
- [ ] `GET /dht` endpoint exposing the snapshot, mirroring `GET /vpn`.
- [ ] SSE push on `StateChanged`: a `dht` event carried by a new nullable `Dht`
      field on `TorrentEvent` (the same pattern as the `Vpn` field), with
      `DhtStatus` registered in `AppJsonSerializerContext`.
- [ ] Tests: status with no engine, with DHT disabled, state/node-count mapping with
      an engine present, and `Initialising` reported as starting rather than broken.
- [ ] Docs: `feature.md` for this folder; cross-link from the torrent-engine docs.
- [ ] Version bump (minor — new functionality while in `0.x`).

## Decisions

Formerly open questions, resolved 2026-08-13:

- **Endpoint shape: a dedicated `GET /dht`, mirroring `GET /vpn`.** media-server
  already consumes exactly the "`GET /vpn` + `vpn` SSE event" pair
  (`RemoteTorrentEngine` → `vpnStatusChanged` rebroadcast → VPN pill in the web UI),
  so a symmetric `GET /dht` + `dht` event slots into that pipeline unchanged. The
  new nullable `Dht` field on `TorrentEvent` is backward-compatible — an unaware
  consumer ignores it.
- **Media-server display is out of scope.** The endpoint + event contract defined
  here is the coordination artifact; the display work is planned in the
  media-server repo when this ships.
- **Bootstrap seeding is not part of this plan.** `IDht` exposes no methods and
  `ClientEngine` has no seeding hook (verified by probe), so seeding needs its own
  feasibility work — it becomes the next plan in this folder after status ships.
- **Implemented after [engine recycling](../torrent-engine/feature.md).** That
  lifecycle has now shipped, so the provider is built against a settled one; it must
  tolerate an absent engine, which is the normal idle state.

## Verification

- Fresh start with no torrents → status reports `Running: false`, no error.
- `TORRENT_ENABLE_DHT=false` with torrents → `Enabled: false`, `Running: false`.
- Torrent added, empty `dht_nodes.cache` → `Enabled && Running`, state settles at
  `NotReady`, `NodeCount: 0` — the "enabled but not working" case.
- Peers arrive via tracker → state flips to `Ready` and the SSE stream carries the
  change.
