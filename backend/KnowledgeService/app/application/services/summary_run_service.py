"""Claim, run, and retry one session's summary.

The summary equivalent of ``IngestionService``, and deliberately the same shape: an atomic claim
that doubles as dedup, an attempt counter, transient-vs-permanent classification, and exponential
backoff. Before this existed a summary got exactly one attempt with no record of it — a 429 or a
slow transcript meant Failed forever, and a crash mid-run meant nothing at all.

WHAT PUBLISHING MEANS HERE. A failure message is published only when the run is TERMINAL. Telling
ClassroomService "failed" on attempt 1 of 3 would flip the classroom to Failed and then back to
Available when a later attempt succeeded, which reads as a bug. So a retryable failure updates
only this service's own state and stays silent.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from datetime import timedelta
from uuid import UUID

from app.application.ports.summary_run_repository import SummaryRunRepository
from app.application.services.clock import Clock
from app.application.services.summary_errors import is_transient
from app.application.services.summary_pipeline import SummaryPipeline

logger = logging.getLogger("knowledge.summary")


@dataclass(frozen=True)
class SummaryRunResult:
    """Outcome of one attempt, for logging and tests."""

    session_id: UUID
    claimed: bool  # False = another delivery owns this run (dedup); nothing was done
    succeeded: bool = False
    retry_scheduled: bool = False
    attempts: int = 0
    error: str | None = None


class SummaryRunService:
    """Owns the lifecycle of a summary run: claim, execute, then done / retry / fail."""

    def __init__(
        self,
        runs: SummaryRunRepository,
        pipeline: SummaryPipeline,
        clock: Clock,
        *,
        max_attempts: int,
        retry_base_seconds: float,
        retry_max_seconds: float,
        stale_minutes: int,
    ) -> None:
        self._runs = runs
        self._pipeline = pipeline
        self._clock = clock
        self._max_attempts = max_attempts
        self._retry_base = retry_base_seconds
        self._retry_max = retry_max_seconds
        self._stale_minutes = stale_minutes

    async def claim(self, session_id: UUID, classroom_id: UUID | None) -> bool:
        """Try to take ownership of this session's run. False = someone else has it.

        Separated from ``execute`` so the AMQP consumer can ack as soon as the claim is DURABLE,
        without holding the message open for the minutes-long generation that follows.
        """
        run = await self._runs.claim(session_id, classroom_id, self._clock.now())
        if run is None:
            logger.info(
                "summary_claim_lost",
                extra={"session_id": str(session_id)},
            )
        return run is not None

    async def execute_claimed(
        self, session_id: UUID, classroom_id: UUID | None
    ) -> SummaryRunResult:
        """Run the pipeline for a run this caller has already claimed."""
        run = await self._runs.get_by_session_id(session_id)
        attempts = run.attempts if run else 1
        # A retry may know the classroom from an earlier attempt even when this request does not.
        effective_classroom = classroom_id or (run.classroom_id if run else None)

        outcome = await self._pipeline.execute(session_id, effective_classroom)
        now = self._clock.now()

        if outcome.error is None:
            # Order matters: mark the run Done FIRST, then publish. With the outbox both land in
            # the same transaction, so a crash between them is impossible — but if the publisher
            # is ever swapped back to a direct one, this order fails safe (a published success
            # with a lost Done is worse than a Done whose message the relay will retry).
            await self._runs.mark_done(session_id, now)
            await self._pipeline.publish_success(outcome.message)
            return SummaryRunResult(
                session_id=session_id, claimed=True, succeeded=True, attempts=attempts
            )

        error_text = f"{type(outcome.error).__name__}: {outcome.error}"
        transient = is_transient(outcome.error)

        if transient and attempts < self._max_attempts:
            delay = self._backoff(attempts)
            await self._runs.schedule_retry(
                session_id, error_text, now + timedelta(seconds=delay), now
            )
            # Log the TYPE only; the full text goes to summary_runs.last_error, matching the
            # privacy stance in the ingestion path.
            logger.warning(
                "summary_retry_scheduled",
                extra={
                    "session_id": str(session_id),
                    "attempt": attempts,
                    "max_attempts": self._max_attempts,
                    "delay_seconds": round(delay, 1),
                    "error_type": type(outcome.error).__name__,
                },
            )
            return SummaryRunResult(
                session_id=session_id,
                claimed=True,
                retry_scheduled=True,
                attempts=attempts,
                error=error_text,
            )

        # Terminal: permanent cause, or the attempt budget is spent. Only now does the classroom
        # get told, because only now is the answer final.
        await self._runs.mark_failed(session_id, error_text, now)
        await self._pipeline.publish_failure(
            session_id, outcome.classroom_id, error_text
        )
        logger.error(
            "summary_failed",
            extra={
                "session_id": str(session_id),
                "attempts": attempts,
                "reason": "permanent" if not transient else "attempts_exhausted",
                "error_type": type(outcome.error).__name__,
            },
        )
        return SummaryRunResult(
            session_id=session_id, claimed=True, attempts=attempts, error=error_text
        )

    async def request(
        self, session_id: UUID, classroom_id: UUID | None
    ) -> SummaryRunResult:
        """Claim and run in one call — the convenience path for the HTTP endpoint and tests."""
        if not await self.claim(session_id, classroom_id):
            return SummaryRunResult(session_id=session_id, claimed=False)
        return await self.execute_claimed(session_id, classroom_id)

    async def reopen(self, session_id: UUID, classroom_id: UUID | None) -> None:
        """Manual regeneration: force the run back to Pending with a fresh attempt budget."""
        await self._runs.reopen(session_id, classroom_id, self._clock.now())
        logger.info("summary_reopened", extra={"session_id": str(session_id)})

    async def find_due(self, limit: int) -> list[tuple[UUID, UUID | None]]:
        """Runs whose scheduled retry has come due, as (session_id, classroom_id) pairs."""
        due = await self._runs.find_due(self._clock.now(), limit)
        return [(run.session_id, run.classroom_id) for run in due]

    async def sweep_stale(self) -> list[UUID]:
        """Return Running runs whose process died back to Pending so retry can pick them up."""
        now = self._clock.now()
        threshold = now - timedelta(minutes=self._stale_minutes)
        reset = await self._runs.reset_stale_running(threshold, now)
        if reset:
            logger.warning(
                "summary_stale_reset", extra={"count": len(reset)}
            )
        return [run.session_id for run in reset]

    def _backoff(self, attempts: int) -> float:
        return min(self._retry_base * (2 ** (attempts - 1)), self._retry_max)
