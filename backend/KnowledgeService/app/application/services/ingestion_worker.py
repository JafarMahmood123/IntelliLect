from __future__ import annotations

import asyncio
import contextlib
import logging
from collections.abc import Awaitable, Callable

from app.application.services.clock import Clock, SystemClock
from app.application.services.ingestion_service import IngestionJob, IngestionResult
from app.observability import metrics

logger = logging.getLogger("knowledge.ingestion.worker")

# The handler returns the ingest outcome so the worker can schedule a retry.
JobHandler = Callable[[IngestionJob], Awaitable[IngestionResult | None]]


class IngestionWorker:
    """A bounded in-process job queue with a fixed pool of worker tasks.

    Decoupled from persistence: it is handed a `handler` coroutine that runs one job
    (open a DB session, build the IngestionService, commit) and returns its
    IngestionResult. The queue is bounded, so `enqueue` returns False when full — the
    endpoint turns that into 503. A failing handler is logged and swallowed so one bad
    job never kills a worker. When a handler reports a transient failure (retry=True),
    the worker re-enqueues the job after the requested backoff delay (via the clock,
    so tests control timing without real sleeps).
    """

    def __init__(
        self,
        handler: JobHandler,
        concurrency: int,
        queue_max: int,
        clock: Clock | None = None,
    ) -> None:
        self._handler = handler
        self._concurrency = max(1, concurrency)
        self._queue: asyncio.Queue[IngestionJob] = asyncio.Queue(maxsize=max(1, queue_max))
        self._clock = clock or SystemClock()
        self._tasks: list[asyncio.Task[None]] = []
        self._retry_tasks: set[asyncio.Task[None]] = set()
        self._started = False

    def enqueue(self, job: IngestionJob) -> bool:
        """Try to enqueue a job. Returns False if the queue is full."""
        try:
            self._queue.put_nowait(job)
            metrics.set_queue_depth(self._queue.qsize())
            return True
        except asyncio.QueueFull:
            logger.warning("Ingestion queue full; rejecting job %s.", job.file_id)
            return False

    def queue_depth(self) -> int:
        """Current number of queued (not-yet-started) jobs."""
        return self._queue.qsize()

    def is_running(self) -> bool:
        return self._started and any(not task.done() for task in self._tasks)

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
            metrics.set_queue_depth(self._queue.qsize())
            try:
                result = await self._handler(job)
                if result is not None and result.retry:
                    self._schedule_retry(job, result.retry_delay_seconds)
            except asyncio.CancelledError:
                self._queue.task_done()
                raise
            except Exception:  # noqa: BLE001 — keep the worker alive across bad jobs
                logger.exception("Ingestion worker %d: job %s failed", worker_id, job.file_id)
                self._queue.task_done()
            else:
                self._queue.task_done()

    def _schedule_retry(self, job: IngestionJob, delay_seconds: float) -> None:
        task = asyncio.create_task(self._delayed_reenqueue(job, delay_seconds))
        self._retry_tasks.add(task)
        task.add_done_callback(self._retry_tasks.discard)

    async def _delayed_reenqueue(self, job: IngestionJob, delay_seconds: float) -> None:
        try:
            await self._clock.sleep(delay_seconds)
        except asyncio.CancelledError:
            return
        if not self.enqueue(job):
            logger.warning(
                "Could not re-enqueue retry for %s (queue full); left Pending.",
                job.file_id,
            )

    async def join(self) -> None:
        """Wait until every queued job has been processed (used by tests)."""
        await self._queue.join()

    async def stop(self) -> None:
        """Cancel worker + pending-retry tasks and wait for them to unwind cleanly."""
        for task in (*self._tasks, *self._retry_tasks):
            task.cancel()
        for task in (*self._tasks, *self._retry_tasks):
            with contextlib.suppress(asyncio.CancelledError):
                await task
        self._tasks = []
        self._retry_tasks.clear()
        self._started = False
        logger.info("Stopped ingestion workers.")
