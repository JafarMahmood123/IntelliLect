"""PostgreSQL-backed ``TranscriptRepository`` via async SQLAlchemy (S-0).

Owns a session *factory* (not a single request-scoped session) because the transcript
writer is long-lived: it appends across a whole session from a background task. Each
method opens a short transaction, so a mid-session crash leaves every already-appended
segment durably committed.

``order_index`` is assigned as ``max(order_index) + 1`` for the session. A single
session's segments are appended sequentially by one recorder worker, so there is no
intra-session write race; the ``(session_id, order_index)`` unique constraint is the
backstop.
"""

from __future__ import annotations

from uuid import UUID

from sqlalchemy import func, select
from sqlalchemy.dialects.postgresql import insert as pg_insert
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker

from app.application.ports.transcript_repository import TranscriptRepository
from app.domain.transcript.session_transcript import SessionTranscript
from app.domain.transcript.stored_segment import StoredSegment, assemble_transcript_text
from app.domain.transcript.transcript_segment import TranscriptSegment
from app.domain.transcript.transcript_status import TranscriptStatus
from app.infrastructure.persistence.models import (
    SessionTranscriptModel,
    TranscriptSegmentModel,
)


class SqlAlchemyTranscriptRepository(TranscriptRepository):
    def __init__(self, session_factory: async_sessionmaker[AsyncSession]) -> None:
        self._session_factory = session_factory

    async def ensure_session(self, session_id: UUID, classroom_id: UUID) -> None:
        # Idempotent create: on a repeat call, do nothing (never resets status).
        stmt = (
            pg_insert(SessionTranscriptModel)
            .values(
                session_id=session_id,
                classroom_id=classroom_id,
                status=TranscriptStatus.RECORDING.value,
            )
            .on_conflict_do_nothing(index_elements=[SessionTranscriptModel.session_id])
        )
        async with self._session_factory() as session:
            await session.execute(stmt)
            await session.commit()

    async def append_segment(self, session_id: UUID, segment: TranscriptSegment) -> None:
        async with self._session_factory() as session:
            next_index = (
                await session.execute(
                    select(func.coalesce(func.max(TranscriptSegmentModel.order_index), -1) + 1)
                    .where(TranscriptSegmentModel.session_id == session_id)
                )
            ).scalar_one()
            session.add(
                TranscriptSegmentModel(
                    session_id=session_id,
                    order_index=next_index,
                    text=segment.text,
                    start_ms=segment.start_ms,
                    end_ms=segment.end_ms,
                )
            )
            await session.commit()

    async def get_transcript(self, session_id: UUID) -> list[StoredSegment]:
        stmt = (
            select(TranscriptSegmentModel)
            .where(TranscriptSegmentModel.session_id == session_id)
            .order_by(TranscriptSegmentModel.order_index.asc())
        )
        async with self._session_factory() as session:
            models = (await session.execute(stmt)).scalars().all()
        return [self._to_segment(model) for model in models]

    async def finalize(self, session_id: UUID) -> None:
        async with self._session_factory() as session:
            model = await session.get(SessionTranscriptModel, session_id)
            if model is None:
                return
            model.status = TranscriptStatus.FINALIZED.value
            await session.commit()

    async def assemble_text(self, session_id: UUID) -> str:
        return assemble_transcript_text(await self.get_transcript(session_id))

    async def get_session_transcript(self, session_id: UUID) -> SessionTranscript | None:
        async with self._session_factory() as session:
            model = await session.get(SessionTranscriptModel, session_id)
        return self._to_header(model) if model is not None else None

    @staticmethod
    def _to_segment(model: TranscriptSegmentModel) -> StoredSegment:
        return StoredSegment(
            session_id=model.session_id,
            order_index=model.order_index,
            text=model.text,
            start_ms=model.start_ms,
            end_ms=model.end_ms,
            created_at=model.created_at,
        )

    @staticmethod
    def _to_header(model: SessionTranscriptModel) -> SessionTranscript:
        return SessionTranscript(
            session_id=model.session_id,
            classroom_id=model.classroom_id,
            status=TranscriptStatus(model.status),
            created_at=model.created_at,
            updated_at=model.updated_at,
        )
