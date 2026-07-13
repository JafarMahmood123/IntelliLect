from __future__ import annotations

import asyncio

from app.application.services.ingestion_service import IngestionJob
from app.application.services.ingestion_worker import IngestionWorker

from tests.ingestion.fakes import make_job


async def test_worker_processes_enqueued_jobs() -> None:
    processed: list[IngestionJob] = []

    async def handler(job: IngestionJob) -> None:
        processed.append(job)

    worker = IngestionWorker(handler, concurrency=2, queue_max=10)
    await worker.start()
    jobs = [make_job() for _ in range(5)]
    for job in jobs:
        assert worker.enqueue(job) is True
    await worker.join()
    await worker.stop()

    assert {job.file_id for job in processed} == {job.file_id for job in jobs}


async def test_worker_survives_a_failing_job() -> None:
    processed: list[IngestionJob] = []
    bad = make_job()
    good = make_job()

    async def handler(job: IngestionJob) -> None:
        if job.file_id == bad.file_id:
            raise RuntimeError("boom")
        processed.append(job)

    worker = IngestionWorker(handler, concurrency=1, queue_max=10)
    await worker.start()
    assert worker.enqueue(bad) is True
    assert worker.enqueue(good) is True
    await worker.join()
    await worker.stop()

    # The bad job did not kill the worker; the next job still ran.
    assert [job.file_id for job in processed] == [good.file_id]


async def test_full_queue_is_rejected() -> None:
    async def handler(job: IngestionJob) -> None:  # never invoked (no start)
        await asyncio.sleep(0)

    worker = IngestionWorker(handler, concurrency=1, queue_max=2)
    # No consumers started, so the queue fills and then rejects.
    assert worker.enqueue(make_job()) is True
    assert worker.enqueue(make_job()) is True
    assert worker.enqueue(make_job()) is False
