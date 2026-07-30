from abc import ABC, abstractmethod
from datetime import datetime
from uuid import UUID

from app.domain.entities.summary_run import SummaryRun
from app.domain.enums.summary_run_status import SummaryRunStatus


class SummaryRunRepository(ABC):
    """Persistence port for the SummaryRun aggregate — the unit of dedup and retry."""

    @abstractmethod
    async def claim(
        self, session_id: UUID, classroom_id: UUID | None, now: datetime
    ) -> SummaryRun | None:
        """Atomically claim this session's summary run, or return None if it is already taken.

        THE DEDUP PRIMITIVE. Returning None means "someone else owns this run" — the caller must
        then do nothing and acknowledge the message, NOT generate. Delivery is at-least-once and
        the service retries internally, so the same session genuinely does arrive more than once;
        without this a redelivery would pay for a second whole-lecture LLM call.

        Claimable states are PENDING (fresh or reset by the sweep) and a FAILED/DONE run whose
        ``next_attempt_at`` has come due. A RUNNING run is never claimable — that is what makes
        two concurrent deliveries collapse into one winner.

        Increments ``attempts`` and sets ``started_at`` on success.
        """
        raise NotImplementedError

    @abstractmethod
    async def get_by_session_id(self, session_id: UUID) -> SummaryRun | None:
        """Read a run without claiming it (status endpoints, tests, diagnostics)."""
        raise NotImplementedError

    @abstractmethod
    async def mark_done(self, session_id: UUID, now: datetime) -> None:
        """Terminal success. Clears the error and any scheduled retry."""
        raise NotImplementedError

    @abstractmethod
    async def mark_failed(
        self, session_id: UUID, error: str, now: datetime
    ) -> None:
        """Terminal failure: retries are exhausted or the cause is permanent.

        Leaves ``next_attempt_at`` NULL so the sweep will not pick it up — only an explicit
        regeneration request re-opens a run in this state.
        """
        raise NotImplementedError

    @abstractmethod
    async def schedule_retry(
        self, session_id: UUID, error: str, next_attempt_at: datetime, now: datetime
    ) -> None:
        """Transient failure with attempts left: back to PENDING, due at ``next_attempt_at``."""
        raise NotImplementedError

    @abstractmethod
    async def find_due(self, now: datetime, limit: int) -> list[SummaryRun]:
        """PENDING runs whose ``next_attempt_at`` has come due (or was never set)."""
        raise NotImplementedError

    @abstractmethod
    async def reset_stale_running(self, threshold: datetime, now: datetime) -> list[SummaryRun]:
        """Return RUNNING runs started before ``threshold`` to PENDING, keeping ``attempts``.

        Covers the process dying mid-generation: the claim survives in the database but nothing
        is working on it, so without this the run would sit RUNNING forever and, because RUNNING
        is not claimable, never be retried.
        """
        raise NotImplementedError

    @abstractmethod
    async def reopen(
        self, session_id: UUID, classroom_id: UUID | None, now: datetime
    ) -> SummaryRun:
        """Force a terminal run back to PENDING and reset ``attempts`` — manual regeneration.

        Resetting the counter is the point: a human asking again should restore the full retry
        budget, not inherit an exhausted one. Creates the row if it does not exist.
        """
        raise NotImplementedError

    @abstractmethod
    async def status_counts(self) -> dict[SummaryRunStatus, int]:
        """Run counts by status, for the metrics endpoint."""
        raise NotImplementedError
