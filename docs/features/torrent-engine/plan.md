# Idle Engine Recycling and DHT Opt-Out

Status: Ready
Created: 2026-07-28
Updated: 2026-08-13

## Goal

Stop paying for a MonoTorrent engine that has nothing to do, and give the operator a
switch to turn DHT off deliberately.

An idle engine — API up, zero torrents registered — burns ~4.7% of a CPU core.
Roughly 3 points of that are the `ClientEngine`'s own background work, almost
entirely its DHT engine spinning. Measured with an isolated probe using this app's
`EngineSettings` and no torrents:

| State | CPU (one core) |
| --- | --- |
| No engine constructed at all | 0.03% |
| Engine alive, DHT off | 0.27% |
| Engine alive, DHT on | 2.58% |
| Engine disposed (was DHT on) | 0.01% |

So tearing the engine down when the roster is empty reclaims **2.57 pp** — the whole
cost, and slightly more than switching DHT off while keeping the engine alive
(2.31 pp), because an idle engine is not free either.

## Approach: recycle the engine, do not special-case DHT

The obvious alternative was to keep the `ClientEngine` alive and flip `DhtEndPoint`
to `null` while idle. This plan does **not** do that. Turning off a specific
subsystem behind the operator's back means reasoning about that subsystem's
semantics — whether the routing table survives, what a half-configured engine
reports — for a saving that is *smaller*. "No torrents, no engine" is one rule with
no DHT-specific behaviour attached: DHT configuration is untouched, it simply is not
running because nothing is running.

It also lines up with MonoTorrent's own lifecycle. It saves DHT nodes "when there
are no active instances in the engine" and restores them "when the first is
started", so a teardown at exactly that edge is when it would persist the routing
table anyway.

Recycling was verified to work in-process: three construct → add → dispose cycles
rebound TCP+UDP on the same port every time with no bind failure, and the port was
released on every dispose. Construction costs ~26ms once and ~0.5ms warm; add+start
~40ms once and ~6ms warm — i.e. the latency an operator would see on the first add
after an idle period is negligible.

## Target behaviour

A diff against [feature.md](feature.md):

- **Engine lifecycle** (new). The `ClientEngine` exists only while at least one
  torrent is registered. It is constructed on the first add and disposed after the
  last removal. `Inspect` does not need it and keeps working while it is absent; the
  read-only views (`GetSnapshot`, `GetAllSnapshots`, `GetFiles`, `TorrentCount`)
  report an empty roster, which is what they would report anyway.
- **Peer discovery** — the bullet currently states DHT, PEX and Local Peer Discovery
  are all enabled. It gains: they are enabled *while the engine is running*, and DHT
  additionally honours `TORRENT_ENABLE_DHT`.
- **Port forwarding** — unchanged, but the listen port is now bound only while
  torrents are registered. Worth stating, because an operator port-forwarding to this
  container will find the port closed on an idle engine. (MonoTorrent already bound
  listeners lazily on first torrent, so this is documentation of existing behaviour
  as much as a change.)
- `TORRENT_ENABLE_DHT=false` additionally makes the per-torrent `AllowDht` — today
  hard-coded `true` — false, so the switch is a real off switch.

Nothing changes for an engine that has torrents.

## Deliverables

- [ ] **`TORRENT_ENABLE_DHT` setting.** Declared in `manifest.json` alongside
      `TORRENT_ENABLE_PORT_MAPPING`, read in `TorrentEngineSettings` as `EnableDht`,
      **defaulting to `true`** so an upgrade changes nothing unless asked.
- [ ] **Per-torrent `AllowDht` follows the setting** (`MonoTorrentEngine.cs:207`).
- [ ] **Engine created on demand.** `StartAsync` no longer constructs a
      `ClientEngine` unconditionally: it restores one only when the persisted roster
      is non-empty. Otherwise the first `AddAsync` constructs it.
- [ ] **Engine disposed when the roster empties**, after the removal that empties it.
- [ ] **Serialized lifecycle.** One `SemaphoreSlim`-guarded reconcile owns
      construction and teardown, so a concurrent add and remove cannot race into a
      disposed-but-referenced engine or a leaked second instance.
- [ ] **Recreate must not resurrect a stale roster.** `RestoreEngineAsync` reads
      `engine-state.bin`; only the hosted-service start may restore from it. An
      engine constructed later in the session starts empty.
- [ ] **Persist an empty roster on teardown**, so a subsequent process restart does
      not restore torrents that were removed.
- [ ] **Tests** — see Testing Expectations below.
- [ ] **Docs.** Update the lifecycle, Peer discovery and Port forwarding sections of
      `feature.md`; add the variable to `docs/features/configuration.md` (a legacy
      flat doc — migrate it with `git mv` since this work touches it).
- [ ] **Correct a stale claim while in that section.** `feature.md` says that with no
      bind address the engine listens on "both `IPAddress.Any` and `IPv6Any`";
      `BuildListenEndPoints` has bound IPv4 only for some time now.
- [ ] **Version bump** to 0.6.0 (minor — new functionality while in `0.x`).

## Phases

One branch, one PR (per `AGENTS.md`); the phases are ordering, not delivery units.

1. `TORRENT_ENABLE_DHT` plumbing — manifest, settings, `AllowDht`.
2. Engine lifecycle — on-demand construction, teardown, serialization, state file.
3. Tests, docs, version.

## Decisions

Formerly open questions, resolved 2026-08-13:

- **Teardown keys off *registered* torrents, not *active* ones.** A stopped torrent
  still holds fast-resume state and a manager the API can act on, and keying off
  activity would recycle the engine on every pause and fight the VPN download gate.
- **Teardown is immediate, with no linger.** Warm construction measured ~0.5ms, so
  the rebuild cost of an add-remove-add sequence does not justify a timer. Revisit
  only if churn shows up in practice.
- **`GET /vpn` and `/healthz` are expected to behave identically with no engine.**
  Neither touches the engine today (`/healthz` is a constant, `/vpn` reads
  `VpnStatusMonitor`, which polls network interfaces). This is confirmed by code
  reading, and the Verification section pins it as a check rather than an
  assumption.

## Interaction with the DHT bootstrap problem

Worth stating, because it bounds what this plan is worth: **DHT in this deployment
currently does nothing at all.** `dht_nodes.cache` in the app data dir is 2 bytes —
`le`, an empty bencoded list — and MonoTorrent 3.0.2 ships no bootstrap routers
(verified: no domain-literal or IP-literal strings in any assembly of the package).
It seeds its routing table only from that cache, from a `.torrent`'s `nodes` field,
or from `PORT` messages sent by already-connected peers, so a cold start with magnet
links has nothing to contact. A local probe confirmed it: `Initialising` →
`NotReady`, `NodeCount = 0`, after 95 seconds.

This plan neither fixes nor worsens that. Recycling the engine is neutral with
respect to seeding: MonoTorrent persists whatever routing table exists at exactly the
teardown edge and reloads it on the next start. Seeding DHT — and reporting DHT
health so the problem is visible at all — is separate work, tracked in the
[dht feature plan](../dht/plan.md).

## Verification

- Idle container CPU with zero torrents, before and after: expect roughly 4.7% → 1.5%
  of a core (`docker stats`, plus the per-thread jiffy deltas under `/proc/1/task`
  that attributed the cost in the first place).
- Add a torrent → TCP+UDP appear on the configured port; remove the last one → both
  are released and the engine threads go away.
- Add → remove → add repeatedly: no bind failure, no leaked engine instance.
- `TORRENT_ENABLE_DHT=false` → no DHT socket is ever bound while the engine runs.
- Restart the container with torrents registered → the roster is restored as before.
- Restart after the roster was emptied → nothing is restored.
- `GET /vpn` and `GET /healthz` respond identically while no engine exists.

## Testing Expectations

Backend tests use xUnit and Imposter. Required coverage:

- `TorrentEngineSettings` reads `TORRENT_ENABLE_DHT`, including the default and a
  malformed value.
- The lifecycle rule: no engine at zero torrents, constructed on the first add,
  disposed after the last removal.
- Concurrent add/remove leaves the engine state agreeing with the torrent count, and
  never hands out a disposed engine.
- `Inspect` works with no engine present; the read-only views report an empty roster
  rather than throwing.
- With `TORRENT_ENABLE_DHT=false`, an added torrent's settings carry `AllowDht`
  false.
