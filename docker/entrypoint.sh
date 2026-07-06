#!/bin/sh
# Bring up the OpenVPN tunnel behind a default-deny killswitch, then launch the control API.
#
# Goal: the BitTorrent engine's peer traffic may leave ONLY through the VPN tunnel (tun0),
# while the control API stays reachable on the docker bridge for the consumer app. If the
# tunnel drops, default-deny ensures no torrent traffic leaks to the direct connection.
#
# NOTE: first cut. Validate with real leak tests (kill the tunnel; confirm no peer traffic
# egresses the bridge) before trusting it for privacy-sensitive use.
set -eu

CONFIG_DIR=/etc/openvpn/hosty
mkdir -p "$CONFIG_DIR"
chmod 700 "$CONFIG_DIR"

: "${OPENVPN_CONFIG:?OPENVPN_CONFIG (the .ovpn contents, raw or base64) is required}"
# The .ovpn arrives as a Hosty setting. A single-line secret field mangles the newlines OpenVPN needs
# ("Maximum option line length exceeded"), so we accept a base64 encoding of the config. Strip any
# whitespace first: such a field also flattens wrapped-base64 line breaks into spaces, which `base64 -d`
# would otherwise reject. A real .ovpn is not valid base64 (it contains '#', '-', '.'), so the decode
# only succeeds on actually-encoded input; anything else falls back to the raw contents.
if printf '%s' "$OPENVPN_CONFIG" | tr -d '[:space:]' | base64 -d > "$CONFIG_DIR/client.ovpn" 2>/dev/null \
  && grep -qiE '^[[:space:]]*(client|remote|proto|dev)[[:space:]]' "$CONFIG_DIR/client.ovpn"; then
  echo "openvpn: using base64-decoded config"
else
  printf '%s\n' "$OPENVPN_CONFIG" > "$CONFIG_DIR/client.ovpn"
fi

# Router-exported .ovpn files are commonly CRLF. Strip the CRs so the killswitch's `remote` parsing
# below doesn't carry a trailing carriage return into values like the port (iptables: Port "1194\r").
tr -d '\r' < "$CONFIG_DIR/client.ovpn" > "$CONFIG_DIR/client.ovpn.tmp" \
  && mv "$CONFIG_DIR/client.ovpn.tmp" "$CONFIG_DIR/client.ovpn"

AUTH_ARGS=""
if [ -n "${OPENVPN_USERNAME:-}" ]; then
  printf '%s\n%s\n' "$OPENVPN_USERNAME" "${OPENVPN_PASSWORD:-}" > "$CONFIG_DIR/auth.txt"
  chmod 600 "$CONFIG_DIR/auth.txt"
  AUTH_ARGS="--auth-user-pass $CONFIG_DIR/auth.txt"
fi

# Port the control API listens on *inside* the container (from ASPNETCORE_URLS / the manifest
# containerPort). This must be the in-container dport docker forwards to — NOT HOSTY_PORT_CONTROL,
# which is the host-published port; opening that leaves the killswitch blocking the actual API port.
CONTROL_PORT="$(printf '%s' "${ASPNETCORE_URLS:-}" | grep -oE ':[0-9]+' | head -1 | tr -d ':')"
: "${CONTROL_PORT:=8080}"

# Primary (non-tunnel) interface, its subnet, and gateway — the docker bridge the consumer reaches us on.
# The gateway is needed to pin host routes (e.g. the telemetry collector) that must keep using the bridge
# once OpenVPN's redirect-gateway makes tun0 the default route.
LAN_IF="$(ip route | awk '/^default/ {print $5; exit}')"
LAN_CIDR="$(ip -o -f inet addr show "$LAN_IF" | awk '{print $4; exit}')"
LAN_GW="$(ip route | awk '/^default/ {print $3; exit}')"

echo "killswitch: lan_if=$LAN_IF lan_cidr=$LAN_CIDR lan_gw=$LAN_GW control_port=$CONTROL_PORT"

# Resolve a hostname to IPv4 address(es) with the resolver live *now* (docker's embedded DNS at 127.0.0.11),
# before resolv.conf is repointed at the tunnel. Passes an IPv4 literal straight through; empty otherwise.
resolve_ipv4() {
  case "$1" in
    *[!0-9.]*) getent ahostsv4 "$1" 2>/dev/null | awk '{print $1}' | sort -u ;;
    *) printf '%s\n' "$1" ;;
  esac
}

# Pin host->IP into /etc/hosts so a later lookup succeeds even when the tunnel DNS is unreachable — e.g. an
# OpenVPN reconnect after the watchdog restart (resolv.conf points at the down tunnel), or the telemetry
# exporter resolving the collector after the resolv.conf rewrite. No-op for an IP literal or an existing entry.
pin_host() {
  case "$1" in *[!0-9.]*) ;; *) return 0 ;; esac
  if grep -qiE "[[:space:]]$1([[:space:]]|\$)" /etc/hosts 2>/dev/null; then
    return 0
  fi
  for ip in $(resolve_ipv4 "$1"); do
    if printf '%s %s\n' "$ip" "$1" >> /etc/hosts 2>/dev/null; then
      echo "hosts: pinned $1 -> $ip"
    else
      echo "hosts: could not pin $1 (read-only /etc/hosts?)" >&2
    fi
    return 0
  done
}

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
  iptables -A OUTPUT -o tun0 -j ACCEPT
  iptables -A INPUT -i tun0 -j ACCEPT

  # Allow reaching the VPN server endpoint(s) on the bridge so the tunnel can be established. Parse
  # `remote <host> [port] [proto]` from the .ovpn (default udp/1194), pin each resolved IP into /etc/hosts
  # so a reconnect resolves without the tunnel DNS, and open the bridge to it.
  grep -E '^[[:space:]]*remote[[:space:]]' "$CONFIG_DIR/client.ovpn" | while read -r _ host port proto _; do
    port="${port:-1194}"
    proto="${proto:-udp}"
    # iptables -p accepts only tcp/udp; an .ovpn may say tcp-client/udp4/udp6/tcp4/tcp6.
    case "$proto" in
      tcp*) proto=tcp ;;
      *) proto=udp ;;
    esac
    pin_host "$host"
    for ip in $(resolve_ipv4 "$host"); do
      iptables -A OUTPUT -o "$LAN_IF" -p "$proto" -d "$ip" --dport "$port" -j ACCEPT
    done
  done

  allow_collector
  apply_ip6_killswitch || echo "killswitch: IPv6 rules could not be applied (v6 left at default policy)" >&2
}

# Telemetry egress: Hosty Core injects an OTLP collector endpoint (typically host.docker.internal) reachable
# only on the bridge, not the tunnel. Without help the killswitch drops the NEW connection to it *and* the
# resolv.conf rewrite makes its host unresolvable, so exports are silently lost. Pin the host, add a /32 route
# so it keeps using the bridge once redirect-gateway makes tun0 the default, and open the bridge to it.
allow_collector() {
  [ -n "${OTEL_EXPORTER_OTLP_ENDPOINT:-}" ] || return 0
  hostport="$(printf '%s' "$OTEL_EXPORTER_OTLP_ENDPOINT" | sed -E 's#^[a-zA-Z][a-zA-Z0-9+.-]*://##; s#/.*$##')"
  host="${hostport%%:*}"
  port="${hostport##*:}"
  case "$port" in ""|"$host") port=4318 ;; esac  # default OTLP http/protobuf port when the URL omits one
  [ -n "$host" ] || return 0
  pin_host "$host"
  for ip in $(resolve_ipv4 "$host"); do
    ip route add "$ip" via "$LAN_GW" dev "$LAN_IF" 2>/dev/null || true
    iptables -A OUTPUT -o "$LAN_IF" -p tcp -d "$ip" --dport "$port" -j ACCEPT
    echo "telemetry: allowed collector $host ($ip:$port) via the bridge"
  done
}

# IPv6 killswitch: the engine binds IPv4-only, but on an IPv6-enabled docker network any stray v6 traffic
# would bypass the (IPv4) tunnel entirely. Default-deny v6, allowing only loopback, established, and tun0.
# Best-effort: skipped when the container has no v6 stack (nothing to leak) or ip6tables is unavailable.
apply_ip6_killswitch() {
  command -v ip6tables >/dev/null 2>&1 || { echo "killswitch: ip6tables unavailable; skipping IPv6 rules" >&2; return 0; }
  [ -e /proc/net/if_inet6 ] || { echo "killswitch: no IPv6 stack in container; no IPv6 rules needed"; return 0; }

  ip6tables -F
  ip6tables -X 2>/dev/null || true
  ip6tables -P INPUT DROP
  ip6tables -P OUTPUT DROP
  ip6tables -P FORWARD DROP
  ip6tables -A INPUT -i lo -j ACCEPT
  ip6tables -A OUTPUT -o lo -j ACCEPT
  ip6tables -A INPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
  ip6tables -A OUTPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
  # Allow the tunnel in case it is itself v6-capable; everything else v6 stays dropped.
  ip6tables -A OUTPUT -o tun0 -j ACCEPT
  ip6tables -A INPUT -i tun0 -j ACCEPT
}

wait_for_tunnel() {
  for _ in $(seq 1 60); do
    if ip link show tun0 >/dev/null 2>&1; then
      echo "tunnel: tun0 is up"
      return 0
    fi
    sleep 1
  done
  echo "tunnel: tun0 did not come up within 60s" >&2
  return 1
}

start_openvpn() {
  openvpn --config "$CONFIG_DIR/client.ovpn" $AUTH_ARGS \
    --daemon --writepid /run/openvpn.pid --log /var/log/openvpn.log
}

apply_killswitch

start_openvpn

# Mirror OpenVPN's log to stdout so it shows up in `docker logs` (it only writes to the file otherwise).
# One follower survives watchdog restarts since they reuse the same log file.
touch /var/log/openvpn.log
(tail -n +1 -F /var/log/openvpn.log 2>/dev/null | sed 's/^/openvpn: /') &

# Best-effort: if the tunnel doesn't appear in time, log and continue rather than aborting the container
# (set -eu would otherwise treat the non-zero return as fatal). The killswitch keeps traffic contained
# regardless, and OpenVPN keeps retrying, so the API can start and report status while it comes up.
wait_for_tunnel || echo "tunnel: continuing without a confirmed tunnel; killswitch keeps traffic contained" >&2

# Route DNS through the tunnel. The host/docker resolver (e.g. Docker Desktop's 192.168.65.7, or any
# bridge-only address) becomes unreachable once redirect-gateway sends all traffic over tun0, and using
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

# Watchdog: OpenVPN's own keepalive/ping-restart recovers network drops without exiting, so this only
# covers the rare case where the openvpn process itself dies — restart it so the tunnel (and thus the
# killswitch's only egress path) comes back instead of staying down until a container restart. Runs in
# the background; the API stays PID 1 (exec) so it still receives signals for a clean shutdown.
(
  while true; do
    sleep 10
    # Check the process by name (robust against a stale PID file or PID reuse).
    if ! pidof openvpn >/dev/null 2>&1; then
      echo "watchdog: openvpn is not running, restarting" >&2
      start_openvpn || echo "watchdog: openvpn restart failed" >&2
    fi
  done
) &

exec /app/TorrentEngine.Api
