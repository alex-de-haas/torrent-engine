# torrent-engine — Code Review

- **Date:** 2026-07-06
- **Baseline:** `main` @ `bffbd75`
- **Scope:** entire repo — `src/TorrentEngine.Api` + tests, `manifest.json`, `Dockerfile`, `docker/` (entrypoint/killswitch/VPN), `.github/workflows`, docs (light pass), git hygiene. Library behavior cross-checked against the shipped MonoTorrent 3.0.2 assemblies and the Hosty Core manifest parser.
- **Method:** full static read of every source file; the two top findings re-verified line-by-line in the main session. No builds/tests were run.

**Severity scale.** *Critical* — remote-unauthenticated compromise or guaranteed data loss. *High* — real security/data-loss path or defeats the app's core promise. *Medium* — correctness/robustness with a plausible trigger. *Low* — hardening, UX, hygiene.

**Totals:** 0 Critical / 3 High / 7 Medium / 5 Low.

---

## Executive summary

Clean, small control-plane/data-plane split: an `ITorrentEngine` wrapper over MonoTorrent, a minimal API, and an SSE hub with bounded per-subscriber channels. Concurrency (`ConcurrentDictionary` + MonoTorrent's own async model, no bespoke locking) and event lifecycle (symmetric subscribe/unsubscribe, per-manager handler detach, exactly-once completion) hold up under review. Native AOT discipline is consistent and well-commented. Request-side `savePath` traversal defense is correct.

**The weakest layer is the shell entrypoint**, which carries most of the security load (killswitch, DNS, VPN watchdog) with the least tooling — and all three High findings live there or at its boundary. The app's core promise is *VPN isolation*, and the current code both leaks around it (IPv6) and can't emit telemetry through it. Fix H1–H3 first.

---

## High

### H1 — Control API fully unauthenticated; killswitch admits the whole docker subnet
`src/TorrentEngine.Api/Api/TorrentEndpoints.cs:14-117` (all routes), `Program.cs:47-52`, `docker/entrypoint.sh:70` (`-s "$LAN_CIDR" --dport "$CONTROL_PORT" -j ACCEPT`). Any container/process on the same docker network can `POST /downloads` (write arbitrary attacker-chosen content to the mounted host storage roots — unbounded count/size, no quota, no torrent cap), `DELETE /downloads/{hash}?deleteFiles=true` (destroy completed downloads), or open unlimited SSE subscribers (`TorrentEventStream.Subscribe` has no connection cap). Acknowledged in README:57-58,155-160 as awaiting platform app-identity tokens. **Fix:** interim shared-secret header (a Hosty secret setting checked in a ~20-line middleware) removes the "anything on the bridge" exposure; add a max-active-torrents setting.

### H2 — Killswitch is IPv4-only while the engine actively listens on IPv6 (leak around the VPN)
`docker/entrypoint.sh:54` — `for cmd in iptables; do …`; `ip6tables` is **never** programmed, so IPv6 policy stays ACCEPT. *(Verified.)* `src/TorrentEngine.Api/Torrents/MonoTorrentEngine.cs:419-426` — with no `TORRENT_BIND_ADDRESS` (the default; it isn't even a declared manifest setting) `BuildListenEndPoints` binds **both** `IPAddress.Any` and `IPAddress.IPv6Any`, and `DhtEndPoint` uses `IPAddress.Any` (`:59`). *(Verified.)* On any IPv6-enabled docker network, peer traffic (inbound/outbound v6, DHT-learned v6 peers) bypasses the tunnel entirely — defeating the app's core promise. `docs/features/vpn-isolation.md:141-142` acknowledges the gap, but the code makes it worse than "uncovered": it *solicits* v6 connectivity. **Fix:** mirror the ruleset with `ip6tables` (default DROP; lo + tun0 only), and/or bind IPv4-only until v6 is deliberately supported.

### H3 — OTLP telemetry can never leave the container (silently defeated by killswitch + DNS rewrite)
`manifest.json:51` opts in and `src/TorrentEngine.Api/Telemetry/HostyTelemetry.cs:22` correctly gates on `OTEL_EXPORTER_OTLP_ENDPOINT`, but Core injects a `host.docker.internal` collector origin for docker apps. Two independent blocks: (1) the killswitch OUTPUT chain allows only lo/tun0/established/VPN-server IPs (`entrypoint.sh:57-85`) — a NEW connection to the collector egresses via the bridge and is dropped; (2) `entrypoint.sh:114-122` rewrites `resolv.conf` to `1.1.1.1`-over-tunnel, so `host.docker.internal` no longer resolves. The OTel SDK swallows export failures → invisible breakage when an operator enables observability. **Fix:** in the entrypoint, parse `OTEL_EXPORTER_OTLP_ENDPOINT`, pin its host into `/etc/hosts` before the resolv.conf rewrite, and add an explicit bridge-interface `iptables -A OUTPUT` accept for that host:port.

---

## Medium

### M1 — Malformed `.torrent` returns 500, contradicting the documented 400
`TorrentEndpoints.cs:23-31` catches only `ArgumentException`; `MonoTorrentEngine.cs:99` calls `Torrent.Load(...)`, which throws `TorrentException`/`BEncodingException` on valid-base64/garbage-bencode input (neither derives from `ArgumentException`). `docs/features/control-api.md:57` explicitly promises a 400. **Fix:** catch `TorrentException`/`BEncodingException` (or narrowly `Exception` around `Torrent.Load`) → 400.

### M2 — Restart drops all downloads; `SaveStateAsync` is write-only
`MonoTorrentEngine.cs:74` calls `_engine.SaveStateAsync()` on shutdown, but nothing ever calls `RestoreStateAsync`/loads it; `StartAsync` (`:37-65`) builds a fresh empty engine. The class doc (`:12-13`) and `docs/root.md:166-167` claim downloads survive restarts — false for an engine-only restart. The consumer (media-server `TorrentCoordinator`) re-adds non-terminal downloads only at *its own* startup, so an engine-only restart strands active downloads until the consumer also restarts. **Fix:** call `RestoreStateAsync` at start and rebuild `_managers`, or delete the dead save, fix the docs, and signal consumers to re-add (SSE `engine-started` / generation id).

### M3 — VPN watchdog restart is likely a permanent-failure loop for hostname remotes
`docker/entrypoint.sh:127-137` restarts a dead openvpn, but by then tun0 is gone, `resolv.conf` points at `1.1.1.1`-via-tunnel (`:114-122`) so the `remote` hostname can't resolve, and a re-resolved IP is only allowed if it matches the boot-time IPs (`:79-85`). The watchdog loops failing until container restart — exactly the scenario it exists to cover. **Fix:** resolve remotes once at boot into `/etc/hosts`, allow those pinned IPs, and have openvpn use the pinned name.

### M4 — Torrent-derived file paths surfaced to consumers unsanitized (consumer-side traversal vector)
`MonoTorrentEngine.cs:396-407` (`MapFiles`) builds `Path.Combine(torrentName, file.Path)` from the **raw attacker-controlled torrent name**; `NormalizeRelative` (`:409`) only swaps slashes. A hostile name (`../..` or absolute) yields a `RelativePath` that lexically escapes the save dir; a consumer combining it with its own root can be traversed. On-disk safety inside *this* app is delegated to MonoTorrent's own `PathValidator`/`PathEscape` (verified present), which also means `MapFiles` descriptor paths can *disagree* with the real escaped layout `MapManagerFiles` (`:383-394`) reports — the same torrent yields two different file lists depending on the endpoint. **Fix:** validate every emitted `RelativePath` (reject rooted paths and `..` segments); derive descriptor lists from the manager where possible. *(Note: request-side `savePath` defense in `TorrentEngineSettings.ResolveSaveDirectory:122-138` is correct, but purely lexical — symlinks not resolved — and `StringComparison.Ordinal` can false-reject on case-insensitive filesystems.)*

### M5 — Duplicate-add TOCTOU → 500 instead of 409
`TorrentEndpoints.cs:33-36` pre-checks `GetSnapshot`, then `AddAsync` at `:57`. Two concurrent POSTs of the same info hash both pass; the second `AddAsync` throws MonoTorrent's "already registered" → unhandled 500. Also `_managers[hash] = manager` (`MonoTorrentEngine.cs:158`) is an unconditional overwrite. **Fix:** catch the duplicate → 409; use `TryAdd` in the engine.

### M6 — Everything runs as root, forever
`Dockerfile` defines no user; `entrypoint.sh:139` execs the API as root. Root + `NET_ADMIN` is needed for the killswitch/openvpn/watchdog setup, but the API — which parses attacker-controlled torrent data — does not need it afterward. **Fix:** `exec setpriv --reuid=app --regid=app --clear-groups /app/TorrentEngine.Api` (the watchdog subshell stays root); pin base images by digest (`Dockerfile:5,36`).

### M7 — `VpnDownloadGate` can strand torrents in `HashingPaused`
`VpnDownloadGate.cs:103-104` — `IsActive` includes `Hashing`; pausing a hashing torrent lands it in `HashingPaused`, but the resume path (`:76`) matches only `EngineState: "Paused"` and the `finally` (`:87-90`) removes the hash from `_gatedPaused` regardless — so after VPN recovery it stays paused until manual intervention. Hashing is disk-local and never needs pausing. **Fix:** exclude `Hashing` from `IsActive` (or also match `HashingPaused` on resume).

---

## Low

- **L1.** Pause/resume/stop return 204 for unknown hashes (`MonoTorrentEngine.cs:178-200`); combined with M2 this hides consumer/engine divergence after an engine restart. Consider 404 for pause/resume/stop, keeping DELETE idempotent.
- **L2.** SSE stream has no keepalive (`TorrentEndpoints.cs:93-117`); an idle stream sends zero bytes and proxies can silently drop it. Emit `: ping` every ~20 s. Also `control-api.md:147` claims 404s carry an `ErrorResponse` body — `Results.NotFound()` sends empty.
- **L3.** Manifest staleness: `pullPolicy: "always"` (`manifest.json:17`) is ignored by current Core — remove; settings omit `TORRENT_BIND_ADDRESS` and `TORRENT_ENABLE_PORT_MAPPING` which the code reads (`TorrentEngineSettings.cs:61-62`), so they aren't Shell-settable.
- **L4.** CI never exercises AOT or the Docker build pre-merge (`ci.yml` is JIT build+test); the first `PublishAot` compile is on main in `publish.yml`, so an AOT-only regression lands before detection. Also `ci.yml` tag-pins actions while `publish.yml` SHA-pins. Add a PR job running `dotnet publish -r linux-x64` (or the Docker build without push).
- **L5.** Entrypoint robustness/doc drift: (a) an `.ovpn` `remote` with proto `tcp-client`/`udp4` yields an invalid `iptables -p` arg → under `set -eu` startup aborts cryptically (`entrypoint.sh:79-85`); map proto→tcp/udp. (b) `vpn-isolation.md:40` says the tunnel-wait timeout "logs and continues" — with `set -eu`, `wait_for_tunnel` returning 1 kills the container (`:96-98,109`); align doc and code. (c) openvpn logs to `/var/log/openvpn.log`, invisible to `docker logs`.

**Repo hygiene:** clean — `git ls-files` shows only intentional files; `.DS_Store` exists untracked and ignored; `bin/`/`obj/`/`data/`/`*.ovpn` ignored; no secrets tracked.

---

## Architecture observations

- The control-plane/data-plane split is the right shape and small; concurrency relies on `ConcurrentDictionary` + MonoTorrent's async model with no bespoke locking and holds up.
- Event lifecycle is handled carefully — no handler leaks found.
- Native AOT discipline is consistent (source-gen JSON, `TrimmerRootAssembly` for MonoTorrent's reflective serializer, RDG endpoints).
- The stateless-engine design (consumer owns durable state) is coherent, but the restart contract is under-specified in both directions (M2): the engine saves state it never restores, and the docs promise survival the system only delivers when the consumer restarts too.
- The security load sits in the shell entrypoint with the least test/review tooling — IPv6 (H2), telemetry egress (H3), and reconnect DNS (M3) are all entrypoint gaps, not C# gaps.

## Test gaps (by value)

Only two test classes exist (settings parsing/resolution; three `POST /downloads` tests). The repo's own docs demand more and none exists:

1. **Malformed-source handling** — valid-base64/garbage-bencode `.torrent` (catches M1), both/neither-source 400s, negative rates, `deleteFiles` propagation.
2. **Traversal matrix** — only one `../../` case today; add absolute `savePath` inside vs outside root, `savePath: ".."`, symlinked roots.
3. **Engine behaviors** — `Inspect` valid/invalid, snapshot edge cases (pre-metadata, ETA null, remaining-at-complete), `RemoveAsync` bookkeeping / fast-resume cleanup.
4. **VPN units** — exit-IP body parsing, `HasChanged`, and `VpnDownloadGate` reconcile (would likely have caught M7).
5. **SSE/eventing** — subscribe/unsubscribe cleanup; AOT-context serialization.

## Priority

1. **H2** IPv6 killswitch + IPv4-only bind (the core promise leaks today).
2. **H3** telemetry egress through the tunnel (or the feature is silently dead).
3. **H1** interim shared-secret auth + torrent cap.
4. **M2** restart/resume contract (or downloads vanish on any engine restart).
5. **M1 / M5** correct 400/409 mapping; **M7** hashing-pause strand; **M3** watchdog DNS.
