#!/usr/bin/env bash
# Run the full "teacher teaches -> gets feedback" E2E reliably.
#
# It puts the LiveAssistant agent in fake-audio mode (AGENT_AUDIO_SOURCE=fake): the
# agent plays a WAV of the teacher through the REAL pipeline (STT -> idea boundary ->
# RagService retrieval -> Ollama brain -> pacing) and records the delivered
# feedback, which the test reads back. This exercises the real feedback intelligence
# and the full cross-service flow without needing WebRTC media (which Docker Desktop /
# a VPN often block). The test then runs INSIDE the compose network so it reaches
# services directly (no nginx 60s cap).
#
# Usage:
#   ./run-in-network.sh                 # the feedback loop (default)
#   ./run-in-network.sh -m "not media"  # just the cross-service wiring
#   ./run-in-network.sh -m media        # the real-WebRTC path (needs media to flow)
#   ./run-in-network.sh -m latency      # the §9 latency budgets (no agent rebuild)
#   ./run-in-network.sh -m internal     # the /api/internal guard (no agent rebuild)
#   ./run-in-network.sh -k xyz -s       # any pytest args
#
# Revert LiveAssistant to normal (LiveKit audio) afterwards:
#   cd <backend> && docker compose up -d live-assistant-service
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND="$(cd "$DIR/../.." && pwd)"
NET="${E2E_NETWORK:-intellilect-platform_intellilect-net}"
IMAGE="${E2E_IMAGE:-intellilect-e2e}"
WAV="$DIR/assets/teacher_line.wav"

# 0) Does this run actually need the agent in fake-audio mode?
#
#    Only the audio-driven markers do. Reconfiguring for the others is not merely
#    wasted time: `up -d --build live-assistant-service` RESTARTS the service, and its
#    Prometheus histograms are in-process — so a latency run that rebuilt first would
#    read an empty `idea_to_feedback_latency_seconds` and report L-3 as unmeasurable,
#    every time, for a reason nothing in the output would explain.
MARKERS=""
for ((i = 1; i <= $#; i++)); do
  if [ "${!i}" = "-m" ]; then
    j=$((i + 1))
    MARKERS="${MARKERS} ${!j:-}"
  fi
done
NEEDS_AGENT_AUDIO=1
if [ -n "${MARKERS// /}" ] && ! echo "$MARKERS" | grep -Eq 'feedback|media'; then
  NEEDS_AGENT_AUDIO=0
  echo ">>> Markers '${MARKERS# }' need no teacher audio — leaving live-assistant-service alone."
fi

# 1) Ensure the teacher voice WAV exists (mounted into LiveAssistant + used by the
#    real-media path). Generated from the test's TEACHER_WRONG_LINE via the host venv.
if [ "$NEEDS_AGENT_AUDIO" = "1" ] && [ ! -f "$WAV" ]; then
  echo ">>> Synthesizing teacher voice ($WAV) ..."
  if [ -x "$DIR/.venv/bin/python" ]; then
    ( cd "$DIR" && .venv/bin/python - <<'PY'
import test_teaching_feedback_e2e as t
from support.tts import synthesize_teacher_wav
print("wrote", synthesize_teacher_wav(t.TEACHER_WRONG_LINE, teacher_wav_path="", piper_model_path=""))
PY
    )
  else
    echo "!!! No $DIR/.venv and no $WAV. Create the venv (see README) or set E2E_TEACHER_WAV." >&2
    exit 1
  fi
fi

echo ">>> Building test runner image ($IMAGE) ..."
docker build -t "$IMAGE" "$DIR"

# 2) Put LiveAssistant in fake-audio mode (rebuild to pick up code, mount the WAV).
if [ "$NEEDS_AGENT_AUDIO" = "1" ]; then
  echo ">>> Reconfiguring live-assistant-service for fake-audio mode ..."
  export E2E_WAV_HOST_PATH="$WAV"
  ( cd "$BACKEND" && docker compose -f docker-compose.yml -f tests/e2e/docker-compose.e2e.yml \
      up -d --build live-assistant-service )

  # Wait for it to come back healthy.
  echo ">>> Waiting for live-assistant-service ..."
  until curl -sf -o /dev/null "http://localhost:8084/health"; do sleep 2; done
fi

PYTEST_ARGS=("$@")
if [ ${#PYTEST_ARGS[@]} -eq 0 ]; then
  PYTEST_ARGS=(-m feedback -s)
fi

echo ">>> Running pytest on network $NET ..."
# NET_ADMIN is only needed by the real-media (-m media) path's DNAT; harmless otherwise.
exec docker run --rm --network "$NET" --cap-add=NET_ADMIN \
  -e E2E_GATEWAY_URL="http://intellilect-gateway" \
  -e E2E_USER_URL="http://user-management-service:8080" \
  -e E2E_CLASSROOM_URL="http://classroom-service:8080" \
  -e E2E_STREAMING_URL="http://streaming-service:8080" \
  -e E2E_HTTP_TIMEOUT_S="${E2E_HTTP_TIMEOUT_S:-120}" \
  -e E2E_LIVEASSISTANT_URL="http://live-assistant-service:8080" \
  -e E2E_KNOWLEDGE_URL="http://rag-service:8080" \
  -e E2E_LIVEKIT_WS_URL="ws://livekit-server:7880" \
  -e E2E_MINIO_ENDPOINT="intellilect-s3:9000" \
  -e E2E_INTERNAL_SECRET="${E2E_INTERNAL_SECRET:-changeme-internal-secret}" \
  -e E2E_FAKE_AUDIO="1" \
  -e E2E_TEACHER_WAV="/work/assets/teacher_line.wav" \
  -v "$DIR:/work" -w /work \
  "$IMAGE" \
  pytest "${PYTEST_ARGS[@]}"
