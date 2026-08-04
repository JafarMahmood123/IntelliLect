from __future__ import annotations

import asyncio
from datetime import timedelta
from uuid import uuid4

from app.application.services.ingestion_errors import (
    PermanentIngestionError,
    TransientIngestionError,
)
from app.application.services.ingestion_service import IngestionJob
from app.application.services.stale_recovery_service import StaleRecoveryService
from app.domain.entities.chunk import Chunk
from app.domain.entities.document import Document
from app.domain.enums.chunk_source import ChunkSource
from app.domain.enums.document_status import DocumentStatus
from app.infrastructure.config.settings import Settings

from tests.ingestion.fakes import (
    FakeClock,
    FakeEmbeddingProvider,
    FakeFileStorage,
    InMemoryChunkRepository,
    InMemoryDocumentRepository,
    RaisingEmbeddingProvider,
    build_ingestion_service,
    make_pdf_bytes,
)

S3_KEY = "classroom/doc.pdf"
DIM = Settings().embedding_dim


def _job() -> IngestionJob:
    return IngestionJob(uuid4(), uuid4(), S3_KEY, "doc.pdf", "application/pdf")


def _pending_doc(job: IngestionJob, **overrides) -> Document:
    fields: dict = dict(
        classroom_id=job.classroom_id,
        file_id=job.file_id,
        s3_key=job.s3_key,
        file_name=job.file_name,
        content_type=job.content_type,
        status=DocumentStatus.PENDING,
    )
    fields.update(overrides)
    return Document(**fields)


# --- 1. Concurrency-safe claiming ----------------------------------------------


async def test_concurrent_claims_only_one_processes() -> None:
    job = _job()
    documents = InMemoryDocumentRepository()
    documents.seed(_pending_doc(job))
    embedder = FakeEmbeddingProvider(DIM)
    chunks = InMemoryChunkRepository()
    service = build_ingestion_service(
        storage=FakeFileStorage({S3_KEY: make_pdf_bytes(1)}),
        embedder=embedder, documents=documents, chunks=chunks, clock=FakeClock(),
    )

    results = await asyncio.gather(service.ingest(job), service.ingest(job))

    # Exactly one winner processed; the other exited cleanly (skipped).
    assert [r.skipped for r in results].count(False) == 1
    assert [r.skipped for r in results].count(True) == 1
    assert embedder.embed_documents_calls == 1
    assert chunks.replace_calls == 1
    stored = await documents.get_by_file_id(job.file_id)
    assert stored.status == DocumentStatus.DONE
    assert stored.attempts == 1  # only one claim incremented attempts


# --- 2. Transient vs permanent + retry/backoff ---------------------------------


async def test_transient_failure_retries_with_backoff_then_fails() -> None:
    job = _job()
    documents = InMemoryDocumentRepository()
    documents.seed(_pending_doc(job))
    chunks = InMemoryChunkRepository()
    clock = FakeClock()
    service = build_ingestion_service(
        storage=FakeFileStorage({S3_KEY: make_pdf_bytes(1)}),
        embedder=RaisingEmbeddingProvider(DIM, TransientIngestionError("ollama down")),
        documents=documents, chunks=chunks, clock=clock,
        max_attempts=3, retry_base=2.0, retry_max=30.0,
    )

    # Attempt 1 -> transient retry with base delay.
    r1 = await service.ingest(job)
    assert r1.retry is True
    assert r1.status == DocumentStatus.PENDING
    assert r1.attempts == 1
    assert r1.retry_delay_seconds == 2.0  # base * 2^0
    reloaded = await documents.get_by_file_id(job.file_id)
    assert reloaded.status == DocumentStatus.PENDING
    assert reloaded.attempts == 1
    assert reloaded.last_error is not None

    # Attempt 2 -> retry with doubled delay.
    r2 = await service.ingest(job)
    assert r2.retry is True
    assert r2.attempts == 2
    assert r2.retry_delay_seconds == 4.0  # base * 2^1

    # Attempt 3 -> budget exhausted -> permanently Failed, no more retries.
    r3 = await service.ingest(job)
    assert r3.retry is False
    assert r3.status == DocumentStatus.FAILED
    assert r3.attempts == 3
    stored = await documents.get_by_file_id(job.file_id)
    assert stored.status == DocumentStatus.FAILED
    assert stored.attempts == 3
    assert stored.last_error is not None
    assert chunks.replace_calls == 0  # never persisted


async def test_backoff_is_capped() -> None:
    job = _job()
    documents = InMemoryDocumentRepository()
    documents.seed(_pending_doc(job, attempts=5))  # high attempt count
    service = build_ingestion_service(
        storage=FakeFileStorage({S3_KEY: make_pdf_bytes(1)}),
        embedder=RaisingEmbeddingProvider(DIM, TransientIngestionError("x")),
        documents=documents, chunks=InMemoryChunkRepository(), clock=FakeClock(),
        max_attempts=10, retry_base=2.0, retry_max=30.0,
    )

    result = await service.ingest(job)  # claim -> attempts becomes 6

    assert result.retry is True
    # base * 2^(6-1) = 64, capped to 30.
    assert result.retry_delay_seconds == 30.0


async def test_permanent_failure_fails_immediately() -> None:
    job = _job()
    documents = InMemoryDocumentRepository()
    documents.seed(_pending_doc(job))
    chunks = InMemoryChunkRepository()
    service = build_ingestion_service(
        storage=FakeFileStorage({S3_KEY: make_pdf_bytes(1)}),
        embedder=RaisingEmbeddingProvider(DIM, PermanentIngestionError("bad file")),
        documents=documents, chunks=chunks, clock=FakeClock(), max_attempts=3,
    )

    result = await service.ingest(job)

    assert result.retry is False
    assert result.status == DocumentStatus.FAILED
    assert result.attempts == 1  # no retries
    assert chunks.replace_calls == 0


async def test_corrupt_file_is_permanent() -> None:
    # A corrupt PDF surfaces as an ExtractionError -> classified permanent -> Failed.
    job = _job()
    documents = InMemoryDocumentRepository()
    documents.seed(_pending_doc(job))
    service = build_ingestion_service(
        storage=FakeFileStorage({S3_KEY: b"%PDF-1.4 not a real pdf"}),
        embedder=FakeEmbeddingProvider(DIM),
        documents=documents, chunks=InMemoryChunkRepository(), clock=FakeClock(),
    )

    result = await service.ingest(job)

    assert result.retry is False
    assert result.status == DocumentStatus.FAILED
    assert result.attempts == 1


# --- 3. Atomic chunk writes ----------------------------------------------------


async def test_failed_chunk_write_is_atomic_and_not_marked_done() -> None:
    job = _job()
    documents = InMemoryDocumentRepository()
    documents.seed(_pending_doc(job, content_hash="0" * 64))
    doc_id = (await documents.get_by_file_id(job.file_id)).id
    chunks = InMemoryChunkRepository(fail_replace=True)
    chunks.by_document[doc_id] = [
        (
            Chunk(document_id=doc_id, classroom_id=job.classroom_id, chunk_index=0,
                  text="prior", source=ChunkSource.TEXT),
            [0.0] * DIM,
        )
    ]
    service = build_ingestion_service(
        storage=FakeFileStorage({S3_KEY: make_pdf_bytes(2)}),  # new content
        embedder=FakeEmbeddingProvider(DIM), documents=documents, chunks=chunks,
        clock=FakeClock(),
    )

    result = await service.ingest(job)

    # The persist failed, so the document is NOT Done and the prior chunks are intact.
    assert result.status != DocumentStatus.DONE
    assert [chunk.text for chunk, _ in chunks.by_document[doc_id]] == ["prior"]
    stored = await documents.get_by_file_id(job.file_id)
    assert stored.status != DocumentStatus.DONE


# --- 4. Stale-processing recovery ----------------------------------------------


async def test_stale_processing_is_reset_and_returned() -> None:
    clock = FakeClock()
    documents = InMemoryDocumentRepository()

    stale_job, fresh_job = _job(), _job()
    documents.seed(
        _pending_doc(stale_job, status=DocumentStatus.PROCESSING, attempts=2,
                     processing_started_at=clock.now() - timedelta(minutes=60))
    )
    documents.seed(
        _pending_doc(fresh_job, status=DocumentStatus.PROCESSING, attempts=1,
                     processing_started_at=clock.now())
    )

    recovery = StaleRecoveryService(documents, clock=clock, stale_minutes=15)
    recovered = await recovery.recover()

    assert [doc.file_id for doc in recovered] == [stale_job.file_id]
    stale = await documents.get_by_file_id(stale_job.file_id)
    assert stale.status == DocumentStatus.PENDING
    assert stale.attempts == 2  # attempts preserved on recovery
    assert stale.processing_started_at is None
    fresh = await documents.get_by_file_id(fresh_job.file_id)
    assert fresh.status == DocumentStatus.PROCESSING  # not stale, untouched
