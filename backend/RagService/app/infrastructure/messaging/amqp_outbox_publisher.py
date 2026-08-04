"""Publishes an outbox row to RabbitMQ in MassTransit's envelope format.

The transport half of the relay. The envelope is already built and stored, so this only has to
declare the exchange and put the bytes on it — which is why an outbox row survives a deploy that
changes the message class.
"""

from __future__ import annotations

import json
import logging

from app.application.ports.outbox_repository import OutboxMessage
from app.infrastructure.messaging.amqp import AmqpConnection
from app.infrastructure.messaging.masstransit import MASSTRANSIT_CONTENT_TYPE

logger = logging.getLogger("knowledge.messaging")


class AmqpOutboxPublisher:
    """Publishes outbox messages onto their durable fanout exchange."""

    def __init__(self, connection: AmqpConnection) -> None:
        self._connection = connection

    async def publish(self, message: OutboxMessage) -> None:
        import aio_pika  # lazy: only the live path needs the broker client

        channel = await self._connection.channel()
        try:
            # Declared on every publish, as MassTransit itself does. Idempotent, and it means a
            # message can be delivered before the .NET consumer has ever started — the exchange
            # exists either way, so nothing is lost to startup order.
            exchange = await channel.declare_exchange(
                message.exchange, aio_pika.ExchangeType.FANOUT, durable=True
            )
            await exchange.publish(
                aio_pika.Message(
                    body=json.dumps(message.payload).encode("utf-8"),
                    content_type=MASSTRANSIT_CONTENT_TYPE,
                    message_id=str(message.message_id),
                    correlation_id=(
                        str(message.correlation_id) if message.correlation_id else None
                    ),
                    # Survive a broker restart; pointless to have a durable outbox in front of a
                    # transient message.
                    delivery_mode=aio_pika.DeliveryMode.PERSISTENT,
                ),
                routing_key="",
            )
            logger.info(
                "outbox_published",
                extra={
                    "outbox_id": message.id,
                    "message_type": message.message_type,
                    "attempts": message.attempts,
                },
            )
        finally:
            await channel.close()
