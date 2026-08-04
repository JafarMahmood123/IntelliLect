"""Background loop that re-runs summaries whose retry has come due.

The counterpart to ``SummaryRunService.schedule_retry``. A failed-but-retryable run is not held in
memory waiting for its backoff — that would lose it on restart, which is precisely the failure this
work exists to remove. It is written to ``summary_runs`` with a ``next_attempt_at``, and this loop
picks it up whenever it happens to be running.

It also resets stale RUNNING rows. A process killed mid-generation leaves a claim in the database
with nobody working on it, and because RUNNING is deliberately not claimable, nothing would ever
retry it. The sweep returns those rows to PENDING so the same loop then re-runs them.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
from collections.abc import Awaitable, Callable

logger = logging.getLogger("knowledge.summary")

# Runs one sweep pass and returns how many runs were started.
SweepFn = Callable[[], Awaitable[int]]


class SummaryRetrySweeper:
    """Owns the single polling task that drives due retries and stale recovery."""

    def __init__(self, sweep: SweepFn, poll_seconds: float) -> None:
        self._sweep = sweep
        self._poll_seconds = poll_seconds
        self._task: asyncio.Task[None] | None = None
        self.last_error: str | None = None
        self.started_total = 0

    def is_running(self) -> bool:
        return self._task is not None and not self._task.done()

    def start(self) -> bool:
        if self.is_running():
            return False
        self._task = asyncio.create_task(self._run(), name="summary-retry-sweeper")
        return True

    async def _run(self) -> None:
        while True:
            try:
                started = await self._sweep()
                self.started_total += started
                self.last_error = None
            except asyncio.CancelledError:
                raise
            except Exception as exc:  # noqa: BLE001 — the sweeper must outlive any single failure
                self.last_error = f"{type(exc).__name__}: {exc}"
                logger.warning(
                    "summary_sweep_failed", extra={"error_type": type(exc).__name__}
                )
            # Always sleep a full interval, even after a productive pass: unlike the outbox relay,
            # each item here is a minutes-long LLM job, so there is no backlog to drain quickly and
            # tight looping would only pile concurrent generations onto the same backend.
            await asyncio.sleep(self._poll_seconds)

    async def stop(self) -> None:
        if self._task is not None:
            self._task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._task
            self._task = None
