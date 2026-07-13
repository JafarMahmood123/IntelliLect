from __future__ import annotations

import httpx
from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse
from sqlalchemy import text

from app.infrastructure.config.settings import get_settings
from app.infrastructure.persistence.database import get_session_factory

router = APIRouter(tags=["health"])

# Ollama reachability is a soft signal; keep its probe snappy so /health stays fast.
_OLLAMA_PROBE_TIMEOUT_SECONDS = 5.0


async def _check_db() -> bool:
    """Verify a `SELECT 1` against PostgreSQL."""
    try:
        factory = get_session_factory()
        async with factory() as session:
            await session.execute(text("SELECT 1"))
        return True
    except Exception:
        return False


async def _check_ollama() -> bool:
    """Probe host Ollama via `GET /api/tags`. Non-fatal — never raises."""
    settings = get_settings()
    headers: dict[str, str] = {}
    if settings.ollama_auth_token:
        headers["Authorization"] = f"Bearer {settings.ollama_auth_token}"
    url = f"{settings.ollama_base_url.rstrip('/')}/api/tags"
    try:
        async with httpx.AsyncClient(
            timeout=_OLLAMA_PROBE_TIMEOUT_SECONDS, headers=headers
        ) as client:
            response = await client.get(url)
        return response.status_code == 200
    except Exception:
        return False


def _check_worker(request: Request) -> tuple[bool, int]:
    """Whether the ingestion worker is running, and its current queue depth."""
    worker = getattr(request.app.state, "ingestion_worker", None)
    if worker is None:
        return False, 0
    return worker.is_running(), worker.queue_depth()


@router.get("/health")
async def health(request: Request) -> JSONResponse:
    """Liveness + component readiness.

    Reports each component (db, ollama, worker) separately. The DB check is the
    liveness-critical one (503 if it fails); ollama and worker are non-fatal
    signals, but any unhealthy component makes the overall status "degraded".
    """
    db_ok = await _check_db()
    ollama_ok = await _check_ollama()
    worker_ok, queue_depth = _check_worker(request)

    status_code = 200 if db_ok else 503
    overall_ok = db_ok and ollama_ok and worker_ok
    return JSONResponse(
        status_code=status_code,
        content={
            "status": "ok" if overall_ok else "degraded",
            "db": "ok" if db_ok else "fail",
            "ollama": "reachable" if ollama_ok else "unreachable",
            "worker": "running" if worker_ok else "down",
            "queueDepth": queue_depth,
        },
    )
