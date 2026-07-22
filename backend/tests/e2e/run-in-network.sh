#!/usr/bin/env bash
# Run the E2E suite INSIDE the compose network so the synthetic teacher's LiveKit
# media flows container<->container. This is the reliable way to run the media loop
# on Docker Desktop or behind a VPN, where host<->container WebRTC ICE fails.
#
# Usage:
#   ./run-in-network.sh                # media test (default)
#   ./run-in-network.sh -m "not media" # just the orchestration seams
#   ./run-in-network.sh -k feedback -s # any pytest args
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NET="${E2E_NETWORK:-intellilect-platform_intellilect-net}"
IMAGE="${E2E_IMAGE:-intellilect-e2e}"

echo ">>> Building $IMAGE ..."
docker build -t "$IMAGE" "$DIR"

# Ensure the teacher voice exists on the host so it can be mounted (no in-container
# TTS/network needed). Falls back to in-container gTTS if this step is skipped.
WAV_ARG=()
if [ -f "$DIR/assets/teacher_line.wav" ]; then
  WAV_ARG=(-e E2E_TEACHER_WAV=/work/assets/teacher_line.wav)
fi

PYTEST_ARGS=("$@")
if [ ${#PYTEST_ARGS[@]} -eq 0 ]; then
  PYTEST_ARGS=(-m media -s)
fi

echo ">>> Running pytest on network $NET ..."
# NET_ADMIN lets the entrypoint DNAT LiveKit's advertised node-ip to the reachable
# livekit-server container IP so WebRTC media works container<->container.
exec docker run --rm --network "$NET" --cap-add=NET_ADMIN \
  -e E2E_GATEWAY_URL="http://intellilect-gateway" \
  -e E2E_USER_URL="http://user-management-service:8080" \
  -e E2E_CLASSROOM_URL="http://classroom-service:8080" \
  -e E2E_STREAMING_URL="http://streaming-service:8080" \
  -e E2E_HTTP_TIMEOUT_S="${E2E_HTTP_TIMEOUT_S:-120}" \
  -e E2E_LIVEASSISTANT_URL="http://live-assistant-service:8080" \
  -e E2E_KNOWLEDGE_URL="http://knowledge-service:8080" \
  -e E2E_LIVEKIT_WS_URL="ws://livekit-server:7880" \
  -e E2E_MINIO_ENDPOINT="intellilect-s3:9000" \
  -e E2E_INTERNAL_SECRET="${E2E_INTERNAL_SECRET:-changeme-internal-secret}" \
  "${WAV_ARG[@]}" \
  -v "$DIR:/work" -w /work \
  "$IMAGE" \
  pytest "${PYTEST_ARGS[@]}"
