from __future__ import annotations

import asyncio

from app.application.services.ingestion_service import IngestionJob, IngestionResult
from app.application.services.ingestion_worker import IngestionWorker
from app.domain.enums.document_status import DocumentStatus

from tests.ingestion.fakes import FakeClock, make_job


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


async def test_worker_reenqueues_after_transient_retry() -> None:
    processed: list[IngestionJob] = []
    calls = {"n": 0}
    job = make_job()
    done = asyncio.Event()

    async def handler(j: IngestionJob) -> IngestionResult:
        calls["n"] += 1
        if calls["n"] == 1:
            # First run: transient failure -> ask the worker to retry after 5s.
            return IngestionResult(
                file_id=j.file_id, status=DocumentStatus.PENDING,
                retry=True, retry_delay_seconds=5.0, attempts=1,
            )
        processed.append(j)
        done.set()
        return IngestionResult(file_id=j.file_id, status=DocumentStatus.DONE, attempts=2)

    clock = FakeClock()
    worker = IngestionWorker(handler, concurrency=1, queue_max=10, clock=clock)
    await worker.start()
    assert worker.enqueue(job) is True

    await asyncio.wait_for(done.wait(), timeout=2)
    await worker.stop()

    # The job was re-enqueued and processed on the second pass, after the backoff delay.
    assert [j.file_id for j in processed] == [job.file_id]
    assert clock.sleeps == [5.0]
    assert calls["n"] == 2
