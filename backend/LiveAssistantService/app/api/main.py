from __future__ import annotations

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI

from app.api.dependencies import (
    build_feedback_recorder,
    build_last_idea_store,
    build_session_manager,
    build_transcript_repository,
)
from app.api.routers import (
    health,
    internal_quizzes,
    internal_sessions,
    internal_transcripts,
)
from app.api.routers import metrics as metrics_router
from app.infrastructure.config.settings import get_settings
from app.infrastructure.persistence.database import dispose_engine
from app.observability import metrics
from app.observability.logging_config import configure_logging

logger = logging.getLogger("liveassistant.api")


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Startup: build the shared transcript store (S-0) and the session registry, and
    # expose both for the internal endpoints. The SAME store the pipelines write
    # through backs the transcript endpoint, so reads see exactly what was persisted.
    settings = get_settings()
    app.state.transcript_repository = build_transcript_repository(settings)
    # Shared feedback store (used when AGENT_AUDIO_SOURCE=fake); the read endpoint and
    # every pipeline share this one instance, so reads see exactly what was delivered.
    app.state.feedback_recorder = build_feedback_recorder()
    # Shared store of the ideas each teacher has finished, written by every pipeline and read by
    # the quiz endpoint — so "what was I just explaining?" is answered from what actually ran.
    app.state.last_idea_store = build_last_idea_store(settings)
    app.state.session_manager = build_session_manager(
        settings,
        app.state.transcript_repository,
        app.state.feedback_recorder,
        app.state.last_idea_store,
    )
    try:
        yield
    finally:
        # Shutdown: stop every active agent pipeline gracefully, then release the DB.
        try:
            await app.state.session_manager.stop_all()
        except Exception:  # noqa: BLE001 — shutdown must not raise
            logger.exception("Error stopping active sessions on shutdown.")
        try:
            await dispose_engine()
        except Exception:  # noqa: BLE001 — shutdown must not raise
            logger.exception("Error disposing the transcript DB engine on shutdown.")


def create_app() -> FastAPI:
    """FastAPI application factory."""
    settings = get_settings()
    configure_logging(settings.log_level)  # structured JSON logs + correlation ids
    metrics.set_enabled(settings.metrics_enabled)

    app = FastAPI(
        title="IntelliLect LiveAssistantService",
        version="0.1.0",
        description=(
            "Server-side live-session assistant. Joins a session as an agent, "
            "captures the teacher's audio, transcribes it, detects idea boundaries, "
            "checks each idea against the classroom's material (RagService RAG), "
            "and privately suggests corrections to the teacher. Sessions are started "
            "and stopped via internal endpoints triggered by the streaming service."
        ),
        lifespan=lifespan,
    )

    app.include_router(health.router)
    app.include_router(internal_sessions.router)
    app.include_router(internal_transcripts.router)
    app.include_router(internal_transcripts.classrooms_router)
    app.include_router(internal_quizzes.router)
    if settings.metrics_enabled:
        app.include_router(metrics_router.router)
    return app


app = create_app()
