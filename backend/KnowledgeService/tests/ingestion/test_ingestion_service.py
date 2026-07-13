from __future__ import annotations

import hashlib
from uuid import uuid4

from app.application.services.ingestion_service import IngestionJob
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
    build_ingestion_service,
    make_pdf_bytes,
)

S3_KEY = "classroom/doc.pdf"
DIM = Settings().embedding_dim


def _seed_pending(documents: InMemoryDocumentRepository, job: IngestionJob) -> None:
    documents.seed(
        Document(
            classroom_id=job.classroom_id,
            file_id=job.file_id,
            s3_key=job.s3_key,
            file_name=job.file_name,
            content_type=job.content_type,
            status=DocumentStatus.PENDING,
        )
    )


def _job() -> IngestionJob:
    return IngestionJob(uuid4(), uuid4(), S3_KEY, "doc.pdf", "application/pdf")


async def test_happy_path_pending_processing_done_with_embeddings() -> None:
    job = _job()
    storage = FakeFileStorage({S3_KEY: make_pdf_bytes(1)})
    embedder = FakeEmbeddingProvider(DIM)
    documents = InMemoryDocumentRepository()
    chunks = InMemoryChunkRepository()
    _seed_pending(documents, job)
    service = build_ingestion_service(
        storage=storage, embedder=embedder, documents=documents, chunks=chunks,
        clock=FakeClock(),
    )

    outcome = await service.ingest(job)

    assert outcome.status == DocumentStatus.DONE
    assert outcome.chunk_count > 0
    assert outcome.attempts == 1
    assert documents.status_history[job.file_id] == [
        DocumentStatus.PENDING,
        DocumentStatus.PROCESSING,
        DocumentStatus.DONE,
    ]

    stored = await documents.get_by_file_id(job.file_id)
    assert stored.status == DocumentStatus.DONE
    assert stored.content_hash is not None and len(stored.content_hash) == 64
    assert stored.attempts == 1
    assert stored.last_error is None

    persisted = chunks.by_document[stored.id]
    assert len(persisted) == outcome.chunk_count
    assert [chunk.chunk_index for chunk, _ in persisted] == list(range(len(persisted)))
    for _, embedding in persisted:
        assert len(embedding) == DIM
    assert chunks.replace_calls == 1


async def test_duplicate_job_on_done_document_is_a_noop() -> None:
    # A Done document is not claimable -> the duplicate exits without reprocessing.
    job = _job()
    content_hash = hashlib.sha256(make_pdf_bytes(1)).hexdigest()
    documents = InMemoryDocumentRepository()
    documents.seed(
        Document(
            classroom_id=job.classroom_id, file_id=job.file_id, s3_key=job.s3_key,
            file_name=job.file_name, content_type=job.content_type,
            status=DocumentStatus.DONE, content_hash=content_hash,
        )
    )
    embedder = FakeEmbeddingProvider(DIM)
    chunks = InMemoryChunkRepository()
    service = build_ingestion_service(
        storage=FakeFileStorage({S3_KEY: make_pdf_bytes(1)}),
        embedder=embedder, documents=documents, chunks=chunks, clock=FakeClock(),
    )

    outcome = await service.ingest(job)

    assert outcome.skipped is True
    assert outcome.status == DocumentStatus.DONE
    assert embedder.embed_documents_calls == 0
    assert chunks.replace_calls == 0


async def test_unchanged_hash_on_reclaimed_document_keeps_chunks() -> None:
    # Simulate a re-run of a Pending doc whose stored hash matches the file (e.g. a
    # reindex of unchanged content): keep existing chunks, just mark Done, no re-embed.
    job = _job()
    pdf = make_pdf_bytes(1)  # generate once so the stored hash matches the file exactly
    content_hash = hashlib.sha256(pdf).hexdigest()
    documents = InMemoryDocumentRepository()
    documents.seed(
        Document(
            classroom_id=job.classroom_id, file_id=job.file_id, s3_key=job.s3_key,
            file_name=job.file_name, content_type=job.content_type,
            status=DocumentStatus.PENDING, content_hash=content_hash,
        )
    )
    embedder = FakeEmbeddingProvider(DIM)
    chunks = InMemoryChunkRepository()
    service = build_ingestion_service(
        storage=FakeFileStorage({S3_KEY: pdf}),
        embedder=embedder, documents=documents, chunks=chunks, clock=FakeClock(),
    )

    outcome = await service.ingest(job)

    assert outcome.skipped is True
    assert outcome.status == DocumentStatus.DONE
    assert embedder.embed_documents_calls == 0
    assert chunks.replace_calls == 0
    stored = await documents.get_by_file_id(job.file_id)
    assert stored.status == DocumentStatus.DONE


async def test_changed_hash_reindexes_and_replaces_chunks() -> None:
    job = _job()
    old_hash = "0" * 64
    documents = InMemoryDocumentRepository()
    documents.seed(
        Document(
            classroom_id=job.classroom_id, file_id=job.file_id, s3_key=job.s3_key,
            file_name=job.file_name, content_type=job.content_type,
            status=DocumentStatus.PENDING, content_hash=old_hash,
        )
    )
    chunks = InMemoryChunkRepository()
    # Pre-existing chunk from a prior index.
    doc_id = (await documents.get_by_file_id(job.file_id)).id
    chunks.by_document[doc_id] = [
        (Chunk(document_id=doc_id, classroom_id=job.classroom_id, chunk_index=0,
               text="old", source=ChunkSource.TEXT), [0.0] * DIM)
    ]
    service = build_ingestion_service(
        storage=FakeFileStorage({S3_KEY: make_pdf_bytes(2)}),  # new content -> new hash
        embedder=FakeEmbeddingProvider(DIM), documents=documents, chunks=chunks,
        clock=FakeClock(),
    )

    outcome = await service.ingest(job)

    assert outcome.status == DocumentStatus.DONE
    assert outcome.skipped is False
    assert chunks.replace_calls == 1
    new_chunks = chunks.by_document[doc_id]
    assert all(chunk.text != "old" for chunk, _ in new_chunks)  # replaced, not appended
    stored = await documents.get_by_file_id(job.file_id)
    assert stored.content_hash != old_hash
