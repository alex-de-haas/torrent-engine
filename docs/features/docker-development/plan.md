# Docker Development Runtime

Status: In Progress
Created: 2026-09-17
Updated: 2026-09-17

## Goal

Run editable Torrent Engine source inside the existing VPN boundary, using Hosty Core's Docker development contract. Approved together with the mixed-development-runtimes plan in docker-host on 2026-09-17.

## Deliverables

- [x] Add the SDK development environment and source-mounted dev profile, preserving production defaults.
- [x] Run the development command only after the existing VPN/firewall entrypoint initialization.
- [x] Validate the manifest through Core 0.104.0, build the SDK environment, and verify source execution/reload with the default-deny firewall and no VPN.
- [x] Verify a controlled torrent transfer through a working VPN, isolation for fresh and established connections on tunnel loss, and automatic transfer recovery.
- [x] Document the implemented profile and regenerate the index.
- [x] Prepare the companion changes for review alongside Hosty Core support.
- [ ] Record Windows runtime acceptance; native Linux remains unverified without an available host.

## Verification

Run unit tests, shell syntax checks, and a Core-managed container test with a controlled torrent and VPN configuration. Development caches must not modify host build outputs or delete source and app data.

## Verification Evidence (2026-09-17)

The companion Core VPN fixture passed on macOS/Docker Desktop with the operator-provided VPN
folder mounted read-only. It received 1,359,872 bytes and verified five pieces of an official
Debian netinst torrent, forced the test tunnel down, observed automatic pause, verified zero
direct egress using a final guard and bridge packet capture, then observed automatic transfer
recovery. The original established OUTPUT rule allowed 55 packets to reach the test guard;
the entrypoint now restricts bridge replies to the control API instead of permitting all
established connections. No production container was modified. IPv6-enabled network and
telemetry egress acceptance remain tracked in the VPN isolation plan.

The owner requested paired PRs before remaining platform acceptance and explicitly authorized
resolving review comments and merging on 2026-09-17, with Windows operator acceptance afterward.
This owner-approved exception allows the feature PR to merge while these acceptance deliverables
remain unchecked; it does not claim that Windows or native Linux verification passed. Merge/release Hosty Core 0.104.0 before adopting this manifest: older Core versions
do not understand its Docker source recipe. The remaining checks are not claimed as completed.
