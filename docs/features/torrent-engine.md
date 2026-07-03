# Torrent Engine

Status: Implemented
Created: 2026-07-03
Updated: 2026-07-03

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
[Configuration](configuration.md)); it never hard-codes ports or paths.

## Engine configuration

On `StartAsync`, the engine builds one `ClientEngine` from `EngineSettingsBuilder`:

- **Cache directory** — `{HOSTY_APP_DATA_DIR}/torrent-engine`. Holds MonoTorrent's
  fast-resume state and magnet-metadata cache, with `AutoSaveLoadFastResume` and
  `AutoSaveLoadMagnetLinkMetadata` on, so downloads and fetched metadata survive a
  restart. `StopAsync` calls `SaveStateAsync()` on shutdown.
- **Listen endpoints** — the raw L4 torrent port (`TORRENT_PORT`, default `6881`,
  TCP + UDP). With no bind address the engine listens on both `IPAddress.Any` and
  `IPv6Any`; with a bind address set (e.g. a VPN tun address) it binds **only** that
  address's family, so the port is not also re-exposed on every interface via
  `IPv6Any`. The DHT endpoint uses the same address/port.
- **Peer discovery** — DHT, Peer Exchange (PEX), and Local Peer Discovery are all
  enabled. Under an in-container VPN the tunnel is the single egress; the provider
  forwards the listen port for inbound peers.
- **Encryption** — `RC4Header`, `RC4Full`, and `PlainText` are all allowed
  (maximum peer compatibility).
- **Port forwarding** — UPnP / NAT-PMP mapping is **off** by default
  (`TORRENT_ENABLE_PORT_MAPPING`), since it is irrelevant behind a VPN.
- **Global rate limits** — `MaximumDownloadRate` / `MaximumUploadRate` from the
  engine defaults (`0` = unlimited).

Each torrent is added with per-torrent `TorrentSettings`: DHT and PEX on,
`CreateContainingDirectory` on, and its own `MaximumDownloadRate` /
`MaximumUploadRate` (the request's rates, or the engine defaults).

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
[Control API snapshot table](control-api.md#the-per-torrent-snapshot) for field
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
persistence keeps working. See [Build and deployment](build-and-deployment.md).

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
