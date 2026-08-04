"""AMQP consumer for ``SessionSummaryRequestedMessage`` from ClassroomService.

Replaces the synchronous HTTP POST that used to trigger a summary at session end. That call was
always a notification rather than a query — the caller read the 202 and logged it — and as an HTTP
hop it coupled session teardown to this service being reachable: if the POST failed, nothing
recorded that a summary was owed and it was simply never built.

ACK SEMANTICS ARE THE CAREFUL PART. This service is Python, so MassTransit's consumer-side retry
does not apply; broker redelivery is the only safety net, and it exists only for messages that
were NOT acked. So:

  * parse failure            -> ack. The message is malformed; redelivering it forever would
                                poison the queue, and no amount of retrying fixes bad JSON.
  * claim lost (dedup)       -> ack. Another delivery owns this run. Correct outcome, not an error.
  * claimed successfully     -> ack, then generate in the background. The DURABLE claim is what
                                makes this safe: if the process dies mid-generation, the stale
                                sweep resets the row and the retry loop picks it up. Holding the
                                message unacked for the whole minutes-long LLM job instead would
                                risk the broker's consumer timeout and a redelivery of work
                                already in flight.
  * database unreachable     -> nack + requeue. Nothing durable was written, so redelivery is the
                                only way the request survives.

Startup order is not a concern: the queue is durable and bound to the exchange on first connect,
so a message published while this service is down waits for it.
"""

from __future__ import annotations

import asyncio
import contextlib
import json
import logging
from collections.abc import Awaitable, Callable
from typing import Any
from uuid import UUID

from app.infrastructure.config.settings import Settings
from app.infrastructure.messaging.amqp import AmqpConnection
from app.infrastructure.messaging.masstransit import (
    SUMMARY_REQUESTED_TYPE,
    envelope_has_type,
    optional_uuid,
    payload_field,
    required_uuid,
)

logger = logging.getLogger("knowledge.messaging")

# Claims and runs a requested summary. Returns True if this delivery won the claim.
RequestHandler = Callable[[UUID, UUID | None, str], Awaitable[bool]]


class SummaryRequestParseError(ValueError):
    """The message is not a usable summary request. Not retryable — ack and move on."""


def parse_summary_request(body: bytes) -> tuple[UUID, UUID | None, str]:
    """Extract (session_id, classroom_id, reason) from a MassTransit envelope.

    Lenient by design. Real MassTransit envelopes carry keys this service never writes and may
    serialize payload properties in PascalCase, so matching an exact shape would pass tests and
    fail against the live bus.
    """
    try:
        envelope = json.loads(body)
    except (ValueError, TypeError) as exc:
        raise SummaryRequestParseError(f"Body is not JSON: {exc}") from exc
    if not isinstance(envelope, dict):
        raise SummaryRequestParseError("Envelope is not a JSON object.")

    # A fanout exchange only carries this type, so a mismatch means a misconfigured binding.
    # Warn rather than reject: the payload is what matters, and refusing it would silently drop
    # summaries over a naming detail.
    if not envelope_has_type(envelope, SUMMARY_REQUESTED_TYPE):
        logger.warning(
            "amqp_unexpected_message_type",
            extra={"declared": envelope.get("messageType")},
        )

    payload = envelope.get("message")
    if not isinstance(payload, dict):
        raise SummaryRequestParseError("Envelope has no 'message' object.")

    try:
        session_id = required_uuid(payload, "sessionId")
    except ValueError as exc:
        raise SummaryRequestParseError(str(exc)) from exc
    classroom_id = optional_uuid(payload, "classroomId")
    reason = str(payload_field(payload, "reason") or "Unknown")
    return session_id, classroom_id, reason


class SummaryRequestConsumer:
    """Binds a durable queue to the request exchange and dispatches to the handler."""

    def __init__(
        self,
        connection: AmqpConnection,
        settings: Settings,
        handler: RequestHandler,
    ) -> None:
        self._connection = connection
        self._queue_name = settings.summary_consumer_queue
        self._prefetch = settings.summary_consumer_prefetch
        self._handler = handler
        self._task: asyncio.Task[None] | None = None
        self.last_error: str | None = None
        self.consumed_total = 0

    def is_running(self) -> bool:
        return self._task is not None and not self._task.done()

    def start(self) -> bool:
        if self.is_running():
            return False
        self._task = asyncio.create_task(self._run(), name="summary-request-consumer")
        return True

    async def _run(self) -> None:
        import aio_pika  # lazy: only the live path needs the broker client

        while True:
            try:
                channel = await self._connection.channel()
                # Bounded prefetch: each message can start a minutes-long LLM job, so an
                # unbounded consumer would pull a whole backlog into one process.
                await channel.set_qos(prefetch_count=self._prefetch)
                exchange = await channel.declare_exchange(
                    SUMMARY_REQUESTED_TYPE, aio_pika.ExchangeType.FANOUT, durable=True
                )
                # Durable so a request published while this service is down still arrives.
                queue = await channel.declare_queue(self._queue_name, durable=True)
                await queue.bind(exchange)
                logger.info(
                    "amqp_consumer_ready",
                    extra={"queue": self._queue_name, "exchange": SUMMARY_REQUESTED_TYPE},
                )
                self.last_error = None

                async with queue.iterator() as messages:
                    async for message in messages:
                        await self._handle(message)
            except asyncio.CancelledError:
                raise
            except Exception as exc:  # noqa: BLE001 — reconnect rather than die
                self.last_error = f"{type(exc).__name__}: {exc}"
                logger.warning(
                    "amqp_consumer_reconnecting", extra={"error_type": type(exc).__name__}
                )
                await asyncio.sleep(5.0)

    async def _handle(self, message: Any) -> None:
        try:
            session_id, classroom_id, reason = parse_summary_request(message.body)
        except SummaryRequestParseError as exc:
            # Unfixable by retrying. Ack so one bad message cannot block the queue forever.
            logger.error("amqp_message_unparseable", extra={"error": str(exc)})
            await message.ack()
            return

        try:
            claimed = await self._handler(session_id, classroom_id, reason)
        except Exception as exc:  # noqa: BLE001
            # Nothing durable was written (the claim itself failed), so redelivery is the only
            # way this request survives.
            logger.warning(
                "amqp_claim_failed",
                extra={"session_id": str(session_id), "error_type": type(exc).__name__},
            )
            with contextlib.suppress(Exception):
                await message.nack(requeue=True)
            return

        # Ack whether or not we won: losing the claim means another delivery owns the run, which
        # is the dedup working, not a failure.
        await message.ack()
        self.consumed_total += 1
        logger.info(
            "amqp_summary_requested",
            extra={"session_id": str(session_id), "reason": reason, "claimed": claimed},
        )

    async def stop(self) -> None:
        if self._task is not None:
            self._task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._task
            self._task = None
