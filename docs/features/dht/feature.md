# DHT Status

Created: 2026-08-14
Updated: 2026-08-14

## Description

DHT health is reported the way VPN health is, so an operator can tell a DHT that is
**enabled but not working** from one that is simply off or idle. Without this the
three are indistinguishable from outside: magnet links without trackers just never
find peers.

This matters in practice — see [the bootstrap gap](#the-bootstrap-gap) below.

## `DhtStatus`

`Torrents/DhtStatus.cs`, served by `GET /dht` and pushed as the `dht` SSE event (see
[Control API](../control-api/feature.md)):

| Field | Meaning |
| --- | --- |
| `enabled` | The `TORRENT_ENABLE_DHT` setting. |
| `running` | DHT is actually running: enabled **and** an engine exists. |
| `state` | MonoTorrent's `DhtState` — `NotReady` / `Initialising` / `Ready` — while running; `null` otherwise. |
| `nodeCount` | Routing-table size; `0` when not running. |

`enabled` is read from configuration, not from the engine, and that is load-bearing:
MonoTorrent hands out a null-object DHT reporting `NotReady` when DHT is disabled, so
the engine alone cannot tell "off" from "failed to start".

A consumer derives the interesting case as:

```text
enabled && running && state == "NotReady"   → enabled but not working
```

Never as `state != "Ready"` — `Initialising` is a healthy start-up, and treating it as
failure would report DHT as broken during every bootstrap. The state is carried
through verbatim rather than collapsed into a boolean precisely so the two stay
distinguishable.

Two states are **not** faults:

- **`running: false` with `enabled: true`** — the engine is recycled when no torrent
  is registered (see [Torrent engine](../torrent-engine/feature.md)), so an idle app
  reports this. Normal.
- **`NotReady` after peers arrive** — it can self-heal: peers found via trackers or
  PEX send `PORT` messages that seed the routing table, flipping the state to `Ready`.
  The status reports the current state, not a verdict.

## Where it comes from

`MonoTorrentEngine.GetDhtStatus()` reads `ClientEngine.Dht` (`State`, `NodeCount`).
It takes **no** lifecycle lock: like the other read-only views it must stay cheap, and
those properties remain readable even on an engine being disposed underneath it.

`DhtStatusChanged` is raised from three places, all forwarded onto the SSE stream by
`Realtime/TorrentProgressBroadcaster.cs`:

- MonoTorrent's own `IDht.StateChanged`, subscribed when an engine is constructed (or
  restored at startup) and unsubscribed at teardown;
- the engine construction edge, where DHT starts running;
- the engine teardown edge, where it stops.

The two lifecycle edges are raised **outside** the lifecycle lock, so a subscriber can
never re-enter the lifecycle while it is held.

## The bootstrap gap

Worth recording, because it is what this status makes visible: **DHT in this
deployment does not currently work at all.** `dht_nodes.cache` in the app data dir is
2 bytes — `le`, an empty bencoded list — and MonoTorrent 3.0.2 ships no bootstrap
routers. It seeds its routing table only from that cache, from a `.torrent`'s `nodes`
field, or from `PORT` messages sent by already-connected peers, so a cold start with
magnet links has nothing to contact and settles at `NotReady` with `nodeCount: 0`.

Reporting the problem is not fixing it. Seeding the routing table is separate work and
is not implemented; `IDht` exposes no seeding API at all (properties and events only),
so it needs its own feasibility study first.

## Testing Expectations

Backend tests use xUnit and Imposter. Required coverage:

- Status with no engine: `enabled` true, `running` false, `state` null.
- Status with a torrent registered: `running` true and a state present.
- With `TORRENT_ENABLE_DHT=false` and a torrent registered: reported as off, **not**
  as a failing DHT — the null-object `NotReady` must not leak out.
- The state is one of MonoTorrent's own `DhtState` values, so `Initialising` is never
  conflated with failure.
- The engine construction and teardown edges each raise a status change.
