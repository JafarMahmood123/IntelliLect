"""Cover the claim/retry/terminal logic that turned summaries from one-shot into recoverable.

The behaviours that matter, and that would each silently regress:
  * a lost claim does NOTHING — that is dedup, and it is what stops a redelivery paying for a
    second whole-lecture LLM call
  * a transient failure with attempts left publishes NOTHING — telling ClassroomService "failed"
    mid-retry would flip the classroom to Failed and back again
  * a permanent failure does not consume the attempt budget
  * backoff is exponential and capped
"""

from __future__ import annotations

from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4

import pytest

from app.application.dtos.summary_messages import SessionSummaryReadyMessage
from app.application.services.summary_errors import (
    PermanentSummaryError,
    TransientSummaryError,
)
from app.application.services.summary_pipeline import SummaryExecution
from app.application.services.summary_run_service import SummaryRunService
from app.domain.entities.summary_run import SummaryRun
from app.domain.enums.summary_run_status import SummaryRunStatus

_NOW = datetime(2026, 7, 30, 12, 0, tzinfo=timezone.utc)


class FakeClock:
    def __init__(self, now: datetime = _NOW) -> None:
        self._now = now

    def now(self) -> datetime:
        return self._now

    async def sleep(self, seconds: float) -> None:  # pragma: no cover - unused here
        return None


class FakeRunRepository:
    """In-memory stand-in that records the transitions the service drives."""

    def __init__(self, *, claimable: bool = True, attempts: int = 1) -> None:
        self.claimable = claimable
        self.run = SummaryRun(
            session_id=uuid4(), status=SummaryRunStatus.RUNNING, attempts=attempts
        )
        self.done_calls: list[UUID] = []
        self.failed_calls: list[tuple[UUID, str]] = []
        self.retry_calls: list[tuple[UUID, datetime]] = []
        self.reopened: list[UUID] = []

    async def claim(self, session_id, classroom_id, now):
        if not self.claimable:
            return None
        self.run.session_id = session_id
        self.run.classroom_id = classroom_id
        return self.run

    async def get_by_session_id(self, session_id):
        return self.run

    async def mark_done(self, session_id, now):
        self.done_calls.append(session_id)

    async def mark_failed(self, session_id, error, now):
        self.failed_calls.append((session_id, error))

    async def schedule_retry(self, session_id, error, next_attempt_at, now):
        self.retry_calls.append((session_id, next_attempt_at))

    async def find_due(self, now, limit):
        return []

    async def reset_stale_running(self, threshold, now):
        return []

    async def reopen(self, session_id, classroom_id, now):
        self.reopened.append(session_id)
        return self.run

    async def status_counts(self):
        return {}


class FakePipeline:
    """Returns a preset execution result and records what was published."""

    def __init__(self, error: Exception | None = None) -> None:
        self._error = error
        self.published_success: list[SessionSummaryReadyMessage] = []
        self.published_failure: list[str] = []
        self.execute_calls = 0

    async def execute(self, session_id, classroom_id=None):
        self.execute_calls += 1
        message = (
            SessionSummaryReadyMessage.failure(session_id, classroom_id, str(self._error))
            if self._error
            else SessionSummaryReadyMessage.success(
                session_id, classroom_id or uuid4(), "md", "pdf", _NOW
            )
        )
        return SummaryExecution(
            message=message, classroom_id=classroom_id, error=self._error
        )

    async def publish_success(self, message):
        self.published_success.append(message)

    async def publish_failure(self, session_id, classroom_id, error):
        self.published_failure.append(error)
        return SessionSummaryReadyMessage.failure(session_id, classroom_id, error)


def _service(runs, pipeline, *, max_attempts: int = 3) -> SummaryRunService:
    return SummaryRunService(
        runs,
        pipeline,
        FakeClock(),
        max_attempts=max_attempts,
        retry_base_seconds=30.0,
        retry_max_seconds=300.0,
        stale_minutes=15,
    )


# --- dedup ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_a_lost_claim_does_no_work() -> None:
    """THE dedup guarantee: a redelivery must not start a second generation."""
    runs = FakeRunRepository(claimable=False)
    pipeline = FakePipeline()

    result = await _service(runs, pipeline).request(uuid4(), uuid4())

    assert result.claimed is False
    assert pipeline.execute_calls == 0, "a lost claim still generated — dedup is broken"
    assert pipeline.published_success == []
    assert pipeline.published_failure == []


# --- success -------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_success_marks_done_and_publishes() -> None:
    runs = FakeRunRepository()
    pipeline = FakePipeline()

    result = await _service(runs, pipeline).request(uuid4(), uuid4())

    assert result.succeeded is True
    assert len(runs.done_calls) == 1
    assert len(pipeline.published_success) == 1
    assert pipeline.published_failure == []


# --- transient failure ---------------------------------------------------------------


@pytest.mark.asyncio
async def test_transient_failure_schedules_a_retry_and_publishes_nothing() -> None:
    """Publishing a failure mid-retry would flip the classroom Failed then Available again."""
    runs = FakeRunRepository(attempts=1)
    pipeline = FakePipeline(TransientSummaryError("rate limited"))

    result = await _service(runs, pipeline).request(uuid4(), uuid4())

    assert result.retry_scheduled is True
    assert len(runs.retry_calls) == 1
    assert runs.failed_calls == []
    assert pipeline.published_failure == [], "a retryable failure must stay silent"


@pytest.mark.asyncio
async def test_backoff_is_exponential_and_capped() -> None:
    for attempts, expected_delay in ((1, 30.0), (2, 60.0), (3, 120.0), (10, 300.0)):
        runs = FakeRunRepository(attempts=attempts)
        pipeline = FakePipeline(TransientSummaryError("boom"))
        # max_attempts high enough that every case retries rather than terminating.
        await _service(runs, pipeline, max_attempts=99).request(uuid4(), None)

        _, next_attempt_at = runs.retry_calls[0]
        assert next_attempt_at == _NOW + timedelta(seconds=expected_delay), attempts


@pytest.mark.asyncio
async def test_exhausted_attempts_become_terminal_and_publish() -> None:
    runs = FakeRunRepository(attempts=3)
    pipeline = FakePipeline(TransientSummaryError("still failing"))

    result = await _service(runs, pipeline, max_attempts=3).request(uuid4(), uuid4())

    assert result.retry_scheduled is False
    assert runs.retry_calls == []
    assert len(runs.failed_calls) == 1
    assert len(pipeline.published_failure) == 1, "the classroom must be told once it is final"


# --- permanent failure ---------------------------------------------------------------


@pytest.mark.asyncio
async def test_permanent_failure_does_not_consume_the_retry_budget() -> None:
    """Attempt 1 of 3, but retrying a missing transcript can never help."""
    runs = FakeRunRepository(attempts=1)
    pipeline = FakePipeline(PermanentSummaryError("No transcript for session (404)."))

    result = await _service(runs, pipeline).request(uuid4(), uuid4())

    assert result.retry_scheduled is False
    assert runs.retry_calls == []
    assert len(runs.failed_calls) == 1
    assert len(pipeline.published_failure) == 1


# --- manual reopen -------------------------------------------------------------------


@pytest.mark.asyncio
async def test_reopen_restores_the_attempt_budget() -> None:
    """A human asking again must not inherit an exhausted counter."""
    runs = FakeRunRepository(attempts=3)
    session_id = uuid4()

    await _service(runs, FakePipeline()).reopen(session_id, uuid4())

    assert runs.reopened == [session_id]
