from __future__ import annotations

from datetime import datetime
from typing import Any
from uuid import UUID, uuid4

from sqlalchemy import func, select, update
from sqlalchemy.ext.asyncio import AsyncSession

from app.application.ports.outbox_repository import OutboxMessage, OutboxRepository
from app.infrastructure.persistence.models import OutboxMessageModel


class SqlAlchemyOutboxRepository(OutboxRepository):
    """OutboxRepository backed by PostgreSQL via async SQLAlchemy.

    ``enqueue`` deliberately does NOT commit — it flushes into the caller's transaction, so the
    message lands only if the work that produced it does. Committing here would reintroduce
    exactly the two-step non-atomicity the outbox exists to remove.
    """

    def __init__(self, session: AsyncSession) -> None:
        self._session = session

    async def enqueue(
        self,
        exchange: str,
        message_type: str,
        payload: dict[str, Any],
        *,
        correlation_id: UUID | None = None,
        message_id: UUID | None = None,
    ) -> None:
        model = OutboxMessageModel(
            message_id=message_id or uuid4(),
            exchange=exchange,
            message_type=message_type,
            payload=payload,
            correlation_id=correlation_id,
        )
        self._session.add(model)
        await self._session.flush()

    async def fetch_unpublished(self, limit: int) -> list[OutboxMessage]:
        stmt = (
            select(OutboxMessageModel)
            .where(OutboxMessageModel.published_at_utc.is_(None))
            # Insertion order. Consumers may reasonably assume a session's messages arrive in the
            # order they happened, and the bigserial id is the only total order available.
            .order_by(OutboxMessageModel.id.asc())
            .limit(limit)
        )
        models = (await self._session.execute(stmt)).scalars().all()
        return [
            OutboxMessage(
                id=m.id,
                message_id=m.message_id,
                exchange=m.exchange,
                message_type=m.message_type,
                payload=m.payload,
                correlation_id=m.correlation_id,
                attempts=m.attempts,
                created_at_utc=m.created_at_utc,
            )
            for m in models
        ]

    async def mark_published(self, outbox_id: int, now: datetime) -> None:
        # The row is kept rather than deleted so a publish can be audited after the fact; the
        # partial-ish index on published_at_utc keeps the relay's query cheap regardless.
        await self._session.execute(
            update(OutboxMessageModel)
            .where(OutboxMessageModel.id == outbox_id)
            .values(published_at_utc=now, last_error=None)
        )
        await self._session.flush()

    async def record_failure(self, outbox_id: int, error: str) -> None:
        await self._session.execute(
            update(OutboxMessageModel)
            .where(OutboxMessageModel.id == outbox_id)
            .values(
                attempts=OutboxMessageModel.attempts + 1,
                last_error=error[:2000],
            )
        )
        await self._session.flush()

    async def count_unpublished(self) -> int:
        stmt = select(func.count()).where(OutboxMessageModel.published_at_utc.is_(None))
        return (await self._session.execute(stmt)).scalar_one()
