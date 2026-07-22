"""Environment-driven configuration for the cross-service E2E.

Every value has a default matching `backend/docker-compose.yml` + the per-service
`docker-compose.unit.yml` files, so a plain `docker compose up -d` from `backend/`
is enough to run against. Override any value via the `E2E_*` environment variables
when your setup differs (different host, ports, or secret).
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field


def _env(name: str, default: str) -> str:
    return os.environ.get(name, default)


@dataclass(frozen=True)
class Config:
    # --- Public HTTP surface (through the nginx gateway on :80) ---------------
    # User, Classroom and Streaming are reachable via the gateway. Each can also be
    # pointed straight at its service (same route paths) — useful when running inside
    # the docker network to bypass nginx's fixed 60s proxy timeout on the slow,
    # synchronous session-start chain. Default: the gateway.
    gateway_url: str = _env("E2E_GATEWAY_URL", "http://localhost")
    user_url: str = _env("E2E_USER_URL", "") or _env("E2E_GATEWAY_URL", "http://localhost")
    classroom_url: str = _env("E2E_CLASSROOM_URL", "") or _env("E2E_GATEWAY_URL", "http://localhost")
    streaming_url: str = _env("E2E_STREAMING_URL", "") or _env("E2E_GATEWAY_URL", "http://localhost")

    # --- Internal Python services (published host ports, hit with X-Internal-Secret)
    liveassistant_url: str = _env("E2E_LIVEASSISTANT_URL", "http://localhost:8084")
    knowledge_url: str = _env("E2E_KNOWLEDGE_URL", "http://localhost:8083")

    # The shared service-to-service secret (X-Internal-Secret header).
    internal_secret: str = _env("E2E_INTERNAL_SECRET", "changeme-internal-secret")

    # --- LiveKit -------------------------------------------------------------
    # The synthetic teacher connects here. The token response also carries a host
    # (StreamResponse.liveKitHost); we prefer that at runtime and fall back to this.
    livekit_ws_url: str = _env("E2E_LIVEKIT_WS_URL", "ws://192.168.1.104:7880")

    # --- MinIO / S3 (for seeding classroom material) -------------------------
    minio_endpoint: str = _env("E2E_MINIO_ENDPOINT", "localhost:9000")
    minio_access_key: str = _env("E2E_MINIO_ACCESS_KEY", "testuser")
    minio_secret_key: str = _env("E2E_MINIO_SECRET_KEY", "testpassword123!")
    minio_secure: bool = _env("E2E_MINIO_SECURE", "false").lower() == "true"
    s3_bucket: str = _env("E2E_S3_BUCKET", "intellilect-files")

    # --- Seeded admin account (DatabaseSeeder in UserManagementService) -------
    admin_email: str = _env("E2E_ADMIN_EMAIL", "admin@intellilect.com")
    admin_password: str = _env("E2E_ADMIN_PASSWORD", "Admin123!")

    # Password used for every teacher/student the test registers.
    user_password: str = _env("E2E_USER_PASSWORD", "Passw0rd!23")

    # --- TTS: how to synthesize the teacher's spoken line --------------------
    # If set, a ready-made 16-bit PCM WAV is used verbatim (skips piper/espeak).
    teacher_wav_path: str = _env("E2E_TEACHER_WAV", "")
    # Optional piper voice model (.onnx). If present, piper is preferred for TTS.
    piper_model_path: str = _env("E2E_PIPER_MODEL", "")

    # --- Timeouts / polling --------------------------------------------------
    # Generous per-call timeout: KnowledgeService embeds via host Ollama, which can
    # be briefly busy serving the LLM/STT, so a single call may take tens of seconds.
    http_timeout_s: float = float(_env("E2E_HTTP_TIMEOUT_S", "60"))
    # The AI loop (STT model load on first use + boundary + retrieval + LLM eval)
    # is slow; give feedback delivery a generous window.
    feedback_timeout_s: float = float(_env("E2E_FEEDBACK_TIMEOUT_S", "240"))
    # First ingest is slow: the Ollama embedding model loads cold on the first call.
    ingest_timeout_s: float = float(_env("E2E_INGEST_TIMEOUT_S", "240"))
    platform_ready_timeout_s: float = float(_env("E2E_PLATFORM_READY_TIMEOUT_S", "120"))

    # Number of students to enroll in the scenario.
    student_count: int = int(_env("E2E_STUDENT_COUNT", "2"))

    def internal_headers(self) -> dict[str, str]:
        return {"X-Internal-Secret": self.internal_secret}


CONFIG = Config()
