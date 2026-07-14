from __future__ import annotations

from fastapi import APIRouter, Request

from app.infrastructure.config.settings import get_settings

router = APIRouter(tags=["health"])


@router.get("/health")
async def health(request: Request) -> dict:
    """Liveness + configuration readiness.

    Always 200 with ``status: ok`` — the service is healthy without a live LiveKit
    connection. ``livekit`` reports whether join credentials are present; ``activeSessions``
    is the number of agent pipelines currently running (0 before startup wiring runs).
    """
    settings = get_settings()
    manager = getattr(request.app.state, "session_manager", None)
    active_sessions = manager.active_count() if manager is not None else 0
    return {
        "status": "ok",
        "livekit": "configured" if settings.livekit_configured else "not-configured",
        "activeSessions": active_sessions,
    }
