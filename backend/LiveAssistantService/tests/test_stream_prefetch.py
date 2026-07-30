"""Exercise the STT->boundary prefetch buffer (ordering, faults, teardown, overlap).

The point of ``prefetch`` is that a slow consumer no longer stalls the producer, so the
overlap test is the one that would catch a regression to the old alternating behaviour —
the rest pin down that the decoupling is otherwise transparent.
"""

from __future__ import annotations

import asyncio

import pytest

from app.application.services.stream_prefetch import lookahead_map, prefetch


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


# --- lookahead_map: concurrent embeddings, ordered decisions ---------------------------------
#
# The contract that matters is the PAIR of properties: the calls overlap, AND the results come back
# in source order regardless of which finished first. Either one alone would be wrong — overlapping
# without ordering breaks the boundary detector's sequential buffer logic, and ordering without
# overlap is just the serial code it replaced.


@pytest.mark.asyncio
async def test_yields_item_result_pairs_in_source_order():
    async def double(n: int) -> int:
        return n * 2

    out = await _collect(lookahead_map(_arange(10), double, max_inflight=4))
    assert out == [(n, n * 2) for n in range(10)]


@pytest.mark.asyncio
async def test_order_is_preserved_when_calls_finish_out_of_order():
    """The correctness guarantee: a fast later call must not overtake a slow earlier one.

    Item 0 is deliberately the slowest, so a naive as-completed implementation would yield it last.
    """

    async def variable_delay(n: int) -> str:
        await asyncio.sleep(0.05 if n == 0 else 0.001)
        return f"r{n}"

    out = await _collect(lookahead_map(_arange(5), variable_delay, max_inflight=5))
    assert [item for item, _ in out] == [0, 1, 2, 3, 4]
    assert [res for _, res in out] == ["r0", "r1", "r2", "r3", "r4"]


@pytest.mark.asyncio
async def test_calls_actually_overlap():
    """The regression guard: serial code would never show more than 1 concurrent call."""
    concurrent = 0
    peak = 0

    async def tracked(n: int) -> int:
        nonlocal concurrent, peak
        concurrent += 1
        peak = max(peak, concurrent)
        await asyncio.sleep(0.02)
        concurrent -= 1
        return n

    await _collect(lookahead_map(_arange(8), tracked, max_inflight=4))
    assert peak > 1, f"calls never overlapped (peak={peak}); this is the old serial behaviour"


@pytest.mark.asyncio
async def test_concurrency_is_bounded():
    """The embedder is a rate-limited API — a burst of speech must not become a burst of 429s."""
    concurrent = 0
    peak = 0

    async def tracked(n: int) -> int:
        nonlocal concurrent, peak
        concurrent += 1
        peak = max(peak, concurrent)
        await asyncio.sleep(0.01)
        concurrent -= 1
        return n

    await _collect(lookahead_map(_arange(50), tracked, max_inflight=3))
    # EXACTLY max_inflight, not "about" it — a slot is taken before the call starts and released
    # only once its result is consumed, so there is no slop to absorb a rate limit.
    assert peak <= 3, f"concurrency was not bounded: peak={peak}"
    assert peak > 1, "nothing overlapped, so the bound proves nothing"


@pytest.mark.asyncio
async def test_a_failing_call_surfaces_in_order():
    async def fails_on_two(n: int) -> int:
        if n == 2:
            raise RuntimeError("embed exploded")
        return n

    seen = []
    with pytest.raises(RuntimeError, match="embed exploded"):
        async for item, _ in lookahead_map(_arange(6), fails_on_two, max_inflight=4):
            seen.append(item)
    # 0 and 1 must have been delivered before the failure at 2.
    assert seen == [0, 1]


@pytest.mark.asyncio
async def test_a_failing_source_surfaces_after_preceding_items():
    async def failing_source():
        yield 0
        yield 1
        raise RuntimeError("stt exploded")

    async def identity(n: int) -> int:
        return n

    seen = []
    with pytest.raises(RuntimeError, match="stt exploded"):
        async for item, _ in lookahead_map(failing_source(), identity, max_inflight=4):
            seen.append(item)
    assert seen == [0, 1]


@pytest.mark.asyncio
async def test_early_exit_cancels_in_flight_calls():
    """Stopping a session must not leave embedding HTTP calls running with nobody watching.

    Measures completions AFTER the close, not total. Counting total would be meaningless: the first
    __anext__ has to wait for call 0, and the lookahead calls legitimately finish during that wait —
    an earlier version of this test failed for exactly that reason, not because of an orphan.
    """
    started = 0
    completed = 0

    async def slow(n: int) -> int:
        nonlocal started, completed
        started += 1
        await asyncio.sleep(0.05)
        completed += 1
        return n

    stream = lookahead_map(_arange(30), slow, max_inflight=4)
    await stream.__anext__()

    assert started > 1, "nothing was running ahead, so this test proves nothing"
    completed_at_close = completed
    await stream.aclose()
    # Well past the 0.05s call duration: anything still running would have finished by now.
    await asyncio.sleep(0.2)

    assert completed == completed_at_close, (
        f"{completed - completed_at_close} calls completed after the stream closed"
    )
    pending = [
        t for t in asyncio.all_tasks() if t.get_name() == "embed-lookahead-pump" and not t.done()
    ]
    assert not pending, "lookahead pump outlived the stream"


@pytest.mark.asyncio
async def test_empty_source_yields_nothing_and_calls_nothing():
    calls = 0

    async def counted(n: int) -> int:
        nonlocal calls
        calls += 1
        return n

    assert await _collect(lookahead_map(_arange(0), counted, max_inflight=4)) == []
    assert calls == 0


@pytest.mark.asyncio
async def test_inflight_of_one_is_the_serial_behaviour():
    """A documented escape hatch: 1 restores the old one-at-a-time behaviour."""
    concurrent = 0
    peak = 0

    async def tracked(n: int) -> int:
        nonlocal concurrent, peak
        concurrent += 1
        peak = max(peak, concurrent)
        await asyncio.sleep(0.005)
        concurrent -= 1
        return n

    out = await _collect(lookahead_map(_arange(6), tracked, max_inflight=1))
    assert [i for i, _ in out] == list(range(6))
    assert peak == 1, f"max_inflight=1 must be strictly serial, saw {peak} concurrent"


@pytest.mark.asyncio
async def test_rejects_a_useless_inflight_limit():
    async def identity(n: int) -> int:
        return n

    with pytest.raises(ValueError):
        await _collect(lookahead_map(_arange(1), identity, max_inflight=0))
