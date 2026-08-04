"""Background relay that drains the outbox to the broker.

The second half of the transactional outbox. Work commits a message into the database; this loop
delivers it whenever the broker is reachable. A broker outage therefore delays a summary
notification instead of destroying it — which is the whole point, because by the time the outcome
message exists the expensive work (transcript fetch, LLM, PDF render, S3 upload) has already been
done and paid for.

DELIVERY IS AT-LEAST-ONCE. A publish that succeeds but whose ``mark_published`` fails will be
retried, so the same message can reach the bus twice. That is safe here: ClassroomService's summary
consumer upserts by session id, and the summary pipeline's S3 keys are deterministic. Guaranteeing
exactly-once would need a distributed transaction across Postgres and RabbitMQ, which is not worth
it for an idempotent consumer.

Failures are never dropped. ``outbox_max_attempts = 0`` means retry forever: a message that cannot
be delivered is a message the classroom will never see, so the correct response is to keep trying
and let the backlog metric make the problem visible.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
from collections.abc import Awaitable, Callable

from app.application.ports.outbox_repository import OutboxMessage

logger = logging.getLogger("knowledge.outbox")

# Publishes one message, raising on failure. Injected so the loop is testable without a broker.
PublishFn = Callable[[OutboxMessage], Awaitable[None]]
# Runs one drain pass and returns how many messages were published.
DrainFn = Callable[[], Awaitable[int]]


class OutboxRelay:
    """Owns the single polling task that drains the outbox."""

    def __init__(self, drain: DrainFn, poll_seconds: float) -> None:
        self._drain = drain
        self._poll_seconds = poll_seconds
        self._task: asyncio.Task[None] | None = None
        # Observable state, so a stuck relay is diagnosable without reading the table.
        self.last_error: str | None = None
        self.published_total = 0

    def is_running(self) -> bool:
        return self._task is not None and not self._task.done()

    def start(self) -> bool:
        """Launch the relay. Returns False if it is already running."""
        if self.is_running():
            return False
        self._task = asyncio.create_task(self._run(), name="outbox-relay")
        return True

    async def _run(self) -> None:
        while True:
            try:
                published = await self._drain()
                self.published_total += published
                self.last_error = None
                # A full-looking pass probably has more waiting, so go straight round again
                # rather than sleeping a backlog away one poll interval at a time.
                if published == 0:
                    await asyncio.sleep(self._poll_seconds)
            except asyncio.CancelledError:
                raise
            except Exception as exc:  # noqa: BLE001 — the relay must outlive any single failure
                # Broker down, DB blip, bad credentials. Log the type and keep going: the
                # messages stay in the table, so nothing is lost by failing this pass.
                self.last_error = f"{type(exc).__name__}: {exc}"
                logger.warning(
                    "outbox_drain_failed", extra={"error_type": type(exc).__name__}
                )
                await asyncio.sleep(self._poll_seconds)

    async def stop(self) -> None:
        """Cancel the relay (shutdown). Undelivered rows simply wait for the next start."""
        if self._task is not None:
            self._task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._task
            self._task = None
