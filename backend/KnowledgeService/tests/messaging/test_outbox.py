"""Cover the outbox: staging, and the relay loop's behaviour when the broker misbehaves.

The scenario this exists for is concrete. On 2026-07-30 a summary was generated, rendered to PDF
and uploaded to S3, and the publish then failed on a DNS error — so the classroom never heard, and
the failure notice could not publish either. The tests below pin the two properties that make that
impossible now: staging never touches the network, and a publish failure leaves the message in the
table for the next pass instead of dropping it.
"""

from __future__ import annotations

import asyncio
from datetime import datetime, timezone
from uuid import uuid4

import pytest

from app.application.dtos.summary_messages import SessionSummaryReadyMessage
from app.application.ports.outbox_repository import OutboxMessage
from app.application.services.outbox_relay import OutboxRelay
from app.infrastructure.messaging.masstransit import SUMMARY_READY_TYPE
from app.infrastructure.messaging.outbox_summary_publisher import OutboxSummaryPublisher

_NOW = datetime(2026, 7, 30, 12, 0, tzinfo=timezone.utc)


class FakeOutboxRepository:
    def __init__(self) -> None:
        self.enqueued: list[dict] = []

    async def enqueue(
        self, exchange, message_type, payload, *, correlation_id=None, message_id=None
    ) -> None:
        self.enqueued.append(
            {
                "exchange": exchange,
                "message_type": message_type,
                "payload": payload,
                "correlation_id": correlation_id,
            }
        )

    async def fetch_unpublished(self, limit):  # pragma: no cover - unused here
        return []

    async def mark_published(self, outbox_id, now):  # pragma: no cover - unused here
        return None

    async def record_failure(self, outbox_id, error):  # pragma: no cover - unused here
        return None

    async def count_unpublished(self):  # pragma: no cover - unused here
        return 0


# --- staging -------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_publishing_stages_a_full_envelope_without_touching_the_broker() -> None:
    outbox = FakeOutboxRepository()
    session_id = uuid4()
    message = SessionSummaryReadyMessage.success(
        session_id, uuid4(), "md-key", "pdf-key", _NOW
    )

    await OutboxSummaryPublisher(outbox, "intellilect-mq").publish_ready(message)

    assert len(outbox.enqueued) == 1
    staged = outbox.enqueued[0]
    assert staged["exchange"] == SUMMARY_READY_TYPE
    assert staged["correlation_id"] == session_id
    # The COMPLETE envelope is stored, so the relay never needs the domain object and a message
    # queued before a deploy still publishes correctly after it.
    assert staged["payload"]["messageType"] == [f"urn:message:{SUMMARY_READY_TYPE}"]
    assert staged["payload"]["message"]["mdS3Key"] == "md-key"


@pytest.mark.asyncio
async def test_a_failure_message_is_staged_the_same_way() -> None:
    """The 2026-07-30 bug: the FAILURE notice could not publish either, so nothing was recorded."""
    outbox = FakeOutboxRepository()
    message = SessionSummaryReadyMessage.failure(uuid4(), uuid4(), "boom")

    await OutboxSummaryPublisher(outbox, "h").publish_ready(message)

    assert outbox.enqueued[0]["payload"]["message"]["succeeded"] is False


# --- the relay loop ------------------------------------------------------------------


def _message(outbox_id: int = 1) -> OutboxMessage:
    return OutboxMessage(
        id=outbox_id,
        message_id=uuid4(),
        exchange=SUMMARY_READY_TYPE,
        message_type=SUMMARY_READY_TYPE,
        payload={},
        correlation_id=None,
        attempts=0,
        created_at_utc=_NOW,
    )


@pytest.mark.asyncio
async def test_relay_keeps_draining_while_messages_remain() -> None:
    """A backlog must not drain one poll-interval at a time."""
    passes = 0

    async def drain() -> int:
        nonlocal passes
        passes += 1
        return 1 if passes < 4 else 0

    relay = OutboxRelay(drain, poll_seconds=60.0)
    relay.start()
    # Only the empty pass sleeps, so three productive passes complete without waiting 60s each.
    await asyncio.sleep(0.05)
    await relay.stop()

    assert passes >= 4, f"relay stalled after {passes} passes"
    assert relay.published_total == 3


@pytest.mark.asyncio
async def test_relay_survives_a_failing_pass() -> None:
    """A broker outage is exactly the case the outbox exists for; the loop must not die."""
    calls = 0

    async def drain() -> int:
        nonlocal calls
        calls += 1
        if calls == 1:
            raise ConnectionError("broker down")
        return 0

    relay = OutboxRelay(drain, poll_seconds=0.01)
    relay.start()
    await asyncio.sleep(0.08)
    await relay.stop()

    assert calls > 1, "relay died on the first failure instead of retrying"
    assert relay.last_error is None, "last_error should clear once a pass succeeds"


@pytest.mark.asyncio
async def test_relay_records_the_last_error() -> None:
    async def drain() -> int:
        raise ConnectionError("broker down")

    relay = OutboxRelay(drain, poll_seconds=0.01)
    relay.start()
    await asyncio.sleep(0.03)
    await relay.stop()

    assert relay.last_error is not None
    assert "ConnectionError" in relay.last_error


@pytest.mark.asyncio
async def test_relay_refuses_to_start_twice() -> None:
    async def drain() -> int:
        return 0

    relay = OutboxRelay(drain, poll_seconds=0.01)
    try:
        assert relay.start() is True
        assert relay.start() is False, "a second relay would double-publish every message"
    finally:
        await relay.stop()


@pytest.mark.asyncio
async def test_stop_is_safe_when_never_started() -> None:
    async def drain() -> int:  # pragma: no cover - never called
        return 0

    await OutboxRelay(drain, poll_seconds=0.01).stop()
