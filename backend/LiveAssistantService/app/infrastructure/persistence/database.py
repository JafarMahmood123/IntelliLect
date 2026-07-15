"""Async engine / session factory for the transcript store (S-0).

Only built when ``TRANSCRIPT_DB_URL`` is configured — the service still runs fully
offline (with the in-memory transcript store) when it is empty, matching the rest of
this service's optional-dependency style. The engine is process-wide and lazy.
"""

from __future__ import annotations

from sqlalchemy.ext.asyncio import (
    AsyncEngine,
    AsyncSession,
    async_sessionmaker,
    create_async_engine,
)

from app.infrastructure.config.settings import get_settings

_engine: AsyncEngine | None = None
_session_factory: async_sessionmaker[AsyncSession] | None = None


def get_engine() -> AsyncEngine:
    """Lazily create the process-wide async engine. Requires TRANSCRIPT_DB_URL."""
    global _engine
    if _engine is None:
        url = get_settings().transcript_db_url
        if not url:
            raise RuntimeError(
                "TRANSCRIPT_DB_URL is not configured; the SQL transcript store is unavailable."
            )
        _engine = create_async_engine(url, pool_pre_ping=True, future=True)
    return _engine


def get_session_factory() -> async_sessionmaker[AsyncSession]:
    global _session_factory
    if _session_factory is None:
        _session_factory = async_sessionmaker(
            bind=get_engine(),
            expire_on_commit=False,
            class_=AsyncSession,
        )
    return _session_factory


async def dispose_engine() -> None:
    """Dispose the engine on shutdown (no-op if it was never created)."""
    global _engine, _session_factory
    if _engine is not None:
        await _engine.dispose()
        _engine = None
        _session_factory = None
