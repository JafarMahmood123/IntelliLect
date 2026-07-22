#!/usr/bin/env bash
# Entrypoint for the in-network test runner.
#
# LiveKit is configured to advertise --node-ip 192.168.1.104 (the host LAN IP, so
# browsers on the LAN can reach it). Containers inside the Docker Desktop VM cannot
# route to that host IP, so the synthetic teacher's WebRTC media to the advertised
# ICE candidate fails ("wait_pc_connection timed out"). We fix it locally: DNAT the
# advertised IP to the livekit-server's actual container IP, which IS reachable on
# the compose network. Requires --cap-add=NET_ADMIN. No platform change.
set -e

NODE_IP="${E2E_LIVEKIT_ADVERTISED_IP:-192.168.1.104}"
LK_HOST="${E2E_LIVEKIT_CONTAINER:-livekit-server}"
LK_IP="$(getent hosts "$LK_HOST" | awk '{print $1}' | head -1)"

if command -v iptables >/dev/null 2>&1 && [ -n "$LK_IP" ] && [ "$LK_IP" != "$NODE_IP" ]; then
  if iptables -t nat -A OUTPUT -d "$NODE_IP" -j DNAT --to-destination "$LK_IP" 2>/dev/null; then
    echo ">>> DNAT $NODE_IP -> $LK_IP ($LK_HOST) installed for LiveKit media."
  else
    echo ">>> WARNING: could not install DNAT (need --cap-add=NET_ADMIN); media may fail."
  fi
fi

exec "$@"
