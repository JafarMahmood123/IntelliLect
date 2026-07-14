from __future__ import annotations

from fastapi import APIRouter

from app.infrastructure.config.settings import get_settings

router = APIRouter(tags=["health"])


@router.get("/health")
async def health() -> dict[str, str]:
    """Liveness + configuration readiness.

    Always 200 with ``status: ok`` — this phase captures audio behind an
    ``AudioSource`` and does NOT require a live LiveKit connection to be healthy.
    ``livekit`` reports whether the join credentials (URL/API key/secret) are
    present, so operators can see at a glance whether real capture is possible or the
    service will fall back to offline/Fake sources.
    """
    settings = get_settings()
    return {
        "status": "ok",
        "livekit": "configured" if settings.livekit_configured else "not-configured",
    }
