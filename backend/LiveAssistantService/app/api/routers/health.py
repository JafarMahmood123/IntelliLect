from __future__ import annotations

import httpx
from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

from app.infrastructure.config.settings import Settings, get_settings
from app.observability import metrics

router = APIRouter(tags=["health"])

# Component probes are soft signals; keep them snappy so /health stays fast.
_PROBE_TIMEOUT_SECONDS = 3.0


async def _reachable(url: str) -> bool:
    """Best-effort GET; True on any HTTP response, False on transport error."""
    try:
        async with httpx.AsyncClient(timeout=_PROBE_TIMEOUT_SECONDS) as client:
            response = await client.get(url)
        return response.status_code < 500
    except Exception:
        return False


async def _check_rag_service(settings: Settings) -> str:
    """"reachable" / "unreachable" / "not-configured" for RagService."""
    base = settings.rag_base_url.rstrip("/")
    if not base:
        return "not-configured"
    return "reachable" if await _reachable(f"{base}/health") else "unreachable"


async def _check_ollama(settings: Settings) -> str:
    """"reachable" / "unreachable" / "not-configured" for host Ollama."""
    base = settings.ollama_base_url.rstrip("/")
    if not base:
        return "not-configured"
    return "reachable" if await _reachable(f"{base}/api/tags") else "unreachable"


@router.get("/health")
async def health(request: Request) -> JSONResponse:
    """Liveness + component/config readiness (LA-8).

    Always alive (200) — the service runs offline with fakes and its live deps are
    enhancements, not liveness-critical. Each component is probed non-fatally and
    labeled; the overall status is ``degraded`` if a CONFIGURED external component is
    unreachable, else ``ok``.
    """
    settings = get_settings()
    manager = getattr(request.app.state, "session_manager", None)
    active_sessions = manager.active_count() if manager is not None else 0

    knowledge = await _check_rag_service(settings)
    ollama = await _check_ollama(settings)

    degraded = knowledge == "unreachable" or ollama == "unreachable"
    return JSONResponse(
        content={
            "status": "degraded" if degraded else "ok",
            "livekit": "configured" if settings.livekit_configured else "not-configured",
            "knowledgeService": knowledge,
            "ollama": ollama,
            "stt": {"model": settings.stt_model, "status": "configured"},
            "activeSessions": active_sessions,
            "metrics": "enabled" if metrics.is_enabled() else "disabled",
        }
    )
