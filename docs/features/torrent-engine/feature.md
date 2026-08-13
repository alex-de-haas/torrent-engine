# Torrent Engine

Created: 2026-07-03
Updated: 2026-08-13

## Description

`MonoTorrentEngine` (`src/TorrentEngine.Api/Torrents/MonoTorrentEngine.cs`) is a
thin wrapper over MonoTorrent's `ClientEngine`. It is registered once and serves
three roles at the same time: the `ITorrentEngine` the control API resolves, the
`IHostedService` that starts/stops the `ClientEngine` with the app, and the source
of the events the broadcaster forwards onto SSE. It was ported from Media Server's
in-process engine and deliberately decoupled from any database or pipeline — it
owns **no** persistence beyond MonoTorrent's own fast-resume/metadata cache, and
surfaces only live snapshots plus a few transition events.

The engine is configured entirely from `TorrentEngineSettings` (see
[Configuration](../configuration/feature.md)); it never hard-codes ports or paths.

## Engine lifecycle

The `ClientEngine` exists only while at least one torrent is registered. An idle one
costs ~2.6% of a CPU core doing nothing (almost all of it the DHT engine spinning),
so:

- **`StartAsync`** restores the persisted roster and keeps the engine only when that
  roster is non-empty. With nothing to restore, the app starts with no engine at all.
- **The first `AddAsync` constructs it** (~0.5ms warm, so the operator-visible latency
  of the first add after an idle period is negligible).
- **The removal that empties the roster disposes it**, immediately and with no linger.
  Teardown keys off *registered* torrents, not active ones — a stopped torrent still
  holds a manager the API can act on.

A single lock serializes every construction and teardown, and counts the operations
currently holding the engine, so a concurrent add and remove can neither leak a second
instance nor dispose one the other is still using. `AddAsync`, `RemoveAsync` and the
manager operations (`PauseAsync` / `ResumeAsync` / `StopAsync`) all take that lease for
the duration of the call: MonoTorrent throws once the engine behind a manager is
disposed, and a concurrent removal of the last torrent would otherwise do exactly that
mid-call.

Everything that does not need the engine keeps working while it is absent: `Inspect`
parses sources as usual, and the read-only views (`GetSnapshot`, `GetAllSnapshots`,
`GetFiles`, `TorrentCount`) report the empty roster they would report anyway. Those
views deliberately take **no** lease — every manager property they read stays valid
after the engine is disposed, so the hot progress-tick path stays lock-free. `GET
/healthz` and `GET /vpn` never touch the engine and are unaffected.

Only `StartAsync` reads the persisted state file, so an engine constructed later in
the session always starts empty — recycling cannot resurrect a stale roster. The
teardown persists the now-empty roster, so a later process restart does not bring back
torrents that were removed; if that write fails (a full or read-only data volume), the
stale file is deleted instead, because with an empty roster "restore nothing" is the
correct outcome and the only one that keeps deleted downloads deleted.

## Engine configuration

Each time the engine is constructed, it is built from `EngineSettingsBuilder`:

- **Cache directory** — `{HOSTY_APP_DATA_DIR}/torrent-engine`. Holds MonoTorrent's
  fast-resume state and magnet-metadata cache, with `AutoSaveLoadFastResume` and
  `AutoSaveLoadMagnetLinkMetadata` on, so downloads and fetched metadata survive a
  restart. `StopAsync` calls `SaveStateAsync()` on shutdown.
- **Listen endpoints** — the raw L4 torrent port (`TORRENT_PORT`, default `6881`,
  TCP + UDP). With no bind address the engine listens on `IPAddress.Any` — IPv4 only,
  because the engine must not solicit IPv6 peers that could bypass the (historically
  IPv4-only) tunnel. With a bind address set (e.g. a VPN tun address) it binds **only**
  that address's family. The DHT endpoint uses the same address/port.
- **Peer discovery** — Peer Exchange (PEX) and Local Peer Discovery are always
  enabled, and DHT is enabled unless `TORRENT_ENABLE_DHT` is `false`; all of them run
  only while the engine does, i.e. while a torrent is registered. Under an
  in-container VPN the tunnel is the single egress; the provider forwards the listen
  port for inbound peers.
- **Encryption** — `RC4Header`, `RC4Full`, and `PlainText` are all allowed
  (maximum peer compatibility).
- **Port forwarding** — UPnP / NAT-PMP mapping is **off** by default
  (`TORRENT_ENABLE_PORT_MAPPING`), since it is irrelevant behind a VPN. Note that the
  listen port is bound only while torrents are registered, so an operator
  port-forwarding to this container finds the port closed while it sits idle.
- **Global rate limits** — `MaximumDownloadRate` / `MaximumUploadRate` from the
  engine defaults (`0` = unlimited).

Each torrent is added with per-torrent `TorrentSettings`: PEX on, DHT following
`TORRENT_ENABLE_DHT`, `CreateContainingDirectory` on, and its own
`MaximumDownloadRate` / `MaximumUploadRate` (the request's rates, or the engine
defaults).

`TORRENT_ENABLE_DHT=false` is a real off switch rather than a half-configured engine:
no DHT endpoint is bound *and* each torrent carries `AllowDht` false, so peer
discovery falls back to trackers, PEX and Local Peer Discovery.

Changing the setting also reaches torrents that already exist. A restored manager
carries the per-torrent settings serialized on the previous run, so `StartAsync`
re-applies `AllowDht` to each one it restores — rebuilt from that manager's own
settings, so per-download rate limits are left untouched.

## Lifecycle and operations

`ITorrentEngine` (`Torrents/ITorrentEngine.cs`) is the full contract:

- **`Inspect(source)`** — parses a magnet or `.torrent` to read its info hash (and,
  for a `.torrent`, size + file list) **without** adding it. The control API uses
  this to fail a bad source or a duplicate add before mutating engine state.
- **`AddAsync(source, saveDirectory, limits, autoStart, ct)`** — adds the torrent,
  records its `addedAt` before exposing the manager (so any snapshot that observes
  the torrent also observes a stable `addedAt`), wires the state-changed handler,
  optionally starts it, and — for a magnet — kicks off a background
  `WaitForMetadataAsync` that raises `MetadataReceived` once the file list is known.
  A `.torrent` already has metadata, so it raises `MetadataReceived` immediately.
- **`PauseAsync` / `ResumeAsync` / `StopAsync`** — map to the MonoTorrent manager
  (`PauseAsync` / `StartAsync` / `StopAsync`); all no-op for an unknown hash.
- **`RemoveAsync(infoHash, deleteFiles, ct)`** — stops the torrent (unless already
  stopped/errored), removes it from the engine (`DownloadedDataOnly` when
  `deleteFiles`, else `KeepAllData`, always `| CacheDataOnly`), and clears the
  persisted fast-resume file for that hash. Removal proceeds even if the stop fails.
- **`GetSnapshot` / `GetAllSnapshots` / `GetFiles`** — read-only live views;
  `GetFiles` returns `null` for an unknown hash and an empty list for a torrent
  whose metadata has not yet arrived.
- **`TorrentCount`** — how many torrents are registered, without building a snapshot
  per torrent. It is the emptiness check the background loops use to skip a tick
  that has nothing to act on (see [VPN isolation](../vpn-isolation/feature.md)).

State is held in three concurrent dictionaries keyed by info hash (managers,
completion-raised guard, and `addedAt`), all cleaned up together in `RemoveAsync`.

## State transitions → events

`MonoTorrentEngine` subscribes to each manager's `TorrentStateChanged` and raises
three engine events the broadcaster forwards onto SSE:

- **`DownloadErrored`** — on transition to `TorrentState.Error`.
- **`DownloadCompleted`** — MonoTorrent moves `Downloading → Seeding` the instant a
  torrent completes (and a re-added complete torrent lands in `Seeding` after
  hashing). Completion is guarded by a set so it is raised **exactly once** per
  info hash, whichever path reaches it.
- **`MetadataReceived`** — raised immediately for a `.torrent`, or after
  `WaitForMetadataAsync` completes for a magnet.

## Snapshot derivation

`ToSnapshot` computes the live view (see the
[Control API snapshot table](../control-api/feature.md#the-per-torrent-snapshot) for field
semantics). The non-obvious parts:

- **Size** — `Torrent.Size` once known, else the magnet's advertised size, else `0`.
- **Progress-derived remaining** — `remainingBytes` is derived from
  `manager.Progress` (0–100, the bitfield percentage), **not** from the session byte
  counter, which diverges from completed content after a resume. It is pinned to `0`
  once complete so floating-point rounding never leaves a stray byte.
- **ETA** — `ceil(remaining / downloadRate)`, but `null` when complete, when the
  rate is `0` (stalled), or when size is unknown — so a consumer never renders a
  bogus "∞" or a countdown for a paused torrent.
- **Session counters** — `downloadedBytes` / `uploadedBytes` come from the
  MonoTorrent `Monitor` and are **session-scoped** (reset on restart); `ratio` is
  computed from them. This is why `downloadedBytes` is not the same as completed
  content after a resume.
- **Piece stats** — meaningful only once metadata is known. A metadata-less magnet
  carries a placeholder 1-bit bitfield, so the code gates on `Torrent` being present
  and reports `0/0` pieces pre-metadata (the documented contract).
- **Peer split** — `peers` is `OpenConnections`; `seeds` / `leeches` /
  `availablePeers` come from `manager.Peers` (`Seeds` / `Leechs` / `Available`).
- **`addedAt` / `elapsedSeconds`** — MonoTorrent does not track add time, so the
  engine records it. `AddedAtOf` uses `GetOrAdd` so a snapshot that races ahead of
  `AddAsync`'s `TryAdd` still gets a single stable timestamp for the session rather
  than a fresh `UtcNow` on every call.

## Native AOT note

The engine ships in a Native AOT binary. `ClientEngine.SaveStateAsync()` serializes
`EngineSettings` reflectively (MonoTorrent's `Serializer` walks public properties),
which the trimmer cannot see, so `TorrentEngine.Api.csproj` roots the
**`MonoTorrent.Client`** assembly (the one that actually defines `Serializer` +
`EngineSettings` — not the `MonoTorrent` facade) with `TrimmerRootAssembly` so state
persistence keeps working. See [Build and deployment](../build-and-deployment.md).

## Testing Expectations

Backend tests use xUnit and Imposter. Required coverage:

- `Inspect` for a valid/invalid magnet and a valid/invalid `.torrent` (info hash,
  size, file mapping; `ArgumentException` on bad input).
- Save-directory creation and `addedAt` stability across concurrent snapshot reads.
- Snapshot derivation edge cases: pre-metadata magnet (`0/0` pieces, `0` size,
  `null` name), stalled/complete ETA being `null`, remaining pinned to `0` on
  complete.
- `RemoveAsync` clears fast-resume and per-hash bookkeeping, and tolerates a
  never-started / unknown hash.
- The engine lifecycle: no engine at zero torrents, constructed on the first add,
  disposed after the last removal, and rebuilt cleanly across repeated add/remove
  cycles.
- Concurrent adds and removes leave the engine's existence agreeing with the torrent
  count, and never hand out a disposed engine.
- `Inspect` and the read-only views work with no engine present, and a remove of an
  unknown hash does not construct one.
- A restart restores a non-empty roster, and restores nothing after the roster was
  emptied — including when the teardown could not rewrite the state file and had to
  discard it instead.
- Manager operations are no-ops (and construct nothing) with no engine present.
- A restored torrent picks up a changed `TORRENT_ENABLE_DHT` without losing its
  per-download rate limits.
- `TORRENT_ENABLE_DHT`: the default, a malformed value, and that `false` both leaves
  the DHT endpoint unbound and carries `AllowDht` false onto added torrents.
