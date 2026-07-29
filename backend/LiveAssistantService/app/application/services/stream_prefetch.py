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

from collections.abc import AsyncIterator
from typing import TypeVar

T = TypeVar("T")

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
