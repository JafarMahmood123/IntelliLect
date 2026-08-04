"""MassTransit-compatible RabbitMQ publisher for the summary-ready event (S-3).

DEFERRED / live path: publishes ``SessionSummaryReadyMessage`` so the .NET
ClassroomService (S-4) consumes it via MassTransit. It reproduces MassTransit's
RabbitMQ conventions:
  * a durable **fanout** exchange named ``<Namespace>:<TypeName>`` (the message type),
  * the MassTransit JSON **envelope** (messageType URN + ``message`` payload), and
  * content-type ``application/vnd.masstransit+json``.

aio-pika is imported lazily so the offline test suite (which uses a fake publisher)
never needs a broker or the dependency. Not exercised by the offline tests.
"""

from __future__ import annotations

import json
import logging
from datetime import datetime, timezone
from uuid import uuid4

from app.application.dtos.summary_messages import SessionSummaryReadyMessage
from app.application.ports.summary_publisher import SummaryPublisher
from app.infrastructure.config.settings import Settings

logger = logging.getLogger("knowledge.messaging")

# The .NET contract type: MassTransit derives the exchange name and messageType URN
# from the message's namespace-qualified name.
_MESSAGE_TYPE = "IntelliLect.Contracts.Messages:SessionSummaryReadyMessage"
_MESSAGE_TYPE_URN = f"urn:message:{_MESSAGE_TYPE}"
_MASSTRANSIT_CONTENT_TYPE = "application/vnd.masstransit+json"


class MassTransitSummaryPublisher(SummaryPublisher):
    """Publishes the summary-ready event onto RabbitMQ in MassTransit's envelope format."""

    def __init__(self, settings: Settings) -> None:
        self._url = (
            f"amqp://{settings.rabbitmq_username}:{settings.rabbitmq_password}"
            f"@{settings.rabbitmq_host}:{settings.rabbitmq_port}/"
            f"{settings.rabbitmq_vhost.lstrip('/')}"
        )
        self._source = f"rabbitmq://{settings.rabbitmq_host}/RagService"

    async def publish_ready(self, message: SessionSummaryReadyMessage) -> None:
        import aio_pika  # lazy: only the live path needs the broker client

        body = json.dumps(self._envelope(message)).encode("utf-8")
        connection = await aio_pika.connect_robust(self._url)
        try:
            channel = await connection.channel()
            exchange = await channel.declare_exchange(
                _MESSAGE_TYPE, aio_pika.ExchangeType.FANOUT, durable=True
            )
            await exchange.publish(
                aio_pika.Message(
                    body=body,
                    content_type=_MASSTRANSIT_CONTENT_TYPE,
                    message_id=str(uuid4()),
                    correlation_id=str(message.session_id),
                ),
                routing_key="",
            )
            logger.info(
                "Published SessionSummaryReadyMessage (succeeded=%s) for session %s.",
                message.succeeded,
                message.session_id,
            )
        finally:
            await connection.close()

    def _envelope(self, message: SessionSummaryReadyMessage) -> dict:
        return {
            "messageId": str(uuid4()),
            "conversationId": str(message.session_id),
            "sourceAddress": self._source,
            "destinationAddress": f"rabbitmq:///{_MESSAGE_TYPE}",
            "messageType": [_MESSAGE_TYPE_URN],
            "message": message.to_contract(),
            "sentTime": datetime.now(timezone.utc).isoformat(),
            "headers": {},
        }
