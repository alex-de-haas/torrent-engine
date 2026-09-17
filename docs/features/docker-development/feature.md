# Docker Development Runtime

Created: 2026-09-17
Updated: 2026-09-17

## Runtime

The `dev` profile requires Hosty Core 0.104.0 or later. Core builds `Dockerfile.dev` into a
locked development environment, mounts the selected checkout read-only at `/workspace`, and
runs the source with `dotnet watch`. Separate Docker volumes hold Linux `bin` and `obj` outputs;
the host's build outputs are not used. The image contains the .NET 10 SDK and the same OpenVPN,
iptables and routing tools as the production image. Restore packages are warmed while building
the image, then restore runs again behind the runtime firewall before watch starts.

The profile sets `HOSTY_DOCKER_DEVELOPMENT=1`, selecting a managed build for watch. Without that
explicit variable, `PublishAot` remains true. Production's default Docker profile and native
executable remain unchanged. `DOTNET_USE_POLLING_FILE_WATCHER=1` supports mounted source changes.

## VPN Boundary

The existing image entrypoint configures the default-deny firewall, starts the selected VPN
profile and its supervisor, and only then executes the supplied development command. With no
usable VPN profile, the API can still start and report the problem while peer egress remains
blocked, matching production behavior. Development grants only the already-declared NET_ADMIN
capability and `/dev/net/tun` device. Download and VPN folders use the existing operator-bound
external mounts; persistent app data stays at `/app/data`.

A source edit is handled by watch without an image rebuild. Dockerfile, SDK or package-baseline
changes require incrementing the manifest's `build.revision` and reviewing that environment change
through Core. The Dockerfile and build context are trusted local source; they are not a sandbox
for untrusted build instructions. Missing packages cannot be downloaded by runtime restore while
the VPN is unavailable; rebuild the environment to warm new dependencies.

## Testing Expectations

- `dotnet test src/TorrentEngine.Api.Tests` covers the API/engine regression suite.
- `sh -n docker/entrypoint.sh` checks entrypoint syntax; production MSBuild evaluates PublishAot=true.
- The companion Hosty test `Torrent_CoreManagedSourceStartsBehindClosedVpnFirewall` installs this
  manifest through an isolated Core instance, starts source behind the real firewall with an empty
  VPN folder, checks API readiness and source reload, and verifies direct TCP egress is refused.
  Opt in with `HOSTY_TEST_TORRENT_SOURCE=<checkout>` in the Hosty Core test project.
- The companion `Torrent_DevelopmentTransfersThroughVpnAndFailsClosedOnTunnelLoss` test additionally
  takes `HOSTY_TEST_TORRENT_VPN=<authorized-profile-folder>` and
  `HOSTY_TEST_TORRENT_METADATA=<legal-test.torrent>`. It verifies actual payload and completed pieces,
  mid-transfer tunnel loss, fresh and established connection isolation, bridge packet capture and
  automatic recovery. It uses temporary Core state/downloads and a test-only packet observer;
  production containers and read-only VPN files are not modified. The IPv4 scenario passed on
  macOS/Docker Desktop; other platform/IPv6 acceptance remains tracked in the relevant plans.
