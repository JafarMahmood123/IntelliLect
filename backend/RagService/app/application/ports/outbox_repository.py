from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import Any
from uuid import UUID


@dataclass(frozen=True)
class OutboxMessage:
    """A message owed to the broker, already serialized into its final envelope.

    The envelope is built at ENQUEUE time, not at publish time, so the relay never needs the
    domain object. That keeps the relay a dumb pipe and means a message enqueued before a deploy
    still publishes correctly afterwards, even if the message class changed.
    """

    id: int
    message_id: UUID
    exchange: str
    message_type: str
    payload: dict[str, Any]
    correlation_id: UUID | None
    attempts: int
    created_at_utc: datetime


class OutboxRepository(ABC):
    """Persistence port for the transactional outbox.

    ``enqueue`` is called INSIDE the transaction that records the work, which is the whole point:
    the message and the state change commit together, so a broker outage can no longer discard an
    outcome whose expensive work has already been done and paid for.
    """

    @abstractmethod
    async def enqueue(
        self,
        exchange: str,
        message_type: str,
        payload: dict[str, Any],
        *,
        correlation_id: UUID | None = None,
        message_id: UUID | None = None,
    ) -> None:
        """Stage a message for publication. Does NOT talk to the broker."""
        raise NotImplementedError

    @abstractmethod
    async def fetch_unpublished(self, limit: int) -> list[OutboxMessage]:
        """Oldest-first batch of messages still owed to the broker."""
        raise NotImplementedError

    @abstractmethod
    async def mark_published(self, outbox_id: int, now: datetime) -> None:
        """Record a successful publish so the relay stops offering this message."""
        raise NotImplementedError

    @abstractmethod
    async def record_failure(self, outbox_id: int, error: str) -> None:
        """Increment the attempt counter and store the error; the row stays unpublished."""
        raise NotImplementedError

    @abstractmethod
    async def count_unpublished(self) -> int:
        """Backlog size, for metrics and for asserting the relay drained in tests."""
        raise NotImplementedError
