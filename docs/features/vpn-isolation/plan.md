# VPN Isolation — Validation Hardening

Status: In Progress
Created: 2026-07-28
Updated: 2026-09-17

## Goal

The killswitch and the telemetry-collector allowance both shipped as first
implementations, verified by reading the rules rather than by observing traffic.
This plan is about proving them against a real tunnel, so the app's core promise —
no peer traffic leaves outside `tun0` — rests on evidence rather than on the rules
looking correct.

## Target behaviour

The owner authorized real VPN validation as part of the Docker development runtime on
2026-09-17. IPv4 acceptance exposed and corrected an overly broad established OUTPUT
allowance; [feature.md](feature.md) describes the corrected rules and observed behavior.
The remaining work verifies IPv6 isolation and actual collector delivery. Any rule that
fails its observation is fixed as part of the deliverable that found it.

## Deliverables

- [x] **Killswitch leak test.** With a real VPN endpoint and an active download,
      drop the tunnel and confirm no peer traffic egresses the bridge. Observe the
      bridge interface directly (packet capture on `eth0`), not just the iptables
      counters — a rule can be present and still be bypassed by traffic that never
      traverses it. Cover the drop happening mid-transfer, not only before start.
- [ ] **IPv6 leak test.** Same, on an IPv6-enabled docker network: confirm the
      `ip6tables` default-deny holds and the engine solicits no v6 peers or DHT.
- [ ] **Telemetry egress validation.** With observability enabled, confirm OTLP
      exports actually arrive at the collector — after the `resolv.conf` rewrite and
      with the collector reached over the pinned `/32` bridge route. A silent drop
      here is invisible from inside the container.
- [ ] Fold whatever the tests reveal back into `docker/entrypoint.sh` and
      [feature.md](feature.md).

## Scope And Verification Decisions

The owner approved implementing Docker development and real VPN validation in chat, supplied
and enabled the VPN for the test, and authorized review fixes and merge on 2026-09-17. The
transition to In Progress records that authorized IPv4 validation, not approval of new network
support. The remaining IPv6 and collector checks stay explicit deliverables; merge does not
mark them complete.

- The implemented leak test is opt-in operator acceptance through the Core-managed fixture.
  It requires an authorized VPN folder and legal test torrent. It is not part of unattended CI;
  ordinary unit/build checks need no VPN credentials. This reflects the implemented verification
  method, rather than a new requirement to provision VPN secrets in CI.
- Multi-homed hosts and IPv6-only VPN remotes remain documented limitations, outside this
  implementation. The pending IPv6 leak test checks default-deny behavior on an IPv6-enabled
  Docker network; it does not expand supported VPN remote/address configurations.

## Verification

- Packet capture from the drop moment onward showing zero peer-destined packets on
  `eth0`, for both IPv4 and IPv6.
- A trace and a metric from the engine visible in the collector's backend while the
  tunnel is up.
- The engine's own state after a drop/restore cycle: torrents paused by the gate,
  then resumed, with no stranded entries.

## IPv4 Evidence (2026-09-17)

The Core-managed source-runtime test transferred and verified Debian torrent pieces through
the real VPN, forced the tunnel down, and observed automatic pause and recovery. Before the
fix, a protective test guard intercepted 55 packets from established connections. With the
restricted control-reply allowance, both guard counters and eth0 capture reported zero leaked
packets; captured control API traffic confirmed the observer was active. The source/dev image
was verified; this does not claim deployment to the installed production image or IPv6 coverage.
