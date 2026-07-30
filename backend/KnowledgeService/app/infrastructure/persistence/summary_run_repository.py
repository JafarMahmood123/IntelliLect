from __future__ import annotations

from datetime import datetime
from uuid import UUID, uuid4

from sqlalchemy import func, or_, select, update
from sqlalchemy.dialects.postgresql import insert as pg_insert
from sqlalchemy.ext.asyncio import AsyncSession

from app.application.ports.summary_run_repository import SummaryRunRepository
from app.domain.entities.summary_run import SummaryRun
from app.domain.enums.summary_run_status import SummaryRunStatus
from app.infrastructure.persistence.models import SummaryRunModel


class SqlAlchemySummaryRunRepository(SummaryRunRepository):
    """SummaryRunRepository backed by PostgreSQL via async SQLAlchemy.

    Mapping and queries only. The interesting part is ``claim``, which mirrors
    ``SqlAlchemyDocumentRepository.claim_for_processing`` — the same upsert-with-WHERE-guard
    idiom, because the problem is the same: turn concurrent requests for one unit of expensive
    work into exactly one winner, using the database as the arbiter rather than a lock.
    """

    def __init__(self, session: AsyncSession) -> None:
        self._session = session

    async def claim(
        self, session_id: UUID, classroom_id: UUID | None, now: datetime
    ) -> SummaryRun | None:
        # Atomic claim-or-create: INSERT as Running, or (on conflict) transition an existing row
        # to Running. The WHERE guard decides what is claimable:
        #   - PENDING            fresh, or reset by the stale sweep
        #   - due next_attempt_at a scheduled retry whose time has come
        # A RUNNING row matches neither, so a second delivery of the same session updates nothing,
        # returns no row, and the caller acks without generating. That is the dedup.
        #
        # The unique index on session_id also serializes two concurrent INSERTs of a session that
        # has no row yet, so the race is safe even on the very first request.
        claimable = or_(
            SummaryRunModel.status == SummaryRunStatus.PENDING.value,
            SummaryRunModel.next_attempt_at.is_not(None)
            & (SummaryRunModel.next_attempt_at <= now),
        )
        stmt = (
            pg_insert(SummaryRunModel)
            .values(
                id=uuid4(),
                session_id=session_id,
                classroom_id=classroom_id,
                status=SummaryRunStatus.RUNNING.value,
                attempts=1,
                last_error=None,
                next_attempt_at=None,
                started_at=now,
                completed_at=None,
            )
            .on_conflict_do_update(
                index_elements=[SummaryRunModel.session_id],
                set_={
                    "status": SummaryRunStatus.RUNNING.value,
                    "attempts": SummaryRunModel.attempts + 1,
                    "last_error": None,
                    "next_attempt_at": None,
                    "started_at": now,
                    "completed_at": None,
                    # COALESCE so a retry never erases a classroom_id learned on an earlier
                    # attempt just because this request did not carry one.
                    "classroom_id": func.coalesce(
                        pg_insert(SummaryRunModel).excluded.classroom_id,
                        SummaryRunModel.classroom_id,
                    ),
                    "updated_at_utc": now,
                },
                where=claimable,
            )
            .returning(SummaryRunModel.id)
        )
        claimed_id = (await self._session.execute(stmt)).scalar_one_or_none()
        if claimed_id is None:
            await self._session.flush()
            return None
        model = (
            await self._session.execute(
                select(SummaryRunModel).where(SummaryRunModel.id == claimed_id)
            )
        ).scalar_one()
        await self._session.flush()
        return self._to_entity(model)

    async def get_by_session_id(self, session_id: UUID) -> SummaryRun | None:
        model = (
            await self._session.execute(
                select(SummaryRunModel).where(SummaryRunModel.session_id == session_id)
            )
        ).scalar_one_or_none()
        return self._to_entity(model) if model is not None else None

    async def mark_done(self, session_id: UUID, now: datetime) -> None:
        await self._set(
            session_id,
            status=SummaryRunStatus.DONE.value,
            last_error=None,
            next_attempt_at=None,
            completed_at=now,
            now=now,
        )

    async def mark_failed(self, session_id: UUID, error: str, now: datetime) -> None:
        await self._set(
            session_id,
            status=SummaryRunStatus.FAILED.value,
            last_error=error[:2000],
            # NULL: the sweep must not pick this up. Terminal until a human asks again.
            next_attempt_at=None,
            completed_at=now,
            now=now,
        )

    async def schedule_retry(
        self, session_id: UUID, error: str, next_attempt_at: datetime, now: datetime
    ) -> None:
        await self._set(
            session_id,
            status=SummaryRunStatus.PENDING.value,
            last_error=error[:2000],
            next_attempt_at=next_attempt_at,
            completed_at=None,
            now=now,
        )

    async def find_due(self, now: datetime, limit: int) -> list[SummaryRun]:
        stmt = (
            select(SummaryRunModel)
            .where(
                SummaryRunModel.status == SummaryRunStatus.PENDING.value,
                or_(
                    SummaryRunModel.next_attempt_at.is_(None),
                    SummaryRunModel.next_attempt_at <= now,
                ),
            )
            .order_by(SummaryRunModel.next_attempt_at.asc().nullsfirst())
            .limit(limit)
        )
        models = (await self._session.execute(stmt)).scalars().all()
        return [self._to_entity(m) for m in models]

    async def reset_stale_running(
        self, threshold: datetime, now: datetime
    ) -> list[SummaryRun]:
        # attempts is deliberately NOT reset: a run that keeps dying mid-generation should still
        # exhaust its budget rather than loop forever.
        stmt = (
            update(SummaryRunModel)
            .where(
                SummaryRunModel.status == SummaryRunStatus.RUNNING.value,
                SummaryRunModel.started_at.is_not(None),
                SummaryRunModel.started_at < threshold,
            )
            .values(
                status=SummaryRunStatus.PENDING.value,
                next_attempt_at=None,  # due immediately
                updated_at_utc=now,
            )
            .returning(SummaryRunModel.id)
        )
        reset_ids = list((await self._session.execute(stmt)).scalars().all())
        await self._session.flush()
        if not reset_ids:
            return []
        models = (
            await self._session.execute(
                select(SummaryRunModel).where(SummaryRunModel.id.in_(reset_ids))
            )
        ).scalars().all()
        return [self._to_entity(m) for m in models]

    async def reopen(
        self, session_id: UUID, classroom_id: UUID | None, now: datetime
    ) -> SummaryRun:
        # Unconditional: no WHERE guard, because a human asking again outranks the state machine.
        # attempts resets to 0 so the full retry budget comes back — inheriting an exhausted
        # counter would make the manual path fail immediately, which is the opposite of its job.
        stmt = (
            pg_insert(SummaryRunModel)
            .values(
                id=uuid4(),
                session_id=session_id,
                classroom_id=classroom_id,
                status=SummaryRunStatus.PENDING.value,
                attempts=0,
                last_error=None,
                next_attempt_at=None,
                started_at=None,
                completed_at=None,
            )
            .on_conflict_do_update(
                index_elements=[SummaryRunModel.session_id],
                set_={
                    "status": SummaryRunStatus.PENDING.value,
                    "attempts": 0,
                    "last_error": None,
                    "next_attempt_at": None,
                    "started_at": None,
                    "completed_at": None,
                    "classroom_id": func.coalesce(
                        pg_insert(SummaryRunModel).excluded.classroom_id,
                        SummaryRunModel.classroom_id,
                    ),
                    "updated_at_utc": now,
                },
            )
            .returning(SummaryRunModel.id)
        )
        run_id = (await self._session.execute(stmt)).scalar_one()
        model = (
            await self._session.execute(
                select(SummaryRunModel).where(SummaryRunModel.id == run_id)
            )
        ).scalar_one()
        await self._session.flush()
        return self._to_entity(model)

    async def status_counts(self) -> dict[SummaryRunStatus, int]:
        stmt = select(SummaryRunModel.status, func.count()).group_by(SummaryRunModel.status)
        rows = (await self._session.execute(stmt)).all()
        return {SummaryRunStatus(status): count for status, count in rows}

    async def _set(
        self,
        session_id: UUID,
        *,
        status: str,
        last_error: str | None,
        next_attempt_at: datetime | None,
        completed_at: datetime | None,
        now: datetime,
    ) -> None:
        stmt = (
            update(SummaryRunModel)
            .where(SummaryRunModel.session_id == session_id)
            .values(
                status=status,
                last_error=last_error,
                next_attempt_at=next_attempt_at,
                completed_at=completed_at,
                updated_at_utc=now,
            )
        )
        await self._session.execute(stmt)
        await self._session.flush()

    @staticmethod
    def _to_entity(model: SummaryRunModel) -> SummaryRun:
        return SummaryRun(
            id=model.id,
            session_id=model.session_id,
            classroom_id=model.classroom_id,
            status=SummaryRunStatus(model.status),
            attempts=model.attempts,
            last_error=model.last_error,
            next_attempt_at=model.next_attempt_at,
            started_at=model.started_at,
            completed_at=model.completed_at,
            created_at_utc=model.created_at_utc,
            updated_at_utc=model.updated_at_utc,
        )
