from __future__ import annotations

from contextlib import asynccontextmanager

from fastapi import FastAPI

from app.api.routers import health, internal_documents
from app.infrastructure.persistence.database import dispose_engine


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Startup: engine/session factory are created lazily on first use.
    yield
    # Shutdown: release DB connections.
    await dispose_engine()


def create_app() -> FastAPI:
    """FastAPI application factory."""
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
    return app


app = create_app()
