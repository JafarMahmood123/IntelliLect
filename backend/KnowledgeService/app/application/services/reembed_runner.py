"""Single background runner for the re-embed sweep.

Deliberately NOT a queue like ``IngestionWorker``: a re-embed is one long whole-corpus sweep, not
many independent jobs, and running two at once would have both racing for the same NULL rows —
double-embedding chunks and paying twice for it. So exactly one run is allowed at a time and a
second ``start`` is refused rather than queued.

Progress is reported straight from the database (``count_missing_embeddings``) rather than from an
in-memory counter, so the status endpoint stays truthful even across a restart mid-run.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
from collections.abc import Awaitable, Callable

from app.application.services.reembed_service import ReembedProgress

logger = logging.getLogger("knowledge.reembed")

# Runs the whole sweep, reporting into the supplied progress object.
SweepFn = Callable[[ReembedProgress], Awaitable[None]]


class ReembedRunner:
    """Owns the single in-flight reindex task and its progress snapshot."""

    def __init__(self, sweep: SweepFn) -> None:
        self._sweep = sweep
        self._task: asyncio.Task[None] | None = None
        self._progress = ReembedProgress()

    def is_running(self) -> bool:
        return self._task is not None and not self._task.done()

    def progress(self) -> ReembedProgress:
        return self._progress

    def start(self) -> bool:
        """Launch a sweep. Returns False if one is already running (never queues a second)."""
        if self.is_running():
            return False
        self._progress = ReembedProgress(state="running")
        self._task = asyncio.create_task(self._run(), name="reembed-sweep")
        return True

    async def _run(self) -> None:
        try:
            await self._sweep(self._progress)
        except asyncio.CancelledError:
            self._progress.state = "failed"
            self._progress.error = "cancelled"
            raise
        except Exception as exc:  # noqa: BLE001 — surfaced via the status endpoint
            self._progress.state = "failed"
            # The message is operator-facing (wrong dimension, bad key, rate limit), so unlike
            # the live pipeline this keeps the text rather than only the exception type.
            self._progress.error = f"{type(exc).__name__}: {exc}"
            logger.exception("Reindex sweep failed")
        else:
            if self._progress.state == "running":
                self._progress.state = "completed"

    async def stop(self) -> None:
        """Cancel an in-flight sweep (called on shutdown). Progress already written survives."""
        if self._task is not None:
            self._task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._task
            self._task = None
