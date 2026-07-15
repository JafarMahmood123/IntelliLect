from __future__ import annotations

from abc import ABC, abstractmethod

from app.application.dtos.summary_messages import SessionSummaryReadyMessage


class SummaryPublisher(ABC):
    """Port for publishing the SessionSummaryReadyMessage onto the message bus.

    Implemented in the infrastructure layer (MassTransit-compatible RabbitMQ publish)
    so ClassroomService (S-4) can consume it. The application layer depends only on
    this abstraction and is tested with an in-memory publisher that records messages.
    """

    @abstractmethod
    async def publish_ready(self, message: SessionSummaryReadyMessage) -> None:
        """Publish the summary outcome (success or failure). Raises if it cannot send."""
        raise NotImplementedError
