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


async def test_a_document_that_yields_no_text_is_failed_not_marked_indexed() -> None:
    """A false success is worse than the failure it hides (test-plan E-06, E-13).

    Nothing stopped an empty extraction before this: zero chunks, zero embeddings, an
    atomic replace with an empty list, and Done. The teacher's file list then showed the
    document as indexed, the assistant could never retrieve a word of it, and no status,
    log or error said why. A FAILED row gets looked at; a DONE row with nothing behind it
    does not.

    Driven with a blank text file because that is the reachable case that has nothing to do
    with mislabelling — a scanned PDF with OCR unavailable produces the same nothing, and so
    did an image sent as application/pdf before the router learned to sniff.
    """
    job = IngestionJob(uuid4(), uuid4(), "classroom/blank.txt", "blank.txt", "text/plain")
    storage = FakeFileStorage({"classroom/blank.txt": b"   \n\n\t\n"})
    embedder = FakeEmbeddingProvider(DIM)
    documents = InMemoryDocumentRepository()
    chunks = InMemoryChunkRepository()
    _seed_pending(documents, job)
    service = build_ingestion_service(
        storage=storage, embedder=embedder, documents=documents, chunks=chunks,
        clock=FakeClock(),
    )

    outcome = await service.ingest(job)

    assert outcome.status == DocumentStatus.FAILED
    # Permanent, so it is not retried: the same bytes yield the same nothing, and retrying
    # only delays telling somebody. One attempt, straight to Failed.
    assert outcome.retry is False
    assert outcome.attempts == 1
    assert documents.status_history[job.file_id] == [
        DocumentStatus.PENDING,
        DocumentStatus.PROCESSING,
        DocumentStatus.FAILED,
    ]

    stored = await documents.get_by_file_id(job.file_id)
    assert stored.last_error is not None
    assert "nothing to index" in stored.last_error

    # And nothing was written: no empty replace, so a previously-indexed version of this
    # document is not wiped out by a re-upload that turned out to be unreadable.
    assert chunks.replace_calls == 0


async def test_an_oversized_object_is_refused_without_being_downloaded() -> None:
    """The size cap, and the half that makes it worth having (test-plan E-08).

    There was no cap at all. `get_bytes` is `response["Body"].read()` — the whole object into
    memory in one call — and ingestion takes an `s3_key` rather than an upload, so nothing on
    this side had ever asked how big the thing on the end of that key is. ClassroomService's
    50 MB limit is enforced at the door it owns; this service is reached through a different
    one (the internal ingest route, and re-index sweeps over keys already in the bucket).

    The assertion that matters is `storage.calls == 0`. A check placed after the download would
    pass a test that only looked at the status, while having already spent exactly the memory it
    exists to protect.
    """
    job = IngestionJob(uuid4(), uuid4(), S3_KEY, "enormous.pdf", "application/pdf")
    storage = FakeFileStorage({S3_KEY: make_pdf_bytes(1)}, sizes={S3_KEY: 512 * 1024 * 1024})
    embedder = FakeEmbeddingProvider(DIM)
    documents = InMemoryDocumentRepository()
    chunks = InMemoryChunkRepository()
    _seed_pending(documents, job)
    service = build_ingestion_service(
        storage=storage, embedder=embedder, documents=documents, chunks=chunks,
        clock=FakeClock(),
    )

    outcome = await service.ingest(job)

    assert outcome.status == DocumentStatus.FAILED
    assert storage.calls == 0, "the object was downloaded despite being over the limit"
    assert storage.size_calls == 1

    # Permanent: the object will not shrink, so retrying only delays telling somebody.
    assert outcome.retry is False
    assert outcome.attempts == 1
    stored = await documents.get_by_file_id(job.file_id)
    assert "above the" in stored.last_error


async def test_a_document_at_the_limit_is_still_ingested() -> None:
    """The other direction. A cap that refuses the largest legitimate file is a defect of its own,
    and the boundary is the only place that distinction lives."""
    job = _job()
    body = make_pdf_bytes(1)
    limit = Settings().max_document_bytes
    storage = FakeFileStorage({S3_KEY: body}, sizes={S3_KEY: limit})
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
    assert storage.calls == 1


async def test_the_limit_covers_the_whole_platforms_largest_upload() -> None:
    """RagService's cap must not sit below the size ClassroomService will accept.

    A cap below the upload limit is the worst of both: the teacher's file is accepted at the
    door, stored, and then refused for indexing — so it exists, is listed, and can never be
    searched. 50 MB is ClassroomService's per-file limit; a ClassroomService test asserts the
    same relationship from its side, so the two cannot drift apart in one direction only.
    """
    assert Settings().max_document_bytes >= 50 * 1024 * 1024
