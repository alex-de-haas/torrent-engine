#!/bin/sh
# Bring up the OpenVPN tunnel behind a default-deny killswitch, then launch the control API.
#
# Goal: the BitTorrent engine's peer traffic may leave ONLY through the VPN tunnel (tun0),
# while the control API stays reachable on the docker bridge for the consumer app. If the
# tunnel drops, default-deny ensures no torrent traffic leaks to the direct connection.
#
# The OpenVPN profiles are the operator's own files in the read-only `vpn` mount (HOSTY_MOUNT_VPN):
# one `<id>.ovpn` (or `.conf`) per profile, an optional `<id>.auth` (username, password — only for a
# profile that needs one) beside it, and whatever certificates/keys a profile references by relative
# path. One profile is active at a time. The API records a switch in the app-data selection file and
# the supervisor loop below performs it; the supervisor's view flows back through $VPN_STATE_DIR/status.
#
# NOTE: first cut. Validate with real leak tests (kill the tunnel; confirm no peer traffic
# egresses the bridge) before trusting it for privacy-sensitive use.
set -eu

VPN_IF="${VPN_INTERFACE:-tun0}"
STATE_DIR="${VPN_STATE_DIR:-/run/vpn}"
STATUS_FILE="$STATE_DIR/status"
AUTH_FILE="$STATE_DIR/auth"
PID_FILE="$STATE_DIR/openvpn.pid"
LOG_FILE=/var/log/openvpn.log
SELECTION_FILE="${HOSTY_APP_DATA_DIR:-/app/data}/vpn/active-profile"
mkdir -p "$STATE_DIR"
chmod 700 "$STATE_DIR"

# Profiles folder. HOSTY_MOUNT_VPN is `label=path[,label=path…]`; the slot takes a single path, so the first wins.
PROFILE_DIR=""
if [ -n "${HOSTY_MOUNT_VPN:-}" ]; then
  first="${HOSTY_MOUNT_VPN%%,*}"
  case "$first" in
    *=*) PROFILE_DIR="${first#*=}" ;;
    *) PROFILE_DIR="$first" ;;
  esac
fi

# Publish the supervisor's view for the API (VpnProfileCatalog reads it): the profile that runs, the one a
# switch is moving to, and the last failure. key=value lines, single-line values, written whole via rename.
write_status() {
  err="$(printf '%s' "${3:-}" | tr -d '\r' | tr '\n' ' ')"
  printf 'profile=%s\npending=%s\nerror=%s\nupdatedAt=%s\n' "${1:-}" "${2:-}" "$err" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$STATUS_FILE.tmp" \
    && mv "$STATUS_FILE.tmp" "$STATUS_FILE"
}

# A profile id is a bare file name: no path separators, no leading dot. Mirrors VpnProfileCatalog.IsValidId.
valid_id() {
  case "$1" in
    ''|.*|*/*|*\\*) return 1 ;;
  esac
  return 0
}

# Prints the profile file for an id (`.ovpn` wins over `.conf`), or fails.
profile_path() {
  valid_id "$1" || return 1
  for ext in ovpn conf; do
    if [ -f "$PROFILE_DIR/$1.$ext" ]; then
      printf '%s\n' "$PROFILE_DIR/$1.$ext"
      return 0
    fi
  done
  return 1
}

# Every profile id in the folder, one per line, deduped and sorted in the C locale. VpnProfileCatalog sorts
# ordinally to match, so "first by name" means the same profile on both sides.
list_profile_ids() {
  [ -d "$PROFILE_DIR" ] || return 0
  for f in "$PROFILE_DIR"/*.ovpn "$PROFILE_DIR"/*.conf; do
    [ -f "$f" ] || continue
    id="${f##*/}"
    id="${id%.*}"
    if valid_id "$id"; then
      printf '%s\n' "$id"
    fi
  done | LC_ALL=C sort -u
}

# The id the API last recorded through PUT /vpn/profile (first line of the selection file), or empty.
read_selection() {
  if [ -f "$SELECTION_FILE" ]; then
    head -n 1 "$SELECTION_FILE" 2>/dev/null | tr -d '\r\n' || true
  fi
}

# Resolves the profile to start with into ACTIVE (which may stay empty), noting in BOOT_ERROR why a configured
# choice was skipped. Precedence: the persisted selection (a switch made through the API), VPN_PROFILE, the only
# profile, else the first by name.
ACTIVE=""
BOOT_ERROR=""
resolve_active_profile() {
  selection="$(read_selection)"
  if [ -n "$selection" ]; then
    if profile_path "$selection" >/dev/null; then
      ACTIVE="$selection"
      echo "profiles: using the selected profile '$ACTIVE'"
      return 0
    fi
    BOOT_ERROR="selected profile '$selection' is not in the profiles folder"
    echo "profiles: $BOOT_ERROR; falling back" >&2
  fi
  if [ -n "${VPN_PROFILE:-}" ]; then
    if profile_path "$VPN_PROFILE" >/dev/null; then
      ACTIVE="$VPN_PROFILE"
      echo "profiles: using VPN_PROFILE '$ACTIVE'"
      return 0
    fi
    BOOT_ERROR="VPN_PROFILE '$VPN_PROFILE' is not in the profiles folder"
    echo "profiles: $BOOT_ERROR; falling back" >&2
  fi
  ids="$(list_profile_ids)"
  count="$(printf '%s\n' "$ids" | grep -c . || true)"
  first="$(printf '%s\n' "$ids" | head -n 1)"
  case "$count" in
    0)
      if [ -z "$PROFILE_DIR" ]; then
        BOOT_ERROR="no profiles folder: bind the app's 'vpn' mount (HOSTY_MOUNT_VPN is unset)"
      else
        BOOT_ERROR="no OpenVPN profiles (*.ovpn / *.conf) in $PROFILE_DIR"
      fi
      echo "profiles: $BOOT_ERROR" >&2
      return 1
      ;;
    1)
      ACTIVE="$first"
      echo "profiles: using the only profile '$ACTIVE'"
      ;;
    *)
      ACTIVE="$first"
      echo "profiles: $count profiles and nothing selected; using the first by name, '$ACTIVE' (set VPN_PROFILE or PUT /vpn/profile to choose)"
      ;;
  esac
  return 0
}

# Port the control API listens on *inside* the container (from ASPNETCORE_URLS / the manifest
# containerPort). This must be the in-container dport docker forwards to — NOT HOSTY_PORT_CONTROL,
# which is the host-published port; opening that leaves the killswitch blocking the actual API port.
CONTROL_PORT="$(printf '%s' "${ASPNETCORE_URLS:-}" | grep -oE ':[0-9]+' | head -1 | tr -d ':')"
: "${CONTROL_PORT:=8080}"

# Primary (non-tunnel) interface, its subnet, and gateway — the docker bridge the consumer reaches us on.
# The gateway is needed to pin host routes (e.g. the telemetry collector) that must keep using the bridge
# once OpenVPN's redirect-gateway makes the tunnel the default route.
LAN_IF="$(ip route | awk '/^default/ {print $5; exit}')"
LAN_CIDR="$(ip -o -f inet addr show "$LAN_IF" | awk '{print $4; exit}')"
LAN_GW="$(ip route | awk '/^default/ {print $3; exit}')"

echo "killswitch: lan_if=$LAN_IF lan_cidr=$LAN_CIDR lan_gw=$LAN_GW control_port=$CONTROL_PORT tunnel=$VPN_IF"

# Resolve a hostname to IPv4 address(es) with the resolver live *now* (docker's embedded DNS at 127.0.0.11
# before resolv.conf is repointed at the tunnel; /etc/hosts for anything pinned). Passes an IPv4 literal
# straight through; empty otherwise.
resolve_ipv4() {
  case "$1" in
    *[!0-9.]*) getent ahostsv4 "$1" 2>/dev/null | awk '{print $1}' | sort -u ;;
    *) printf '%s\n' "$1" ;;
  esac
}

# Pin host->IP into /etc/hosts so a later lookup succeeds even when the tunnel DNS is unreachable — e.g. an
# OpenVPN reconnect after a watchdog restart or a profile switch (resolv.conf points at the down tunnel), or
# the telemetry exporter resolving the collector after the resolv.conf rewrite. No-op for an IP literal or an
# existing entry.
pin_host() {
  case "$1" in *[!0-9.]*) ;; *) return 0 ;; esac
  if grep -qiE "[[:space:]]$1([[:space:]]|\$)" /etc/hosts 2>/dev/null; then
    return 0
  fi
  # Pin every resolved A record (redundancy for reconnects), not just the first.
  for ip in $(resolve_ipv4 "$1"); do
    if printf '%s %s\n' "$ip" "$1" >> /etc/hosts 2>/dev/null; then
      echo "hosts: pinned $1 -> $ip"
    else
      echo "hosts: could not pin $1 (read-only /etc/hosts?)" >&2
      return 0
    fi
  done
  return 0
}

# The `remote <host> [port] [proto]` lines of one profile, CR-stripped (router-exported .ovpn files are
# commonly CRLF, and a trailing '\r' would otherwise ride into the port: iptables: Port "1194\r").
profile_remotes() {
  grep -E '^[[:space:]]*remote[[:space:]]' "$1" | tr -d '\r'
}

# Open the bridge to one profile's VPN endpoint(s) — just enough for OpenVPN to establish the tunnel.
# Default udp/1194; a non-numeric port is left to OpenVPN to reject rather than fed to iptables.
allow_vpn_endpoints() {
  profile_remotes "$1" | while read -r _ host port proto _; do
    port="${port:-1194}"
    proto="${proto:-udp}"
    case "$port" in *[!0-9]*) port=1194 ;; esac
    # iptables -p accepts only tcp/udp; an .ovpn may say tcp-client/udp4/udp6/tcp4/tcp6.
    case "$proto" in
      tcp*) proto=tcp ;;
      *) proto=udp ;;
    esac
    for ip in $(resolve_ipv4 "$host"); do
      iptables -A OUTPUT -o "$LAN_IF" -p "$proto" -d "$ip" --dport "$port" -j ACCEPT
    done
  done
}

pin_profile_remotes() {
  profile_remotes "$1" | while read -r _ host _; do
    pin_host "$host"
  done
}

# Pin the remotes of EVERY profile while docker's DNS is still the resolver, so a later switch — with the
# tunnel, and thus the tunnel resolver, possibly down — needs no lookup. Only the active profile's endpoints
# are opened in the killswitch (allow_vpn_endpoints): least privilege on the bridge.
pin_all_remotes() {
  list_profile_ids | while read -r id; do
    cfg="$(profile_path "$id")" || continue
    pin_profile_remotes "$cfg"
  done
}

# Default-deny with the minimum opened: loopback, established, the control API from the docker subnet, the
# tunnel, the active profile's VPN endpoint(s) on the bridge ($1, optional), and the telemetry collector.
# Re-applied on a profile switch: the policies persist across the flush, so there is no leak window.
apply_killswitch() {
  iptables -F
  iptables -X 2>/dev/null || true
  iptables -P INPUT DROP
  iptables -P OUTPUT DROP
  iptables -P FORWARD DROP

  # Loopback (includes docker's embedded DNS at 127.0.0.11).
  iptables -A INPUT -i lo -j ACCEPT
  iptables -A OUTPUT -o lo -j ACCEPT

  # Keep established/related flowing both ways.
  iptables -A INPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
  iptables -A OUTPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT

  # Control API: accept new connections from the docker subnet to the control port only.
  iptables -A INPUT -i "$LAN_IF" -s "$LAN_CIDR" -p tcp --dport "$CONTROL_PORT" -j ACCEPT

  # Everything over the tunnel.
  iptables -A OUTPUT -o "$VPN_IF" -j ACCEPT
  iptables -A INPUT -i "$VPN_IF" -j ACCEPT

  if [ -n "${1:-}" ]; then
    allow_vpn_endpoints "$1"
  fi

  allow_collector
  apply_ip6_killswitch || echo "killswitch: IPv6 rules could not be applied (v6 left at default policy)" >&2
}

# Telemetry egress: Hosty Core injects an OTLP collector endpoint (typically host.docker.internal) reachable
# only on the bridge, not the tunnel. Without help the killswitch drops the NEW connection to it *and* the
# resolv.conf rewrite makes its host unresolvable, so exports are silently lost. Pin the host, add a /32 route
# so it keeps using the bridge once redirect-gateway makes the tunnel the default, and open the bridge to it.
allow_collector() {
  [ -n "${OTEL_EXPORTER_OTLP_ENDPOINT:-}" ] || return 0
  hostport="$(printf '%s' "$OTEL_EXPORTER_OTLP_ENDPOINT" | sed -E 's#^[a-zA-Z][a-zA-Z0-9+.-]*://##; s#/.*$##')"
  host="${hostport%%:*}"
  port="${hostport##*:}"
  # Fall back to the default OTLP http/protobuf port unless we parsed a purely-numeric one — a missing or
  # malformed port must not feed iptables a bad --dport (which, under set -e, would abort the container).
  case "$port" in ""|"$host"|*[!0-9]*) port=4318 ;; esac
  [ -n "$host" ] || return 0
  pin_host "$host"
  # Best-effort: a route/rule failure here must not stop the API from starting (telemetry is optional).
  for ip in $(resolve_ipv4 "$host"); do
    ip route add "$ip" via "$LAN_GW" dev "$LAN_IF" 2>/dev/null || true
    if iptables -A OUTPUT -o "$LAN_IF" -p tcp -d "$ip" --dport "$port" -j ACCEPT 2>/dev/null; then
      echo "telemetry: allowed collector $host ($ip:$port) via the bridge"
    else
      echo "telemetry: could not open the bridge to collector $host ($ip:$port)" >&2
    fi
  done
}

# IPv6 killswitch: the engine binds IPv4-only, but on an IPv6-enabled docker network any stray v6 traffic
# would bypass the (IPv4) tunnel entirely. Default-deny v6, allowing only loopback, established, and the tunnel.
# Best-effort: skipped when the container has no v6 stack (nothing to leak) or ip6tables is unavailable.
apply_ip6_killswitch() {
  command -v ip6tables >/dev/null 2>&1 || { echo "killswitch: ip6tables unavailable; skipping IPv6 rules" >&2; return 0; }
  [ -e /proc/net/if_inet6 ] || { echo "killswitch: no IPv6 stack in container; no IPv6 rules needed"; return 0; }

  # Fail *closed*: the default-deny policies are the actual leak defense, so set them first and gate on
  # them — if any won't apply (broken ip6tables), bail before adding ACCEPT rules rather than leaving the
  # chains at their default-ACCEPT policy. The allow rules below are then best-effort (worst case: v6 is
  # more restricted than intended, never less). Under set -e the caller invokes this via `|| echo`, so a
  # non-zero return is logged, not fatal.
  ip6tables -P INPUT DROP && ip6tables -P OUTPUT DROP && ip6tables -P FORWARD DROP || {
    echo "killswitch: could not set IPv6 default-deny policy" >&2
    return 1
  }
  ip6tables -F || true
  ip6tables -X 2>/dev/null || true
  ip6tables -A INPUT -i lo -j ACCEPT || true
  ip6tables -A OUTPUT -o lo -j ACCEPT || true
  ip6tables -A INPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT || true
  ip6tables -A OUTPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT || true
  # Allow the tunnel in case it is itself v6-capable; everything else v6 stays dropped.
  ip6tables -A OUTPUT -o "$VPN_IF" -j ACCEPT || true
  ip6tables -A INPUT -i "$VPN_IF" -j ACCEPT || true
  return 0
}

wait_for_tunnel() {
  for _ in $(seq 1 60); do
    if ip link show "$VPN_IF" >/dev/null 2>&1; then
      echo "tunnel: $VPN_IF is up"
      return 0
    fi
    sleep 1
  done
  echo "tunnel: $VPN_IF did not come up within 60s" >&2
  return 1
}

# Starts OpenVPN for a profile id, in place from the read-only folder (--cd resolves the relative paths a
# profile references). A `<id>.auth` beside it supplies username/password: copied CR-stripped into the private
# state dir (OpenVPN wants 0600 and no '\r' in the password line) and passed after --config, so it overrides
# any auth-user-pass directive inside the profile. Without one the profile runs as-is.
start_openvpn() {
  cfg="$(profile_path "$1")" || { echo "openvpn: profile '$1' not found" >&2; return 1; }
  auth_args=""
  rm -f "$AUTH_FILE"
  if [ -f "$PROFILE_DIR/$1.auth" ]; then
    tr -d '\r' < "$PROFILE_DIR/$1.auth" > "$AUTH_FILE"
    chmod 600 "$AUTH_FILE"
    auth_args="--auth-user-pass $AUTH_FILE"
    echo "openvpn: starting '$1' with credentials from $1.auth"
  else
    echo "openvpn: starting '$1'"
  fi
  # shellcheck disable=SC2086 # auth_args is deliberately word-split (two words or none)
  openvpn --cd "$PROFILE_DIR" --config "$cfg" $auth_args \
    --daemon --writepid "$PID_FILE" --log "$LOG_FILE"
}

# Stops the OpenVPN we started (SIGTERM, bounded wait, SIGKILL), then anything else by that name — a stale pid
# file after a crash must not leave a second client racing for the tun device.
stop_openvpn() {
  pid="$(cat "$PID_FILE" 2>/dev/null || true)"
  if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
    kill "$pid" 2>/dev/null || true
    for _ in $(seq 1 10); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 1
    done
    if kill -0 "$pid" 2>/dev/null; then
      kill -9 "$pid" 2>/dev/null || true
    fi
  fi
  for p in $(pidof openvpn 2>/dev/null || true); do
    kill -9 "$p" 2>/dev/null || true
  done
  rm -f "$PID_FILE"
}

# The most telling line of OpenVPN's log for a status error: the last one naming an error or auth failure,
# else simply the last non-empty one (a bare "Use --help" trailer would otherwise hide the actual message).
last_log_line() {
  line="$(grep -iE 'error|fatal|auth_failed|failed|cannot|could not' "$LOG_FILE" 2>/dev/null | tail -n 1 || true)"
  if [ -z "$line" ]; then
    line="$(grep -v '^[[:space:]]*$' "$LOG_FILE" 2>/dev/null | tail -n 1 || true)"
  fi
  printf '%s\n' "$line" | cut -c1-200
}

# The switch the supervisor performs when the selection file names another profile. Fail-closed throughout:
# the killswitch is re-keyed to the new profile's endpoint(s) first (policies stay DROP across the flush), then
# the old client is stopped and the new one started. CURRENT follows the switch even when the new client fails
# to start — the firewall is already keyed for it, and the watchdog keeps retrying it.
switch_profile() {
  cfg="$(profile_path "$1")" || {
    write_status "$CURRENT" "" "profile '$1' is not in the profiles folder"
    return 1
  }
  echo "profiles: switching from '${CURRENT:-none}' to '$1'"
  write_status "$CURRENT" "$1" ""
  pin_profile_remotes "$cfg"
  apply_killswitch "$cfg"
  stop_openvpn
  CURRENT="$1"
  if start_openvpn "$1"; then
    STATUS_ERROR=""
  else
    STATUS_ERROR="openvpn failed to start for '$1'"
  fi
  write_status "$CURRENT" "" "$STATUS_ERROR"
}

# One loop owns the OpenVPN process, so a watchdog restart and a switch can never race. Every 2s it acts on a
# changed selection file (a switch made through PUT /vpn/profile); otherwise, every 10s, it restarts a dead
# client — OpenVPN's own keepalive/ping-restart recovers ordinary network drops without exiting, so this only
# covers the process itself dying. Runs in the background; the API stays PID 1 (exec) for clean shutdown.
supervise() {
  last_seen="$(read_selection)"
  ticks=0
  while true; do
    sleep 2
    ticks=$((ticks + 1))
    desired="$(read_selection)"
    if [ "$desired" != "$last_seen" ]; then
      last_seen="$desired"
      if [ -n "$desired" ] && [ "$desired" != "$CURRENT" ]; then
        switch_profile "$desired" || true
      fi
      continue
    fi
    if [ -n "$CURRENT" ] && [ $((ticks % 5)) -eq 0 ]; then
      if ! pidof openvpn >/dev/null 2>&1; then
        STATUS_ERROR="openvpn exited: $(last_log_line)"
        echo "watchdog: openvpn is not running; restarting '$CURRENT' ($STATUS_ERROR)" >&2
        write_status "$CURRENT" "" "$STATUS_ERROR"
        start_openvpn "$CURRENT" || echo "watchdog: openvpn restart failed" >&2
      elif ip link show "$VPN_IF" >/dev/null 2>&1; then
        # The client is back up after an exit: that failure is history. Boot/switch errors stay until the next
        # switch — they explain why *this* profile runs, and the tunnel coming up does not change that.
        case "$STATUS_ERROR" in
          "openvpn exited:"*)
            STATUS_ERROR=""
            write_status "$CURRENT" "" ""
            ;;
        esac
      fi
    fi
  done
}

# ---- boot ----

resolve_active_profile || true
ACTIVE_CFG=""
if [ -n "$ACTIVE" ]; then
  ACTIVE_CFG="$(profile_path "$ACTIVE")"
fi
CURRENT="$ACTIVE"
STATUS_ERROR="$BOOT_ERROR"
write_status "$CURRENT" "" "$STATUS_ERROR"

# The killswitch goes up BEFORE OpenVPN starts, so there is no window where traffic can leak.
pin_all_remotes
apply_killswitch "$ACTIVE_CFG"

if [ -n "$CURRENT" ]; then
  if ! start_openvpn "$CURRENT"; then
    STATUS_ERROR="openvpn failed to start for '$CURRENT'"
    write_status "$CURRENT" "" "$STATUS_ERROR"
  fi
fi

# Mirror OpenVPN's log to stdout so it shows up in `docker logs` (it only writes to the file otherwise).
# One follower survives restarts and switches since they reuse the same log file.
touch "$LOG_FILE"
(tail -n +1 -F "$LOG_FILE" 2>/dev/null | sed 's/^/openvpn: /') &

# Best-effort: if the tunnel doesn't appear in time, log and continue rather than aborting the container
# (set -eu would otherwise treat the non-zero return as fatal). The killswitch keeps traffic contained
# regardless, and OpenVPN keeps retrying, so the API can start and report status while it comes up.
if [ -n "$CURRENT" ]; then
  wait_for_tunnel || echo "tunnel: continuing without a confirmed tunnel; killswitch keeps traffic contained" >&2
else
  echo "tunnel: no profile to start; the API reports why, and the killswitch keeps traffic contained" >&2
fi

# Route DNS through the tunnel. The host/docker resolver (e.g. Docker Desktop's 192.168.65.7, or any
# bridge-only address) becomes unreachable once redirect-gateway sends all traffic over the tunnel, and using
# it would leak lookups outside the VPN. Point resolv.conf at a tunnel-reachable resolver instead.
VPN_DNS="${VPN_DNS:-1.1.1.1}"
if [ -n "$VPN_DNS" ]; then
  if : > /etc/resolv.conf 2>/dev/null; then
    for ns in $VPN_DNS; do printf 'nameserver %s\n' "$ns" >> /etc/resolv.conf; done
    echo "dns: routing lookups through the tunnel via $VPN_DNS"
  else
    echo "dns: could not rewrite /etc/resolv.conf (read-only?); lookups may fail" >&2
  fi
fi

supervise &

exec /app/TorrentEngine.Api
