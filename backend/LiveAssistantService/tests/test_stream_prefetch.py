"""Exercise the STT->boundary prefetch buffer (ordering, faults, teardown, overlap).

The point of ``prefetch`` is that a slow consumer no longer stalls the producer, so the
overlap test is the one that would catch a regression to the old alternating behaviour —
the rest pin down that the decoupling is otherwise transparent.
"""

from __future__ import annotations

import asyncio

import pytest

from app.application.services.stream_prefetch import prefetch


async def _arange(n: int):
    for i in range(n):
        yield i


async def _collect(source) -> list:
    return [item async for item in source]


@pytest.mark.asyncio
async def test_yields_every_item_in_order():
    assert await _collect(prefetch(_arange(25), max_buffered=4)) == list(range(25))


@pytest.mark.asyncio
async def test_empty_source_yields_nothing():
    assert await _collect(prefetch(_arange(0), max_buffered=2)) == []


@pytest.mark.asyncio
async def test_producer_error_surfaces_after_preceding_items():
    """A stream fault must arrive in order, not jump the queue or vanish."""

    async def failing():
        yield "a"
        yield "b"
        raise RuntimeError("stt exploded")

    seen = []
    with pytest.raises(RuntimeError, match="stt exploded"):
        async for item in prefetch(failing(), max_buffered=8):
            seen.append(item)
    assert seen == ["a", "b"]


@pytest.mark.asyncio
async def test_producer_runs_ahead_while_consumer_is_busy():
    """The regression guard: producer and consumer must OVERLAP, not alternate.

    The producer records how many items it has emitted. With a buffer of 4, by the time the
    slow consumer has taken its first item the producer must already be several ahead — under
    the old direct-iteration behaviour it could never be more than one.
    """
    produced = []

    async def producer():
        for i in range(10):
            produced.append(i)
            await asyncio.sleep(0)  # let the pump be scheduled
            yield i

    stream = prefetch(producer(), max_buffered=4)
    first = await stream.__anext__()
    # Give the pump room to fill the buffer while "the consumer is embedding".
    await asyncio.sleep(0.05)

    assert first == 0
    assert len(produced) >= 4, f"producer only reached {len(produced)}; it is not running ahead"

    assert await _collect(stream) == list(range(1, 10))


@pytest.mark.asyncio
async def test_buffer_is_bounded():
    """A fast producer must not be allowed to buffer the whole stream (memory guard)."""
    produced = []

    async def producer():
        for i in range(1000):
            produced.append(i)
            await asyncio.sleep(0)
            yield i

    stream = prefetch(producer(), max_buffered=4)
    await stream.__anext__()
    await asyncio.sleep(0.05)

    # One in flight + queue capacity + a little scheduling slack — nowhere near 1000.
    assert len(produced) < 20, f"buffer is unbounded: producer reached {len(produced)}"
    await stream.aclose()


@pytest.mark.asyncio
async def test_early_exit_cancels_the_pump_and_closes_the_source():
    """Stopping a session mid-stream must not leave an orphan task or an open generator."""
    closed = asyncio.Event()

    async def producer():
        try:
            for i in range(1000):
                await asyncio.sleep(0)
                yield i
        finally:
            closed.set()

    stream = prefetch(producer(), max_buffered=4)
    await stream.__anext__()
    await stream.aclose()

    await asyncio.wait_for(closed.wait(), timeout=1.0)
    pending = [
        t for t in asyncio.all_tasks() if t.get_name() == "stt-prefetch-pump" and not t.done()
    ]
    assert not pending, "prefetch pump outlived the stream"


@pytest.mark.asyncio
async def test_rejects_a_useless_buffer_size():
    with pytest.raises(ValueError):
        await _collect(prefetch(_arange(1), max_buffered=0))
