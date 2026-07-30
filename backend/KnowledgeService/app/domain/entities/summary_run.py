from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from uuid import UUID, uuid4

from app.domain.enums.summary_run_status import SummaryRunStatus


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


@dataclass
class SummaryRun:
    """One session's summary generation, tracked so it can be deduplicated and retried.

    Pure domain object; the persistence layer maps it to/from SQLAlchemy. Modelled on
    ``Document`` because summaries now reuse ingestion's machinery — the same claim, the same
    attempt counting, the same stale sweep.

    ``session_id`` is the identity that matters. ``id`` is a surrogate key; the claim, the dedup
    and every lookup go through ``session_id``, which is UNIQUE in the schema.
    """

    session_id: UUID
    # Nullable because a manual request may not carry it and the pipeline learns it from the
    # transcript. It is needed for the S3 key template, so a run that never learns it cannot
    # produce artifacts.
    classroom_id: UUID | None = None
    status: SummaryRunStatus = SummaryRunStatus.PENDING
    attempts: int = 0  # incremented on each claim, so it counts starts rather than failures
    last_error: str | None = None
    next_attempt_at: datetime | None = None  # when the retry sweep may claim it again
    started_at: datetime | None = None  # set on claim; how the stale sweep spots a dead run
    completed_at: datetime | None = None
    id: UUID = field(default_factory=uuid4)
    created_at_utc: datetime = field(default_factory=_utcnow)
    updated_at_utc: datetime = field(default_factory=_utcnow)

    @property
    def is_terminal(self) -> bool:
        return self.status in (SummaryRunStatus.DONE, SummaryRunStatus.FAILED)
