from __future__ import annotations

from contextlib import asynccontextmanager

from fastapi import FastAPI

import logging

from app.api.dependencies import (
    build_ingestion_worker,
    build_reembed_runner,
    build_summary_runner,
    run_stale_recovery,
)
from app.api.routers import (
    answer,
    health,
    internal_documents,
    internal_knowledge,
    internal_reembed,
    internal_summaries,
    metrics as metrics_router,
    search,
)
from app.infrastructure.config.settings import get_settings
from app.observability import metrics
from app.observability.logging_config import configure_logging
from app.infrastructure.persistence.database import dispose_engine

logger = logging.getLogger("knowledge.api")


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Startup: engine/session factory are created lazily on first use. Launch the
    # bounded ingestion worker and expose it on app.state for the ingest endpoint.
    worker = build_ingestion_worker()
    await worker.start()
    app.state.ingestion_worker = worker
    # Re-embed runner: idle until an operator POSTs /api/internal/reembed. Distinct from the
    # per-document /reindex endpoints, which re-INGEST (S3 -> extract -> chunk -> embed).
    reembed_runner = build_reembed_runner()
    app.state.reembed_runner = reembed_runner

    # Background runner for the session-end summary trigger (S-3).
    app.state.summary_runner = build_summary_runner()

    # Recover documents left stuck in Processing by a previous crash/restart.
    if get_settings().stale_recovery_on_startup:
        try:
            await run_stale_recovery(worker)
        except Exception:  # noqa: BLE001 — recovery must never block startup
            logger.exception("Stale-processing recovery failed on startup.")

    try:
        yield
    finally:
        # Shutdown: stop workers cleanly, drain in-flight summaries, then release DB.
        await worker.stop()
        await reembed_runner.stop()
        try:
            await app.state.summary_runner.drain()
        except Exception:  # noqa: BLE001 — shutdown must not raise
            logger.exception("Error draining in-flight summaries on shutdown.")
        await dispose_engine()


def create_app() -> FastAPI:
    """FastAPI application factory."""
    settings = get_settings()
    configure_logging(settings.log_level)
    metrics.set_enabled(settings.metrics_enabled)

    app = FastAPI(
        title="IntelliLect KnowledgeService",
        version="0.1.0",
        description=(
            "Ingests classroom files, embeds them, and serves retrieval. "
            "This build contains the foundation only."
        ),
        lifespan=lifespan,
    )

    app.include_router(health.router)
    app.include_router(internal_documents.router)
    app.include_router(internal_knowledge.router)
    app.include_router(internal_reembed.router)
    app.include_router(internal_summaries.router)
    app.include_router(search.router)
    app.include_router(answer.router)
    if settings.metrics_enabled:
        app.include_router(metrics_router.router)
    return app


app = create_app()
