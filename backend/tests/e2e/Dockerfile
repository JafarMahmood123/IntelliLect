# Test-runner image so the suite can run INSIDE the compose network
# (intellilect-net). This is the reliable way to exercise the LiveKit media loop:
# the synthetic teacher then reaches livekit-server over the container network, so
# WebRTC media flows container<->container — avoiding the host<->container ICE
# problems of Docker Desktop / VPNs. ffmpeg + gTTS are included so the teacher voice
# can also be synthesized in-network; mounting a ready WAV (E2E_TEACHER_WAV) is faster.
FROM python:3.12-slim

RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg iptables \
    && rm -rf /var/lib/apt/lists/*

# Deps only — the test code is bind-mounted at run time (see run-in-network.sh).
RUN pip install --no-cache-dir \
    pytest pytest-asyncio httpx "livekit>=1.0" "minio>=7.2" "reportlab>=4.0" "gTTS>=2.5" \
    "websockets>=13" numpy

COPY container-entrypoint.sh /usr/local/bin/container-entrypoint.sh
RUN chmod +x /usr/local/bin/container-entrypoint.sh

WORKDIR /work
ENTRYPOINT ["/usr/local/bin/container-entrypoint.sh"]
CMD ["pytest", "-m", "media", "-s"]
