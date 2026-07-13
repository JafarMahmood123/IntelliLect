from __future__ import annotations

import asyncio
import contextlib
import logging
from collections.abc import Awaitable, Callable

from app.application.services.ingestion_service import IngestionJob

logger = logging.getLogger("knowledge.ingestion.worker")

JobHandler = Callable[[IngestionJob], Awaitable[None]]


class IngestionWorker:
    """A bounded in-process job queue with a fixed pool of worker tasks.

    Decoupled from persistence: it is handed a `handler` coroutine that knows how to
    run one job (open a DB session, build the IngestionService, commit). The queue is
    bounded, so `enqueue` returns False when full — the endpoint turns that into 503.
    A failing handler is logged and swallowed so one bad job never kills a worker.
    """

    def __init__(self, handler: JobHandler, concurrency: int, queue_max: int) -> None:
        self._handler = handler
        self._concurrency = max(1, concurrency)
        self._queue: asyncio.Queue[IngestionJob] = asyncio.Queue(maxsize=max(1, queue_max))
        self._tasks: list[asyncio.Task[None]] = []
        self._started = False

    def enqueue(self, job: IngestionJob) -> bool:
        """Try to enqueue a job. Returns False if the queue is full."""
        try:
            self._queue.put_nowait(job)
            return True
        except asyncio.QueueFull:
            logger.warning("Ingestion queue full; rejecting job %s.", job.file_id)
            return False

    async def start(self) -> None:
        if self._started:
            return
        self._started = True
        self._tasks = [
            asyncio.create_task(self._run(i), name=f"ingest-worker-{i}")
            for i in range(self._concurrency)
        ]
        logger.info("Started %d ingestion worker(s).", self._concurrency)

    async def _run(self, worker_id: int) -> None:
        while True:
            job = await self._queue.get()
            try:
                await self._handler(job)
            except asyncio.CancelledError:
                self._queue.task_done()
                raise
            except Exception:  # noqa: BLE001 — keep the worker alive across bad jobs
                logger.exception("Ingestion worker %d: job %s failed", worker_id, job.file_id)
                self._queue.task_done()
            else:
                self._queue.task_done()

    async def join(self) -> None:
        """Wait until every queued job has been processed (used by tests)."""
        await self._queue.join()

    async def stop(self) -> None:
        """Cancel worker tasks and wait for them to unwind cleanly."""
        for task in self._tasks:
            task.cancel()
        for task in self._tasks:
            with contextlib.suppress(asyncio.CancelledError):
                await task
        self._tasks = []
        self._started = False
        logger.info("Stopped ingestion workers.")
