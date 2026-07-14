"""Registry + lifecycle for per-session agent pipelines (LA-6).

Exactly one ``SessionPipeline`` per active ``session_id``: ``start`` launches and
registers one (idempotent — a duplicate start returns the existing pipeline), ``stop``
cancels and deregisters (idempotent — stopping an unknown session is a safe no-op),
and a bounded ``max_concurrent`` caps active sessions. A pipeline that ends
unexpectedly (crash or natural stream end) is deregistered and logged.

Pure application logic: it depends only on a pipeline factory (injected by the
composition root) + domain, so no infrastructure/framework leaks in.
"""

from __future__ import annotations

import asyncio
import logging
from collections.abc import Callable
from uuid import UUID

from app.application.services.session_pipeline import SessionPipeline
from app.domain.entities.session_context import SessionContext

logger = logging.getLogger("liveassistant.sessions")

# Builds a fresh, fully-wired pipeline for a session (each session needs its own
# room connection + boundary buffer). The composition root supplies the real one.
SessionPipelineFactory = Callable[[SessionContext], SessionPipeline]


class SessionCapacityError(RuntimeError):
    """Raised when starting a session would exceed ``max_concurrent_sessions``."""


class SessionManager:
    def __init__(self, factory: SessionPipelineFactory, max_concurrent: int) -> None:
        self._factory = factory
        self._max_concurrent = max_concurrent
        self._pipelines: dict[UUID, SessionPipeline] = {}
        self._lock = asyncio.Lock()

    async def start(self, session: SessionContext) -> SessionPipeline:
        """Launch + register a pipeline for ``session``; return it (idempotent).

        Raises ``SessionCapacityError`` if the concurrency cap is reached. Returns
        fast — the heavy work (join room, capture) runs inside the pipeline task.
        """
        async with self._lock:
            existing = self._pipelines.get(session.session_id)
            if existing is not None:
                logger.info("Session %s already active; start is a no-op.", session.session_id)
                return existing

            if len(self._pipelines) >= self._max_concurrent:
                raise SessionCapacityError(
                    f"At capacity ({self._max_concurrent} concurrent sessions); "
                    f"cannot start {session.session_id}."
                )

            pipeline = self._factory(session)
            self._pipelines[session.session_id] = pipeline
            task = pipeline.start()
            task.add_done_callback(
                lambda _t, sid=session.session_id: self._on_pipeline_done(sid)
            )
            logger.info(
                "Started session %s (%d active).", session.session_id, len(self._pipelines)
            )
            return pipeline

    async def stop(self, session_id: UUID) -> bool:
        """Cancel + deregister the pipeline. Returns whether one was active."""
        async with self._lock:
            pipeline = self._pipelines.pop(session_id, None)
        if pipeline is None:
            logger.info("Stop for unknown/stopped session %s; no-op.", session_id)
            return False
        await pipeline.stop()
        logger.info("Stopped session %s (%d active).", session_id, len(self._pipelines))
        return True

    async def stop_all(self) -> None:
        """Stop every active pipeline (called on app shutdown)."""
        for session_id in self.active_session_ids():
            await self.stop(session_id)

    def active_session_ids(self) -> list[UUID]:
        return list(self._pipelines.keys())

    def active_count(self) -> int:
        return len(self._pipelines)

    def _on_pipeline_done(self, session_id: UUID) -> None:
        """Deregister a pipeline whose task ended on its own (crash/stream end).

        If it is still registered when its task finishes, it ended unexpectedly (a
        deliberate ``stop`` removes it first) — drop it and log. Keeping it simple:
        no auto-restart for now.
        """
        if self._pipelines.pop(session_id, None) is not None:
            logger.warning(
                "Session %s pipeline ended unexpectedly; deregistered (%d active).",
                session_id, len(self._pipelines),
            )
