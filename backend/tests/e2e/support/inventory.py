"""The platform's service inventory, and what proves each one is alive (§10.1).

One table, used by two very different tests. `test_smoke.py` runs the probes against a
deployment; `test_smoke_inventory.py` checks the table itself against the compose
files, with nothing running.

Keeping them in one place is the point. A smoke suite's characteristic failure is not
a wrong assertion — it is a service that quietly stopped being covered, so the suite
stays green while proving less every release.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import yaml

# support/ -> e2e/ -> tests/ -> backend/. Only the containerless inventory test reads
# these paths; the in-network smoke run bind-mounts just e2e/ at /work, so they will not
# exist there and that test skips itself.
BACKEND = Path(__file__).resolve().parents[3]

COMPOSE_FILES = [
    BACKEND / "docker-compose.yml",
    BACKEND / "UserManagementService/docker-compose.unit.yml",
    BACKEND / "EmailService/docker-compose.unit.yml",
    BACKEND / "ClassroomService/docker-compose.unit.yml",
    BACKEND / "StreamingService/docker-compose.unit.yml",
    BACKEND / "RagService/docker-compose.unit.yml",
    BACKEND / "LiveAssistantService/docker-compose.unit.yml",
]


def compose_services() -> set[str]:
    """Every service in the compose graph, read from the files rather than listed here.

    Read, not hard-coded, so that adding a service to compose and forgetting to give it
    a probe is a test failure instead of a silent hole.
    """
    names: set[str] = set()
    for path in COMPOSE_FILES:
        document = yaml.safe_load(path.read_text(encoding="utf-8")) or {}
        names.update((document.get("services") or {}).keys())
    return names


@dataclass(frozen=True)
class Probe:
    """One HTTP liveness probe: the URL to hit and what counts as alive."""

    service: str
    url_env: str  # config attribute holding the base URL
    path: str
    # A 503 from /health is a *successful* probe of a broken service — it means the
    # endpoint works. Only 2xx counts as alive, and that distinction is the whole
    # reason the DatabaseHealthCheck exists (see §10.1): before it, /health could not
    # answer anything but 200.
    description: str


# --- our services: each answers /health, and each /health can now fail ---------------

PROBES: tuple[Probe, ...] = (
    Probe(
        "user-service",
        "user_url",
        "/health",
        "owns login; had no health endpoint at all until §10.1",
    ),
    Probe("classroom-service", "classroom_url", "/health", "classrooms, sessions, quizzes"),
    Probe("streaming-service", "streaming_url", "/health", "tokens, hub, recordings"),
    Probe("rag-service", "knowledge_url", "/health", "ingestion + retrieval; 503 when its DB is gone"),
    Probe("live-assistant-service", "liveassistant_url", "/health", "the agent pipeline"),
)

# EmailService is reachable only inside the compose network — it publishes no host port
# and nginx does not route to it, so it is probed in-network only.
IN_NETWORK_ONLY: dict[str, str] = {
    "email-service": "http://email-service:8080/health",
}

# Infrastructure with its own HTTP liveness surface.
INFRASTRUCTURE_PROBES: dict[str, str] = {
    "intellilect-s3": "/minio/health/live",
    "gateway": "/health",
}

# --- everything else, with the reason it is not probed directly ----------------------

EXEMPT: dict[str, str] = {
    # Databases have no HTTP surface, and probing Postgres directly would prove less
    # than what already covers them: each owning service's /health now gates liveness
    # on CanConnect, so a dead database shows up as a 503 from the service that needs
    # it. Which is the failure anyone actually cares about.
    "user-db": "probed through user-service/health, which 503s when its database is gone",
    "classroom-db": "probed through classroom-service/health",
    "streaming-db": "probed through streaming-service/health",
    "rag-db": "probed through rag-service/health",
    "live-assistant-db": "probed through live-assistant-service/health",
    # RabbitMQ's management API needs credentials this suite has no business holding,
    # and every service that depends on the bus fails its own probe without it.
    "intellilect-mq": "no unauthenticated health surface; bus outages surface via the services that use it",
    "livekit-redis": "probed through livekit-server, which will not accept a room without it",
    # A worker, not a server. It has no port to answer on; its liveness is only
    # observable by asking it to record something, which is §8's recording path.
    "livekit-egress": "no HTTP surface — a worker; covered by the recording path (test-plan G-06)",
}

# LiveKit answers HTTP on its signalling port but has no documented health route; any
# response at all proves the process is accepting connections, which is all a smoke
# probe needs from it.
LIVEKIT_SERVICE = "livekit-server"


def covered() -> set[str]:
    return (
        {probe.service for probe in PROBES}
        | set(IN_NETWORK_ONLY)
        | set(INFRASTRUCTURE_PROBES)
        | set(EXEMPT)
        | {LIVEKIT_SERVICE}
    )


# --- the services that must expose /health, and where their wiring lives -------------

DOTNET_HEALTH_SOURCES: dict[str, Path] = {
    "user-service": BACKEND / "UserManagementService/src/UserManagementService.Api/Program.cs",
    "classroom-service": BACKEND / "ClassroomService/src/ClassroomService.Api/Program.cs",
    "streaming-service": BACKEND / "StreamingService/src/StreamingService.Api/Program.cs",
    "email-service": BACKEND / "EmailService/src/EmailService.Api/Program.cs",
}

PYTHON_HEALTH_SOURCES: dict[str, Path] = {
    "rag-service": BACKEND / "RagService/app/api/routers/health.py",
    "live-assistant-service": BACKEND / "LiveAssistantService/app/api/routers/health.py",
}

# The three whose /health must be able to fail. EmailService is exempt: it holds no
# database and no state — its liveness genuinely is "the process answers".
DATABASE_GATED: dict[str, Path] = {
    "user-service": BACKEND
    / "UserManagementService/src/UserManagementService.Infrastructure/Observability/DatabaseHealthCheck.cs",
    "classroom-service": BACKEND
    / "ClassroomService/src/ClassroomService.Infrastructure/Observability/DatabaseHealthCheck.cs",
    "streaming-service": BACKEND
    / "StreamingService/src/StreamingService.Infrastructure/Observability/DatabaseHealthCheck.cs",
}
