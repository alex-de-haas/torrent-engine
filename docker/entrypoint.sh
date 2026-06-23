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
# The .ovpn arrives as a Hosty setting. A single-line secret field flattens newlines, which OpenVPN
# rejects ("Maximum option line length exceeded"). So accept a base64-encoded config (newline-safe) and
# fall back to the raw contents — a real .ovpn is not valid base64 (spaces, '#', '.'), so the decode
# attempt only succeeds on actually-encoded input.
if printf '%s' "$OPENVPN_CONFIG" | base64 -d > "$CONFIG_DIR/client.ovpn" 2>/dev/null \
  && grep -qiE '^[[:space:]]*(client|remote|proto|dev)[[:space:]]' "$CONFIG_DIR/client.ovpn"; then
  echo "openvpn: using base64-decoded config"
else
  printf '%s\n' "$OPENVPN_CONFIG" > "$CONFIG_DIR/client.ovpn"
fi

AUTH_ARGS=""
if [ -n "${OPENVPN_USERNAME:-}" ]; then
  printf '%s\n%s\n' "$OPENVPN_USERNAME" "${OPENVPN_PASSWORD:-}" > "$CONFIG_DIR/auth.txt"
  chmod 600 "$CONFIG_DIR/auth.txt"
  AUTH_ARGS="--auth-user-pass $CONFIG_DIR/auth.txt"
fi

CONTROL_PORT="${HOSTY_PORT_CONTROL:-8080}"

# Primary (non-tunnel) interface and its subnet — the docker bridge the consumer reaches us on.
LAN_IF="$(ip route | awk '/^default/ {print $5; exit}')"
LAN_CIDR="$(ip -o -f inet addr show "$LAN_IF" | awk '{print $4; exit}')"

echo "killswitch: lan_if=$LAN_IF lan_cidr=$LAN_CIDR control_port=$CONTROL_PORT"

apply_killswitch() {
  for cmd in iptables; do
    $cmd -F
    $cmd -X 2>/dev/null || true
    $cmd -P INPUT DROP
    $cmd -P OUTPUT DROP
    $cmd -P FORWARD DROP

    # Loopback (includes docker's embedded DNS at 127.0.0.11).
    $cmd -A INPUT -i lo -j ACCEPT
    $cmd -A OUTPUT -o lo -j ACCEPT

    # Keep established/related flowing both ways.
    $cmd -A INPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
    $cmd -A OUTPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT

    # Control API: accept new connections from the docker subnet to the control port only.
    $cmd -A INPUT -i "$LAN_IF" -s "$LAN_CIDR" -p tcp --dport "$CONTROL_PORT" -j ACCEPT

    # Everything over the tunnel.
    $cmd -A OUTPUT -o tun0 -j ACCEPT
    $cmd -A INPUT -i tun0 -j ACCEPT
  done

  # Allow reaching the VPN server endpoint(s) on the bridge so the tunnel can be established.
  # Parse `remote <host> [port] [proto]` from the .ovpn; default proto udp, port 1194.
  grep -E '^[[:space:]]*remote[[:space:]]' "$CONFIG_DIR/client.ovpn" | while read -r _ host port proto _; do
    port="${port:-1194}"
    proto="${proto:-udp}"
    for ip in $(getent ahostsv4 "$host" | awk '{print $1}' | sort -u); do
      iptables -A OUTPUT -o "$LAN_IF" -p "$proto" -d "$ip" --dport "$port" -j ACCEPT
    done
  done
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

apply_killswitch

openvpn --config "$CONFIG_DIR/client.ovpn" $AUTH_ARGS \
  --daemon --writepid /run/openvpn.pid --log /var/log/openvpn.log

wait_for_tunnel

exec dotnet TorrentEngine.Api.dll
