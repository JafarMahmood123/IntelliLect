from __future__ import annotations

import logging

from fastapi import FastAPI

from app.api.routers import health
from app.infrastructure.config.settings import get_settings


def _configure_logging(level: str) -> None:
    logging.basicConfig(
        level=getattr(logging, level.upper(), logging.INFO),
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
    )


def create_app() -> FastAPI:
    """FastAPI application factory."""
    settings = get_settings()
    _configure_logging(settings.log_level)

    app = FastAPI(
        title="IntelliLect LiveAssistantService",
        version="0.1.0",
        description=(
            "Server-side live-session assistant. This build is the foundation only: "
            "the service skeleton plus a LiveKit agent that captures the teacher's "
            "audio behind an AudioSource port. STT, boundary detection, retrieval, "
            "brain evaluation, and feedback delivery are later phases."
        ),
    )

    app.include_router(health.router)
    return app


app = create_app()
