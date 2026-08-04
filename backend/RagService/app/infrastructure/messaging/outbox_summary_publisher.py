"""SummaryPublisher that writes to the outbox instead of the broker.

This is the fix for the failure that cost a full Gemini run on 2026-07-30: the summary was
generated, rendered to PDF and uploaded to S3, and only then did the publish throw because the
broker hostname was wrong. The artifacts existed, nobody was told, and the classroom sat on a
Generating row forever — the FAILURE notice could not publish either.

Satisfying the same ``SummaryPublisher`` port means the pipeline is unchanged: it still "publishes"
at the end. What changes is that publishing is now a row in the caller's transaction rather than a
network call, so it either commits with the run's terminal state or not at all. The relay delivers
it afterwards, taking as long as the broker needs.
"""

from __future__ import annotations

import logging

from app.application.dtos.summary_messages import SessionSummaryReadyMessage
from app.application.ports.outbox_repository import OutboxRepository
from app.application.ports.summary_publisher import SummaryPublisher
from app.infrastructure.messaging.masstransit import (
    SUMMARY_READY_TYPE,
    build_envelope,
)

logger = logging.getLogger("knowledge.messaging")


class OutboxSummaryPublisher(SummaryPublisher):
    """Stages ``SessionSummaryReadyMessage`` in the outbox for the relay to deliver."""

    def __init__(self, outbox: OutboxRepository, source_host: str) -> None:
        self._outbox = outbox
        self._source_host = source_host

    async def publish_ready(self, message: SessionSummaryReadyMessage) -> None:
        # The envelope is built HERE, not in the relay, so sentTime reflects when the outcome
        # actually happened. A message delayed by a broker outage would otherwise claim to have
        # been sent at the moment the outage ended.
        envelope = build_envelope(
            SUMMARY_READY_TYPE,
            message.to_contract(),
            source_host=self._source_host,
            conversation_id=message.session_id,
        )
        await self._outbox.enqueue(
            exchange=SUMMARY_READY_TYPE,
            message_type=SUMMARY_READY_TYPE,
            payload=envelope,
            correlation_id=message.session_id,
        )
        logger.info(
            "outbox_enqueued",
            extra={
                "message_type": "SessionSummaryReadyMessage",
                "session_id": str(message.session_id),
                "succeeded": message.succeeded,
            },
        )
