"""SummaryRunner (S-3) — background dispatch, per-session dedup, non-fatal. Offline."""

from __future__ import annotations

import asyncio
from uuid import uuid4

from app.application.services.summary_runner import SummaryRunner


async def test_enqueue_runs_the_handle_in_the_background():
    seen: list = []

    async def handle(session_id, classroom_id):
        seen.append((session_id, classroom_id))

    runner = SummaryRunner(handle)
    sid, cid = uuid4(), uuid4()

    assert runner.enqueue(sid, cid) is True
    await runner.drain()

    assert seen == [(sid, cid)]


async def test_duplicate_enqueue_while_in_flight_is_skipped():
    gate = asyncio.Event()
    runs = 0

    async def handle(session_id, classroom_id):
        nonlocal runs
        runs += 1
        await gate.wait()  # hold the run open so the second enqueue overlaps

    runner = SummaryRunner(handle)
    sid = uuid4()

    assert runner.enqueue(sid, None) is True     # starts, then blocks on the gate
    await asyncio.sleep(0)                        # let the task begin
    assert runner.enqueue(sid, None) is False     # same session in flight -> skipped
    assert runner.enqueue(uuid4(), None) is True  # a different session still runs

    gate.set()
    await runner.drain()
    assert runs == 2  # the duplicate never ran a second pipeline


async def test_reenqueue_after_completion_is_allowed():
    async def handle(session_id, classroom_id):
        return None

    runner = SummaryRunner(handle)
    sid = uuid4()

    assert runner.enqueue(sid, None) is True
    await runner.drain()
    assert runner.enqueue(sid, None) is True  # no longer in flight
    await runner.drain()


async def test_handle_failure_is_non_fatal():
    async def handle(session_id, classroom_id):
        raise RuntimeError("pipeline blew up")

    runner = SummaryRunner(handle)

    assert runner.enqueue(uuid4(), None) is True
    await runner.drain()  # must not raise; the error is swallowed and logged
    # A subsequent enqueue still works (in-flight set was cleared).
    assert runner.enqueue(uuid4(), None) is True
    await runner.drain()
