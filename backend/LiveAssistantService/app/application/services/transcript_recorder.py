"""Off-hot-path, non-fatal transcript writer for one session (S-0).

Wraps a ``TranscriptRepository`` for a single session so the live loop can persist
FINAL transcript segments WITHOUT paying DB latency on the feedback path and WITHOUT
ever letting a persistence failure break the session:

  * ``record(segment)`` only *enqueues* — it never awaits the store and never raises.
  * A single background worker drains the queue in FIFO order and appends segments,
    so ``order_index`` follows the exact order the teacher spoke.
  * Every store error (ensure/append/finalize) is caught and logged at WARNING; a lost
    segment must never stop feedback.

One recorder instance per session (it is stateful): ``start`` → many ``record`` →
``finalize``. Pure application logic — depends only on the repository port + domain.
"""

from __future__ import annotations

import asyncio
import logging
from uuid import UUID

from app.application.ports.transcript_repository import TranscriptRepository
from app.domain.transcript.transcript_segment import TranscriptSegment

logger = logging.getLogger("liveassistant.transcript")

# Sentinel enqueued by finalize() to tell the worker to flush and exit.
_STOP = object()


class TranscriptRecorder:
    def __init__(
        self,
        repository: TranscriptRepository,
        session_id: UUID,
        classroom_id: UUID,
        *,
        batch: int = 1,
    ) -> None:
        self._repo = repository
        self._session_id = session_id
        self._classroom_id = classroom_id
        self._batch = max(1, batch)
        self._queue: asyncio.Queue = asyncio.Queue()
        self._worker: asyncio.Task | None = None

    async def start(self) -> None:
        """Ensure the session's transcript exists and launch the background writer.

        Non-fatal: an ``ensure_session`` failure is logged and the worker still starts,
        so later appends are attempted (and the endpoint degrades gracefully) rather
        than taking down the session.
        """
        if self._worker is not None:
            return  # idempotent
        try:
            await self._repo.ensure_session(self._session_id, self._classroom_id)
        except Exception as exc:  # noqa: BLE001 — persistence must never break the session
            logger.warning(
                "transcript_ensure_failed",
                extra={"session_id": str(self._session_id), "error_type": type(exc).__name__},
            )
        self._worker = asyncio.create_task(
            self._run(), name=f"transcript-writer-{self._session_id}"
        )

    def record(self, segment: TranscriptSegment) -> None:
        """Enqueue a FINAL segment for persistence. Non-blocking; never raises.

        Interim segments must be filtered out by the caller; as a guard, non-final
        segments are ignored here too so nothing unstable is ever stored.
        """
        if not segment.is_final:
            return
        if self._worker is None:
            # Not started (or already finalized) — drop rather than raise on the hot path.
            logger.warning(
                "transcript_record_before_start", extra={"session_id": str(self._session_id)}
            )
            return
        self._queue.put_nowait(segment)

    async def finalize(self) -> None:
        """Drain pending segments, stop the worker, and mark the transcript Finalized.

        Idempotent and non-fatal. Safe to await from the pipeline's teardown.
        """
        worker = self._worker
        if worker is not None:
            self._queue.put_nowait(_STOP)
            await worker  # the worker swallows its own errors, so this never raises
            self._worker = None
        try:
            await self._repo.finalize(self._session_id)
        except Exception as exc:  # noqa: BLE001
            logger.warning(
                "transcript_finalize_failed",
                extra={"session_id": str(self._session_id), "error_type": type(exc).__name__},
            )

    async def _run(self) -> None:
        pending: list[TranscriptSegment] = []
        stopping = False
        while True:
            item = await self._queue.get()
            if item is _STOP:
                stopping = True
            else:
                pending.append(item)
            # Flush when the batch is full, or on stop to drain the tail.
            if pending and (stopping or len(pending) >= self._batch):
                await self._flush(pending)
                pending = []
            if stopping:
                return

    async def _flush(self, segments: list[TranscriptSegment]) -> None:
        """Append buffered segments in order; a failure drops that segment, never raises."""
        for segment in segments:
            try:
                await self._repo.append_segment(self._session_id, segment)
            except Exception as exc:  # noqa: BLE001 — a lost segment must not stop feedback
                logger.warning(
                    "transcript_append_failed",
                    extra={
                        "session_id": str(self._session_id),
                        "error_type": type(exc).__name__,
                    },
                )
