"""Shared AMQP connection plumbing for the outbox relay and the summary consumer.

WHY A SHARED, LONG-LIVED CONNECTION. The original publisher opened a fresh connection per publish
and closed it in a finally. That is acceptable for one message at the end of a lecture; it is not
acceptable for a relay that drains a backlog, where per-message TCP + AMQP handshakes would
dominate the work. ``connect_robust`` also reconnects on its own, which is exactly the behaviour a
relay wants during the broker outage it exists to survive.

aio_pika is imported lazily, as it was before: the offline test suite drives fakes and must not
need a broker or the dependency.
"""

from __future__ import annotations

import asyncio
import logging
from typing import Any

from app.infrastructure.config.settings import Settings

logger = logging.getLogger("knowledge.messaging")


def broker_url(settings: Settings) -> str:
    """AMQP URL from the RABBITMQ_* settings.

    Credentials are not URL-encoded, matching the previous publisher. Fine for the current
    password; a username or password containing '@' or '/' would need quoting.
    """
    return (
        f"amqp://{settings.rabbitmq_username}:{settings.rabbitmq_password}"
        f"@{settings.rabbitmq_host}:{settings.rabbitmq_port}/"
        f"{settings.rabbitmq_vhost.lstrip('/')}"
    )


class AmqpConnection:
    """Lazily-opened, auto-reconnecting connection shared by publisher and consumer.

    Connecting is deferred to first use rather than done at startup so the service still boots
    when the broker is down — ingestion, search and answering do not need it, and the outbox means
    summaries survive the outage anyway. A hard dependency at startup would turn a broker blip
    into a service that will not start.
    """

    def __init__(self, settings: Settings) -> None:
        self._url = broker_url(settings)
        self._connection: Any | None = None
        self._lock = asyncio.Lock()

    async def channel(self) -> Any:
        """A fresh channel on the shared connection.

        Per-channel, not per-connection: a channel is cheap, and AMQP closes a whole channel on a
        protocol error, so sharing one between the relay and the consumer would let either kill
        the other.
        """
        connection = await self._ensure_connection()
        return await connection.channel()

    async def _ensure_connection(self) -> Any:
        import aio_pika  # lazy: only the live path needs the broker client

        # Double-checked under a lock so a burst of concurrent first-uses opens ONE connection.
        if self._connection is not None and not self._connection.is_closed:
            return self._connection
        async with self._lock:
            if self._connection is not None and not self._connection.is_closed:
                return self._connection
            logger.info("amqp_connecting")
            self._connection = await aio_pika.connect_robust(self._url)
            return self._connection

    async def close(self) -> None:
        if self._connection is not None and not self._connection.is_closed:
            await self._connection.close()
        self._connection = None
