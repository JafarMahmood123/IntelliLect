"""SessionManager — one pipeline per session, idempotency, cap, teardown (LA-6)."""

from __future__ import annotations

import asyncio
from uuid import uuid4

import pytest

from app.application.services.session_manager import SessionCapacityError, SessionManager
from app.domain.entities.session_context import SessionContext
from tests.support.fake_pipeline import FakePipelineFactory


def _session() -> SessionContext:
    return SessionContext(uuid4(), uuid4(), "teacher", "room")


def _manager(max_concurrent: int = 10) -> tuple[SessionManager, FakePipelineFactory]:
    factory = FakePipelineFactory()
    return SessionManager(factory, max_concurrent), factory


async def test_start_launches_and_registers_one_pipeline():
    manager, factory = _manager()
    session = _session()

    pipeline = await manager.start(session)

    assert pipeline.started is True
    assert manager.active_count() == 1
    assert manager.active_session_ids() == [session.session_id]
    assert len(factory.created) == 1


async def test_duplicate_start_is_a_noop_returning_the_same_pipeline():
    manager, factory = _manager()
    session = _session()

    first = await manager.start(session)
    second = await manager.start(session)

    assert first is second
    assert manager.active_count() == 1
    assert len(factory.created) == 1  # no second pipeline built


async def test_stop_cancels_and_deregisters():
    manager, _ = _manager()
    session = _session()
    pipeline = await manager.start(session)

    stopped = await manager.stop(session.session_id)

    assert stopped is True
    assert pipeline.stopped is True
    assert manager.active_count() == 0


async def test_stop_unknown_session_is_safe_noop():
    manager, _ = _manager()

    assert await manager.stop(uuid4()) is False


async def test_capacity_cap_rejects_starts_beyond_limit():
    manager, _ = _manager(max_concurrent=2)

    await manager.start(_session())
    await manager.start(_session())

    with pytest.raises(SessionCapacityError):
        await manager.start(_session())
    assert manager.active_count() == 2


async def test_stop_all_stops_every_active_pipeline():
    manager, factory = _manager()
    await manager.start(_session())
    await manager.start(_session())

    await manager.stop_all()

    assert manager.active_count() == 0
    assert all(p.stopped for p in factory.created)


async def test_pipeline_ending_on_its_own_is_deregistered():
    manager, _ = _manager()
    session = _session()
    pipeline = await manager.start(session)

    pipeline.finish()  # simulate crash / natural stream end
    await asyncio.sleep(0)  # let the done-callback run

    assert manager.active_count() == 0
    assert pipeline.stopped is False  # ended on its own, not via stop()
