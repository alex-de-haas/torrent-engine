# VPN Profiles

Created: 2026-09-03
Updated: 2026-09-03

## Description

The OpenVPN configuration is a **folder of profile files** the operator binds into
the container, not a pasted setting. Several profiles can live there at once;
exactly one is **active**, chosen at start by a short precedence rule and
switchable at runtime through the control API without recreating the container.
The killswitch from [VPN isolation](../vpn-isolation/feature.md) stays closed
throughout a switch, and the [download gate](../vpn-isolation/feature.md#vpn-download-gate-vpnvpndownloadgatecs)
pauses and resumes torrents around the dip as it does for any tunnel outage.

Two halves cooperate, talking through two small files:

- **`docker/entrypoint.sh`** (root, `NET_ADMIN`) discovers the profiles, resolves
  the active one, runs OpenVPN, keys the killswitch to it, and owns every switch
  and restart in one **supervisor** loop. It publishes its view to a status file.
- **`Vpn/VpnProfileCatalog.cs`** lists the folder for the API, validates ids the way
  the entrypoint does, reads the status file, and writes the **selection file** the
  supervisor watches. The API never touches `openvpn` or `iptables`.

## The profiles folder (`vpn` mount)

The manifest declares a read-only, required, single-path external mount:

```jsonc
"vpn": { "kind": "host-path", "multiple": false, "mode": "ro", "service": "engine", "required": true }
```

The operator keeps their profiles in a host folder (say `/srv/hosty/vpn/`) and binds
it once — Shell "External storage", a shared global mount, or
`hosty apps mounts set com.haas.torrent-engine --mount vpn=profiles=/srv/hosty/vpn`.
Core bind-mounts it read-only at `/mnt/vpn/<label>`, injects
`HOSTY_MOUNT_VPN=<label>=/mnt/vpn/<label>`, and blocks start until it is bound.
Both halves read the first binding's path (`TorrentEngineSettings.VpnProfilesDirectory`).

Inside the folder:

- **A profile is one `<id>.ovpn` or `<id>.conf` file in the folder's root.** Its
  **id is the file name without the extension**. The listing is flat (no
  subdirectories), deduplicated (`.ovpn` wins over `.conf` for the same id), and
  sorted **ordinally** (C locale) on both sides, so "first by name" means the same
  profile to the API and to the entrypoint. A leading dot hides a file.
- **Everything else is a supporting file** — certificates, keys, `tls-crypt` keys,
  credentials — and is never listed. OpenVPN runs with `--cd <folder>`, so the
  relative paths a profile references (`ca ca.crt`, `key client.key`, …) resolve
  there. Profiles run **in place**: nothing is copied or rewritten, and CRLF files
  are fine (the entrypoint strips `\r` only from the `remote` lines it parses itself).
- **Credentials live in a sibling `<id>.auth`** (two lines: username, password) —
  **only for a profile that needs them**. When present, the entrypoint copies it
  CR-stripped into the private state dir with mode `0600` (OpenVPN insists on that
  mode and would otherwise read a trailing `\r` into the password) and passes it as
  `--auth-user-pass` after `--config`, which overrides any `auth-user-pass` directive
  inside the profile. Without one the profile runs as-is; a profile that needs
  credentials and has none fails to start, and `lastError` says so. The file is the
  operator's plain text on their host: `chmod 600` it. The read-only bind keeps the
  engine from ever writing it, and its contents never pass through the API or the
  app data directory.

## Choosing the active profile

At start the entrypoint resolves the active profile in this order, the first hit
winning:

1. the **persisted selection** — `<HOSTY_APP_DATA_DIR>/vpn/active-profile`, written
   by `PUT /vpn/profile` (below), so a switch made through the API survives a
   restart and travels with a backup;
2. the **`VPN_PROFILE`** setting (an id);
3. the **only** profile, when the folder holds exactly one;
4. the **first id in sort order**, logged as an automatic choice.

An id from rule 1 or 2 that no longer matches a file falls through to the next rule
and is reported as `lastError` (until the next switch). With no profile at all —
an empty folder, or no mount injected in a run outside Hosty — nothing starts: the
killswitch still goes up, the API starts, and `lastError` explains why.

## Switching at runtime

`PUT /vpn/profile` with `{ "id": "<profile id>" }` validates the id (`400` for
anything that is not a bare file name, `404` for an unknown id or when no folder is
configured) and then **only records the wish**: the id is written atomically to the
selection file. The response is `202 Accepted` with the current `VpnStatus`, which
still shows the profile that runs right now.

The supervisor re-reads the selection file every **2s**. When it names a profile
other than the running one, it performs the switch, fail-closed at every step:

1. validate the id and locate the file (an unknown id is recorded as `lastError`
   and the running profile is left alone);
2. publish `pendingProfile`;
3. pin the new profile's `remote` hosts (a no-op for anything pinned at boot) and
   **re-apply the killswitch** keyed to them — the default `DROP` policies persist
   across the flush, so there is no leak window;
4. stop the running OpenVPN (SIGTERM, a bounded wait, then SIGKILL — plus anything
   else named `openvpn`, so a stale PID file never leaves two clients racing for the
   tun device);
5. start the new one, with its `<id>.auth` when present.

The running profile follows the switch even when the new client fails to start:
the firewall is already keyed for it, and the supervisor's watchdog keeps retrying
it. The tunnel going down and coming back is an ordinary outage to the download
gate, which pauses and resumes torrents on its own.

The same loop is the **watchdog**: every **10s** it restarts the `openvpn` process
if it died (checked by name, so a stale PID file cannot fool it), recording the most
telling line of OpenVPN's log — an `AUTH_FAILED`, an options error — as
`lastError`, and clearing that error once the tunnel is back. Errors from boot or a
switch stay until the next switch: they explain why *this* profile runs, and the
tunnel coming up does not change that.

The supervisor's view lives in **`<VPN_STATE_DIR>/status`** (default `/run/vpn`),
`key=value` lines written whole via rename: `profile` (what runs), `pending` (a
switch in flight), `error`, `updatedAt`. The API reads it on every status request
and every poll.

## What the API reports

- **`GET /vpn`** and the SSE **`vpn`** event carry three additive `VpnStatus` fields
  beside the tunnel ones: `profile`, `pendingProfile`, `lastError` (all `null`
  outside the container). A change in any of them is a change worth an event, so a
  picker sees the switch start and finish.
- **`GET /vpn/profiles`** → `{ "active", "profiles": [ { "id", "remote" } ] }`. The
  folder is listed live on every call — a file dropped in shows up without a restart
  — and `remote` is the host[:port] of the profile's first `remote` line, enough to
  label a picker entry and never the file contents.

See [Control API](../control-api/feature.md#vpn-status-and-profiles) for the exact
contracts. The picker itself belongs to the consumer (Media Server), against these
two endpoints and the `vpn` event.

## Known limitations

- **A profile added after boot, switched to while the tunnel is down.** Remotes are
  pinned into `/etc/hosts` at boot for every profile present then; a later addition
  is resolved at switch time through the tunnel resolver, which is unreachable when
  the tunnel is down — the switch then fails with a `lastError` until a restart pins it.
- **Flat folder, exact extensions.** Only `*.ovpn` / `*.conf` in the folder's root
  are profiles (case-sensitive extension); subfolders are ignored.
- **Credentials are plain text on the host.** `<id>.auth` is as safe as the folder
  it sits in; the app adds no encryption.

## Testing Expectations

xUnit, with Imposter where a dependency is faked:

- `VpnProfileCatalogTests` — listing on a real temp folder (only profile files,
  ordinal order, `.ovpn` over `.conf`, supporting and hidden files skipped, `remote`
  extraction incl. CRLF and a missing port), id validation (separators, `..`, leading
  dot, whitespace, unknown id, a symlink leaving the folder), status-file parsing
  (missing, partial, garbage, empty values), and the atomic selection write.
- `VpnEndpointTests` — `GET /vpn/profiles` shape, `PUT /vpn/profile` → `400` /
  `404` (unknown, unconfigured) / `202`, the selection file written, `GET /vpn`
  carrying the supervisor trio.
- `VpnStatusMonitorTests` — the change predicate reacts to the trio and not to
  `checkedAt` alone.
- `TorrentEngineSettingsTests` — `HOSTY_MOUNT_VPN` parsing and the `VPN_STATE_DIR`
  default.

The entrypoint side is validated at the runtime level, in a container with
`NET_ADMIN` and `/dev/net/tun` and a folder holding two profiles (one with a
`<id>.auth`): the automatic pick and tunnel-up at boot, a switch through the
selection file (`pending` → `profile`, the killswitch re-keyed to the new `remote`,
the credentials file copied `0600` without `\r`), an unknown id recorded as `error`
with the running profile untouched, and the watchdog restarting a killed client and
clearing the error once the tunnel is back.
