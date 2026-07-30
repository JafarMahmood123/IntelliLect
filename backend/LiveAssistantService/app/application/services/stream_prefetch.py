"""Decouple a producing async iterator from a slow consumer via a bounded queue.

WHY THIS EXISTS. An async generator only advances when its consumer pulls from it, so a chain
like ``STT -> BoundaryDetector`` runs the two stages STRICTLY ALTERNATELY: while the boundary
detector awaits its per-segment embedding round trip, the STT generator is suspended and no
audio is being transcribed at all. Measured on a real session, that made every segment cost
STT (~2.0s) + embed (~1.8s) back to back — ~3.8s of serial work per window, against windows that
are often only ~5s of speech. At that ratio the pipeline is running at ~0.76x realtime and a
burst of short utterances pushes it past 1.0x, where the audio queue grows without bound and
feedback falls further behind the lecture the longer the class runs.

Interposing this buffer lets the two stages OVERLAP: a pump task keeps draining the producer
into a queue while the consumer is busy, so window N+1 is being transcribed while segment N is
being embedded. The per-segment critical path is unchanged (the last segment of an idea still
costs STT then embed before the idea can close) — what this removes is the compounding backlog.

The queue is BOUNDED on purpose: an unbounded one would trade a latency problem for a memory
problem, silently buffering an entire lecture. When it fills, ``await queue.put`` applies
backpressure to the producer, which is the same behaviour as today — no worse, and visible.
"""

from __future__ import annotations

import asyncio
import contextlib

from collections.abc import AsyncIterator, Awaitable, Callable
from typing import TypeVar

T = TypeVar("T")
R = TypeVar("R")

# Sentinel for "the producer finished normally". A dedicated object (not None) so a legitimate
# None item could never be mistaken for end-of-stream.
_DONE = object()


async def prefetch(source: AsyncIterator[T], max_buffered: int) -> AsyncIterator[T]:
    """Yield items from ``source``, keeping up to ``max_buffered`` read ahead.

    Ordering is preserved exactly — this changes WHEN items are produced, never their sequence
    or content, so it is transparent to the boundary detector's logic.

    Failure semantics match a plain ``async for`` over ``source``: an exception raised by the
    producer is re-raised here, in order, after every item that preceded it has been yielded.
    Cancellation of the consumer cancels and awaits the pump, so no orphan task survives the
    session — the pipeline's ``stop()`` relies on that.
    """
    if max_buffered < 1:
        raise ValueError("max_buffered must be at least 1")

    queue: asyncio.Queue = asyncio.Queue(maxsize=max_buffered)

    async def pump() -> None:
        try:
            async for item in source:
                await queue.put(item)
        except asyncio.CancelledError:
            raise  # teardown, not a stream fault — never forward it as one
        except Exception as exc:  # noqa: BLE001 — forwarded to the consumer verbatim
            await queue.put(exc)
        else:
            await queue.put(_DONE)

    task = asyncio.create_task(pump(), name="stt-prefetch-pump")
    try:
        while True:
            item = await queue.get()
            if item is _DONE:
                return
            if isinstance(item, BaseException):
                raise item
            yield item
    finally:
        task.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await task
        # Release the generator the pump was iterating; without this an early consumer exit
        # (session stop mid-stream) would leave the STT generator un-closed until GC.
        with contextlib.suppress(Exception):
            aclose = getattr(source, "aclose", None)
            if aclose is not None:
                await aclose()


async def lookahead_map(
    source: AsyncIterator[T],
    fn: Callable[[T], Awaitable[R]],
    max_inflight: int,
) -> AsyncIterator[tuple[T, R]]:
    """Yield ``(item, await fn(item))`` IN ORDER, with several ``fn`` calls running concurrently.

    WHY THIS EXISTS. The boundary detector needs one embedding per transcript segment, and awaiting
    them one at a time made the embedding round trip the pipeline's dominant cost: measured on a
    real session, 90.6s of embedding inside a 143s lecture, a 2.01s median against a 3.05s median
    gap between segments — 61% occupancy, with a 1.39s minimum gap and a 10.5s outlier. At that
    ratio a burst of quick segments accumulates lag, and one slow call stalls everything behind it.

    The embeddings are independent of each other — segment N's vector does not depend on N-1's — so
    they can overlap. The DECISIONS cannot: whether segment N starts a new idea depends on the
    buffer state left by N-1. This yields results strictly in source order, so the consumer's
    sequential logic is untouched while the waiting happens in parallel.

    ``max_inflight`` bounds concurrency, which matters for a rate-limited API as much as for memory:
    an unbounded fan-out would turn a burst of speech into a burst of 429s.
    """
    if max_inflight < 1:
        raise ValueError("max_inflight must be at least 1")

    # A semaphore, not just a bounded queue, is what makes max_inflight EXACT. Bounding the queue
    # alone leaks two extra concurrent calls: the one the consumer is awaiting (already dequeued)
    # plus the one the pump created immediately before blocking on a full put. For a rate-limited
    # API the difference between "4" and "4 plus slop" is the difference between a documented
    # ceiling and a guess, so a slot is taken BEFORE the call starts and released only once its
    # result has been consumed.
    slots = asyncio.Semaphore(max_inflight)
    queue: asyncio.Queue = asyncio.Queue(maxsize=max_inflight)
    inflight: set[asyncio.Task] = set()

    async def pump() -> None:
        try:
            async for item in source:
                await slots.acquire()
                # create_task starts the call immediately; we deliberately do NOT await it here.
                task = asyncio.create_task(fn(item))
                inflight.add(task)
                await queue.put((item, task))
        except asyncio.CancelledError:
            raise
        except Exception as exc:  # noqa: BLE001 — forwarded to the consumer in order
            await queue.put(exc)
        else:
            await queue.put(_DONE)

    pump_task = asyncio.create_task(pump(), name="embed-lookahead-pump")
    try:
        while True:
            entry = await queue.get()
            if entry is _DONE:
                return
            if isinstance(entry, BaseException):
                raise entry
            item, task = entry
            try:
                # In-order await: a later call that finished first still waits its turn.
                result = await task
            finally:
                inflight.discard(task)
                slots.release()
            yield item, result
    finally:
        pump_task.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await pump_task
        # Cancel work whose result will never be consumed. Without this, an early exit (session
        # stop mid-lecture) leaves embedding HTTP calls running with nobody to observe their
        # failure. Drain the queue too — the pump may have queued tasks we never dequeued.
        while not queue.empty():
            entry = queue.get_nowait()
            if isinstance(entry, tuple):
                inflight.add(entry[1])
        for task in inflight:
            task.cancel()
        inflight.clear()
        with contextlib.suppress(Exception):
            aclose = getattr(source, "aclose", None)
            if aclose is not None:
                await aclose()
