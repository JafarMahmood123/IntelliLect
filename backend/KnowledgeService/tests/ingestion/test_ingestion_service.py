from __future__ import annotations

from dataclasses import dataclass
from uuid import UUID, uuid4

from app.application.services.ingestion_service import IngestionJob, IngestionService
from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus
from app.infrastructure.chunking.factory import create_chunker
from app.infrastructure.config.settings import Settings
from app.infrastructure.extraction.router import ExtractorRouter
from app.infrastructure.ocr.tesseract_ocr_processor import TesseractOcrProcessor

from tests.ingestion.fakes import (
    FakeEmbeddingProvider,
    FakeFileStorage,
    InMemoryChunkRepository,
    InMemoryDocumentRepository,
    RaisingEmbeddingProvider,
    make_pdf_bytes,
)

S3_KEY = "classroom/doc.pdf"


@dataclass
class Harness:
    service: IngestionService
    storage: FakeFileStorage
    embedder: FakeEmbeddingProvider
    documents: InMemoryDocumentRepository
    chunks: InMemoryChunkRepository
    settings: Settings
    job: IngestionJob
    file_id: UUID


def _build(embedder: FakeEmbeddingProvider | None = None, *, seed: bool = True) -> Harness:
    settings = Settings()
    embedder = embedder or FakeEmbeddingProvider(settings.embedding_dim)
    storage = FakeFileStorage({S3_KEY: make_pdf_bytes(variant=1)})
    documents = InMemoryDocumentRepository()
    chunks = InMemoryChunkRepository()
    file_id, classroom_id = uuid4(), uuid4()

    if seed:
        # The endpoint would have upserted a Pending row before enqueuing.
        seed_doc = Document(
            classroom_id=classroom_id,
            file_id=file_id,
            s3_key=S3_KEY,
            file_name="doc.pdf",
            content_type="application/pdf",
            status=DocumentStatus.PENDING,
        )
        # add() is async; seeded inside the test via the returned harness if needed.
        documents._by_file_id[file_id] = seed_doc  # noqa: SLF001 — direct seed
        documents.status_history[file_id] = [DocumentStatus.PENDING]

    service = IngestionService(
        file_storage=storage,
        extractor=ExtractorRouter.default(),
        ocr_processor=TesseractOcrProcessor(settings),
        chunker=create_chunker(settings, embedder),
        embedding_provider=embedder,
        document_repository=documents,
        chunk_repository=chunks,
        embed_batch_size=settings.embed_batch_size,
    )
    job = IngestionJob(
        file_id=file_id,
        classroom_id=classroom_id,
        s3_key=S3_KEY,
        file_name="doc.pdf",
        content_type="application/pdf",
    )
    return Harness(service, storage, embedder, documents, chunks, settings, job, file_id)


async def test_happy_path_pending_processing_done_with_embeddings() -> None:
    h = _build()

    outcome = await h.service.ingest(h.job)

    assert outcome.status == DocumentStatus.DONE
    assert outcome.skipped is False
    assert outcome.chunk_count > 0

    # Status transitions: Pending -> Processing -> Done.
    assert h.documents.status_history[h.file_id] == [
        DocumentStatus.PENDING,
        DocumentStatus.PROCESSING,
        DocumentStatus.DONE,
    ]

    stored = await h.documents.get_by_file_id(h.file_id)
    assert stored is not None
    assert stored.status == DocumentStatus.DONE
    assert stored.content_hash is not None and len(stored.content_hash) == 64
    assert stored.error is None

    # Chunks persisted with aligned embeddings of length EMBEDDING_DIM.
    persisted = h.chunks.by_document[stored.id]
    assert len(persisted) == outcome.chunk_count
    assert [chunk.chunk_index for chunk, _ in persisted] == list(range(len(persisted)))
    for chunk, embedding in persisted:
        assert len(embedding) == h.settings.embedding_dim
        assert "page" in chunk.metadata  # location metadata preserved


async def test_idempotent_rerun_same_hash_skips_reprocessing() -> None:
    h = _build()
    await h.service.ingest(h.job)

    stored = await h.documents.get_by_file_id(h.file_id)
    chunks_snapshot = list(h.chunks.by_document[stored.id])
    embeds_after_first = h.embedder.embed_documents_calls
    add_calls_after_first = h.chunks.add_many_calls

    outcome = await h.service.ingest(h.job)

    assert outcome.skipped is True
    assert outcome.status == DocumentStatus.DONE
    # No re-embedding and no chunk churn.
    assert h.embedder.embed_documents_calls == embeds_after_first
    assert h.chunks.add_many_calls == add_calls_after_first
    assert h.chunks.by_document[stored.id] == chunks_snapshot


async def test_reindex_on_changed_hash_replaces_chunks() -> None:
    h = _build()
    await h.service.ingest(h.job)
    stored = await h.documents.get_by_file_id(h.file_id)
    first_hash = stored.content_hash
    first_ids = {chunk.id for chunk, _ in h.chunks.by_document[stored.id]}

    # Same key, new content -> new hash -> re-index.
    h.storage.put(S3_KEY, make_pdf_bytes(variant=2))
    outcome = await h.service.ingest(h.job)

    assert outcome.status == DocumentStatus.DONE
    assert outcome.skipped is False
    # Old chunks deleted, new set written.
    assert h.chunks.delete_calls == 2
    assert h.chunks.add_many_calls == 2
    new_ids = {chunk.id for chunk, _ in h.chunks.by_document[stored.id]}
    assert new_ids.isdisjoint(first_ids)

    stored2 = await h.documents.get_by_file_id(h.file_id)
    assert stored2.content_hash is not None and stored2.content_hash != first_hash


async def test_failure_marks_document_failed_and_records_error() -> None:
    settings = Settings()
    h = _build(embedder=RaisingEmbeddingProvider(settings.embedding_dim))

    outcome = await h.service.ingest(h.job)

    assert outcome.status == DocumentStatus.FAILED
    assert outcome.error is not None and "embedder is down" in outcome.error

    stored = await h.documents.get_by_file_id(h.file_id)
    assert stored.status == DocumentStatus.FAILED
    assert stored.error is not None and "embedder is down" in stored.error

    # It reached Processing before failing, and ended Failed.
    history = h.documents.status_history[h.file_id]
    assert DocumentStatus.PROCESSING in history
    assert history[-1] == DocumentStatus.FAILED

    # No chunks were persisted.
    assert h.chunks.add_many_calls == 0
    assert h.chunks.by_document.get(stored.id) in (None, [])


async def test_ingest_returns_normally_on_failure_so_worker_continues() -> None:
    # ingest() must never raise for a pipeline failure — it returns an outcome.
    h = _build(embedder=RaisingEmbeddingProvider(Settings().embedding_dim))
    outcome = await h.service.ingest(h.job)  # would raise if not swallowed
    assert outcome.status == DocumentStatus.FAILED
