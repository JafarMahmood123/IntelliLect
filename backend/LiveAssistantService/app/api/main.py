from __future__ import annotations

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI

from app.api.dependencies import build_session_manager
from app.api.routers import health, internal_sessions
from app.infrastructure.config.settings import get_settings

logger = logging.getLogger("liveassistant.api")


def _configure_logging(level: str) -> None:
    logging.basicConfig(
        level=getattr(logging, level.upper(), logging.INFO),
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
    )


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Startup: build the session registry and expose it for the internal endpoints.
    app.state.session_manager = build_session_manager(get_settings())
    try:
        yield
    finally:
        # Shutdown: stop every active agent pipeline gracefully.
        try:
            await app.state.session_manager.stop_all()
        except Exception:  # noqa: BLE001 — shutdown must not raise
            logger.exception("Error stopping active sessions on shutdown.")


def create_app() -> FastAPI:
    """FastAPI application factory."""
    settings = get_settings()
    _configure_logging(settings.log_level)

    app = FastAPI(
        title="IntelliLect LiveAssistantService",
        version="0.1.0",
        description=(
            "Server-side live-session assistant. Joins a session as an agent, "
            "captures the teacher's audio, transcribes it, detects idea boundaries, "
            "checks each idea against the classroom's material (KnowledgeService RAG), "
            "and privately suggests corrections to the teacher. Sessions are started "
            "and stopped via internal endpoints triggered by the streaming service."
        ),
        lifespan=lifespan,
    )

    app.include_router(health.router)
    app.include_router(internal_sessions.router)
    return app


app = create_app()
