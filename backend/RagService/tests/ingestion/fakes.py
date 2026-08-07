"""Offline fakes + fixtures for the ingestion tests.

No S3, no Ollama, no Postgres: file bytes come from an in-memory map, embeddings
are deterministic vectors of length EMBEDDING_DIM, the repositories are plain dicts,
and time is a controllable FakeClock. The extractor/OCR/chunker used in the tests
are the REAL ones (they run offline; OCR needs only the tesseract binary).
"""

from __future__ import annotations

import hashlib
from dataclasses import replace
from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4

import pymupdf

from app.application.ports.chunk_repository import ChunkRepository
from app.application.ports.document_repository import (
    DocumentListItem,
    DocumentRepository,
    KnowledgeStats,
)
from app.application.ports.embedding_provider import EmbeddingProvider
from app.application.ports.file_storage import FileStorage
from app.application.services.ingestion_errors import TransientIngestionError
from app.application.services.ingestion_service import IngestionJob, IngestionService
from app.domain.entities.chunk import Chunk
from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus
from app.infrastructure.chunking.factory import create_chunker
from app.infrastructure.config.settings import Settings
from app.infrastructure.extraction.router import ExtractorRouter
from app.infrastructure.ocr.tesseract_ocr_processor import TesseractOcrProcessor


def build_ingestion_service(
    *,
    storage: FileStorage,
    embedder: EmbeddingProvider,
    documents: DocumentRepository,
    chunks: ChunkRepository,
    clock=None,
    settings: Settings | None = None,
    max_attempts: int = 3,
    retry_base: float = 2.0,
    retry_max: float = 30.0,
) -> IngestionService:
    """Build an IngestionService with the REAL extractor/OCR/chunker + the given fakes."""
    settings = settings or Settings()
    return IngestionService(
        file_storage=storage,
        extractor=ExtractorRouter.default(),
        ocr_processor=TesseractOcrProcessor(settings),
        chunker=create_chunker(settings, embedder),
        embedding_provider=embedder,
        document_repository=documents,
        chunk_repository=chunks,
        embed_batch_size=settings.embed_batch_size,
        clock=clock,
        max_attempts=max_attempts,
        retry_base_seconds=retry_base,
        retry_max_seconds=retry_max,
        max_document_bytes=settings.max_document_bytes,
    )


def make_pdf_bytes(variant: int = 1) -> bytes:
    """A deterministic 2-page text PDF. Different `variant` -> different bytes/hash."""
    document = pymupdf.open()
    for page_number in range(1, 3):
        page = document.new_page()
        page.insert_text((72, 100), f"Variant {variant} Chapter {page_number}")
        page.insert_text(
            (72, 140),
            f"This is body text for variant {variant}, page {page_number}. " * 3,
        )
    data = document.tobytes()
    document.close()
    return data


def make_job(
    *,
    file_id: UUID | None = None,
    classroom_id: UUID | None = None,
    s3_key: str = "classroom/doc.pdf",
    file_name: str = "doc.pdf",
    content_type: str = "application/pdf",
) -> IngestionJob:
    return IngestionJob(
        file_id=file_id or uuid4(),
        classroom_id=classroom_id or uuid4(),
        s3_key=s3_key,
        file_name=file_name,
        content_type=content_type,
    )


class FakeClock:
    """Controllable clock: now() is fixed until advanced; sleep() records + advances."""

    def __init__(self, start: datetime | None = None) -> None:
        self._now = start or datetime(2026, 1, 1, tzinfo=timezone.utc)
        self.sleeps: list[float] = []

    def now(self) -> datetime:
        return self._now

    def advance(self, seconds: float) -> None:
        self._now += timedelta(seconds=seconds)

    async def sleep(self, seconds: float) -> None:
        self.sleeps.append(seconds)
        self._now += timedelta(seconds=seconds)


class FakeFileStorage(FileStorage):
    """Maps s3_key -> bytes. `put` lets a test change the content (re-index)."""

    def __init__(
        self,
        mapping: dict[str, bytes] | None = None,
        sizes: dict[str, int] | None = None,
    ) -> None:
        self._mapping: dict[str, bytes] = dict(mapping or {})
        # Sizes are overridable so a test can present a huge object without allocating one.
        # An object nobody overrode reports its real length, so the size check cannot pass
        # by reporting zero for everything.
        self._sizes: dict[str, int] = dict(sizes or {})
        self.calls = 0
        self.size_calls = 0

    def put(self, s3_key: str, data: bytes) -> None:
        self._mapping[s3_key] = data

    async def get_bytes(self, s3_key: str) -> bytes:
        self.calls += 1
        return self._mapping[s3_key]

    async def get_size(self, s3_key: str) -> int:
        self.size_calls += 1
        if s3_key in self._sizes:
            return self._sizes[s3_key]
        return len(self._mapping[s3_key])


class FakeEmbeddingProvider(EmbeddingProvider):
    """Deterministic embeddings of length `dim`. Counts calls / records texts."""

    def __init__(self, dim: int) -> None:
        self._dim = dim
        self.embed_documents_calls = 0
        self.embedded_texts: list[str] = []

    async def embed_documents(self, texts: list[str]) -> list[list[float]]:
        self.embed_documents_calls += 1
        self.embedded_texts.extend(texts)
        return [self._vector(text) for text in texts]

    async def embed_query(self, text: str) -> list[float]:
        return self._vector(text)

    def _vector(self, text: str) -> list[float]:
        digest = hashlib.sha256(text.encode("utf-8")).digest()
        return [digest[i % len(digest)] / 255.0 for i in range(self._dim)]


class RaisingEmbeddingProvider(FakeEmbeddingProvider):
    """Raises a chosen exception on embed to exercise the failure paths."""

    def __init__(self, dim: int, error: Exception | None = None) -> None:
        super().__init__(dim)
        self._error = error or TransientIngestionError("embedder is down")

    async def embed_documents(self, texts: list[str]) -> list[list[float]]:
        self.embed_documents_calls += 1
        raise self._error


class InMemoryDocumentRepository(DocumentRepository):
    """Dict-backed DocumentRepository mirroring the upsert/claim semantics and
    recording every status it transitions through (for assertions)."""

    def __init__(self) -> None:
        self._by_file_id: dict[UUID, Document] = {}
        self.status_history: dict[UUID, list[DocumentStatus]] = {}
        # Chunk counts keyed by document id, set by tests exercising the admin read side.
        self.chunk_counts: dict[UUID, int] = {}

    def seed(self, document: Document) -> None:
        self._by_file_id[document.file_id] = document
        self.status_history.setdefault(document.file_id, []).append(document.status)

    def _record(self, file_id: UUID, status: DocumentStatus) -> None:
        self.status_history.setdefault(file_id, []).append(status)

    async def add(self, document: Document) -> Document:
        existing = self._by_file_id.get(document.file_id)
        if existing is not None:
            document.id = existing.id
            document.created_at_utc = existing.created_at_utc
            # Preserve attempts / processing_started_at on upsert (mirrors SQL add()).
            document.attempts = existing.attempts
            document.processing_started_at = existing.processing_started_at
        stored = replace(document)
        self._by_file_id[document.file_id] = stored
        self._record(document.file_id, stored.status)
        return replace(stored)

    async def get_by_file_id(self, file_id: UUID) -> Document | None:
        stored = self._by_file_id.get(file_id)
        return replace(stored) if stored is not None else None

    async def update_status(
        self, file_id: UUID, status: DocumentStatus, last_error: str | None = None
    ) -> None:
        stored = self._by_file_id.get(file_id)
        if stored is None:
            return
        stored.status = status
        stored.last_error = last_error
        self._record(file_id, status)

    async def delete_by_file_id(self, file_id: UUID) -> bool:
        return self._by_file_id.pop(file_id, None) is not None

    async def delete_by_classroom_id(self, classroom_id: UUID) -> int:
        doomed = [
            file_id
            for file_id, doc in self._by_file_id.items()
            if doc.classroom_id == classroom_id
        ]
        for file_id in doomed:
            del self._by_file_id[file_id]
        return len(doomed)

    async def claim_for_processing(
        self, document: Document, now: datetime
    ) -> Document | None:
        # Synchronous body (no await) -> atomic per call, so gather() gives exactly one
        # winner. Claims a Pending row (or creates a missing one) as Processing.
        existing = self._by_file_id.get(document.file_id)
        if existing is None:
            stored = replace(
                document,
                status=DocumentStatus.PROCESSING,
                attempts=1,
                processing_started_at=now,
                last_error=None,
                content_hash=None,
            )
            self._by_file_id[document.file_id] = stored
            self._record(document.file_id, DocumentStatus.PROCESSING)
            return replace(stored)
        if existing.status != DocumentStatus.PENDING:
            return None
        existing.status = DocumentStatus.PROCESSING
        existing.attempts += 1
        existing.processing_started_at = now
        existing.last_error = None
        self._record(document.file_id, DocumentStatus.PROCESSING)
        return replace(existing)

    async def find_stale_processing(self, threshold: datetime) -> list[Document]:
        return [
            replace(doc)
            for doc in self._by_file_id.values()
            if doc.status == DocumentStatus.PROCESSING
            and doc.processing_started_at is not None
            and doc.processing_started_at < threshold
        ]

    async def reset_to_pending(
        self, file_id: UUID, *, reset_attempts: bool
    ) -> Document | None:
        stored = self._by_file_id.get(file_id)
        if stored is None:
            return None
        stored.status = DocumentStatus.PENDING
        stored.last_error = None
        stored.processing_started_at = None
        if reset_attempts:
            stored.attempts = 0
        self._record(file_id, DocumentStatus.PENDING)
        return replace(stored)

    def _item(self, doc: Document) -> DocumentListItem:
        return DocumentListItem(
            file_id=doc.file_id,
            classroom_id=doc.classroom_id,
            file_name=doc.file_name,
            content_type=doc.content_type,
            size_bytes=doc.size_bytes,
            status=doc.status,
            attempts=doc.attempts,
            chunk_count=self.chunk_counts.get(doc.id, 0),
        )

    async def list_paged(self, *, page, page_size, status, classroom_id, search):
        docs = [
            d for d in self._by_file_id.values()
            if (status is None or d.status == status)
            and (classroom_id is None or d.classroom_id == classroom_id)
            and (not search or search.lower() in d.file_name.lower())
        ]
        docs.sort(key=lambda d: d.created_at_utc, reverse=True)
        total = len(docs)
        start = (page - 1) * page_size
        return [self._item(d) for d in docs[start:start + page_size]], total

    async def get_statuses(self, file_ids):
        wanted = set(file_ids)
        return [self._item(d) for d in self._by_file_id.values() if d.file_id in wanted]

    async def stats(self, classroom_id):
        docs = [
            d for d in self._by_file_id.values()
            if classroom_id is None or d.classroom_id == classroom_id
        ]
        status_counts: dict[str, int] = {}
        storage = 0
        for d in docs:
            status_counts[d.status.value] = status_counts.get(d.status.value, 0) + 1
            storage += d.size_bytes
        total_chunks = sum(
            self.chunk_counts.get(d.id, 0) for d in docs
        )
        return KnowledgeStats(
            document_count=len(docs),
            status_counts=status_counts,
            total_chunks=total_chunks,
            failed_count=status_counts.get(DocumentStatus.FAILED.value, 0),
            storage_bytes=storage,
        )

    async def list_file_ids_for_reindex(self, classroom_id, *, failed_only):
        return [
            d.file_id for d in self._by_file_id.values()
            if d.classroom_id == classroom_id
            and (not failed_only or d.status == DocumentStatus.FAILED)
        ]

    async def count_active(self, classroom_id):
        return sum(
            1 for d in self._by_file_id.values()
            if d.classroom_id == classroom_id
            and d.status in (DocumentStatus.PENDING, DocumentStatus.PROCESSING)
        )


class InMemoryChunkRepository(ChunkRepository):
    """Dict-backed ChunkRepository keyed by document_id, holding (chunk, embedding).

    replace_for_document is overridden to be all-or-nothing (models a DB transaction):
    when `fail_replace` is set it raises BEFORE mutating, so prior chunks stay intact.
    """

    def __init__(self, *, fail_replace: bool = False) -> None:
        self.by_document: dict[UUID, list[tuple[Chunk, list[float]]]] = {}
        self.add_many_calls = 0
        self.delete_calls = 0
        self.replace_calls = 0
        self.fail_replace = fail_replace

    async def add_many(self, chunks, embeddings) -> None:
        self.add_many_calls += 1
        for chunk, embedding in zip(chunks, embeddings, strict=True):
            self.by_document.setdefault(chunk.document_id, []).append(
                (chunk, list(embedding))
            )

    async def delete_by_document_id(self, document_id: UUID) -> int:
        removed = self.by_document.pop(document_id, [])
        self.delete_calls += 1
        return len(removed)

    async def delete_by_classroom_id(self, classroom_id: UUID) -> int:
        removed = 0
        for document_id in list(self.by_document):
            entries = self.by_document[document_id]
            if entries and entries[0][0].classroom_id == classroom_id:
                removed += len(entries)
                del self.by_document[document_id]
        return removed

    # The re-embed job's half of the port. Implemented against the same dict rather than
    # stubbed, so an e2e test that re-embeds sees consistent state — a chunk stored with an
    # empty vector really is reported missing, and set_embeddings really fills it in.
    async def count_all(self) -> int:
        return sum(len(entries) for entries in self.by_document.values())

    async def count_missing_embeddings(self) -> int:
        return sum(
            1
            for entries in self.by_document.values()
            for _, embedding in entries
            if not embedding
        )

    async def fetch_missing_embeddings(self, limit: int) -> list[tuple[UUID, str]]:
        missing: list[tuple[UUID, str]] = []
        for entries in self.by_document.values():
            for chunk, embedding in entries:
                if not embedding:
                    missing.append((chunk.id, chunk.text))
                    if len(missing) >= limit:
                        return missing
        return missing

    async def set_embeddings(self, embeddings: dict[UUID, list[float]]) -> int:
        updated = 0
        for document_id, entries in self.by_document.items():
            for index, (chunk, _) in enumerate(entries):
                vector = embeddings.get(chunk.id)
                if vector is not None:
                    entries[index] = (chunk, list(vector))
                    updated += 1
        return updated

    async def replace_for_document(self, document_id, chunks, embeddings) -> None:
        self.replace_calls += 1
        if self.fail_replace:
            raise TransientIngestionError("chunk persistence failed")
        self.by_document[document_id] = [
            (chunk, list(embedding))
            for chunk, embedding in zip(chunks, embeddings, strict=True)
        ]

    async def search(self, classroom_id, query_embedding, top_k):
        return []
