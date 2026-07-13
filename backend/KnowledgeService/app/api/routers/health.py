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


async def _check_ollama() -> tuple[bool, list[str]]:
    """Probe host Ollama via `GET /api/tags`. Non-fatal — never raises.

    Returns (reachable, [model names present]) so the caller can also confirm the
    configured models are pulled.
    """
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
        if response.status_code != 200:
            return False, []
        models = [m.get("name", "") for m in response.json().get("models", [])]
        return True, [name for name in models if name]
    except Exception:
        return False, []


def _model_status(reachable: bool, models: list[str], target: str) -> str:
    """"available" / "missing" / "unknown" for a configured model name."""
    if not reachable:
        return "unknown"
    present = any(name == target or name.startswith(f"{target}:") for name in models)
    return "available" if present else "missing"


def _check_worker(request: Request) -> tuple[bool, int]:
    """Whether the ingestion worker is running, and its current queue depth."""
    worker = getattr(request.app.state, "ingestion_worker", None)
    if worker is None:
        return False, 0
    return worker.is_running(), worker.queue_depth()


@router.get("/health")
async def health(request: Request) -> JSONResponse:
    """Liveness + component readiness.

    Reports each component (db, ollama, generation model, worker) separately. The DB
    check is the liveness-critical one (503 if it fails); the others are non-fatal
    signals, but any unhealthy component makes the overall status "degraded".
    """
    settings = get_settings()
    db_ok = await _check_db()
    ollama_ok, models = await _check_ollama()
    generation_status = _model_status(ollama_ok, models, settings.generation_model)
    worker_ok, queue_depth = _check_worker(request)

    status_code = 200 if db_ok else 503
    overall_ok = db_ok and ollama_ok and worker_ok and generation_status == "available"
    return JSONResponse(
        status_code=status_code,
        content={
            "status": "ok" if overall_ok else "degraded",
            "db": "ok" if db_ok else "fail",
            "ollama": "reachable" if ollama_ok else "unreachable",
            "generationModel": generation_status,
            "worker": "running" if worker_ok else "down",
            "queueDepth": queue_depth,
        },
    )
