from __future__ import annotations

import asyncio
import logging
from collections.abc import Awaitable, Callable
from uuid import UUID

logger = logging.getLogger("knowledge.summary")

# Builds + runs the summary pipeline for one session. Injected by the composition root
# so it can open a fresh DB session per run (like the ingestion worker's handle).
SummaryHandle = Callable[[UUID, UUID | None], Awaitable[None]]


class SummaryRunner:
    """Fire-and-forget background runner for the summary pipeline (S-3).

    The session-end trigger calls ``enqueue`` and returns immediately (202); the actual
    generate -> render -> upload -> publish work runs on a background task, so session
    end is never blocked. Non-fatal: a failing run is swallowed here (the pipeline
    already publishes a failure message). ``enqueue`` de-duplicates concurrent runs for
    the same session, so a double trigger does not start two pipelines at once.
    """

    def __init__(self, handle: SummaryHandle) -> None:
        self._handle = handle
        self._inflight: set[UUID] = set()
        self._tasks: set[asyncio.Task] = set()

    def enqueue(self, session_id: UUID, classroom_id: UUID | None) -> bool:
        """Start a background summary run. Returns False if one is already in flight."""
        if session_id in self._inflight:
            logger.info("Summary already in progress for session %s; skipping.", session_id)
            return False
        self._inflight.add(session_id)
        task = asyncio.create_task(self._run(session_id, classroom_id))
        self._tasks.add(task)
        task.add_done_callback(self._tasks.discard)
        return True

    async def _run(self, session_id: UUID, classroom_id: UUID | None) -> None:
        try:
            await self._handle(session_id, classroom_id)
        except Exception:  # noqa: BLE001 — background work must never crash the loop
            logger.exception("Unexpected error running the summary for session %s.", session_id)
        finally:
            self._inflight.discard(session_id)

    async def drain(self) -> None:
        """Await any in-flight summary tasks (used on app shutdown / in tests)."""
        while self._tasks:
            await asyncio.gather(*list(self._tasks), return_exceptions=True)
