from __future__ import annotations

import hashlib
import logging
from dataclasses import dataclass
from uuid import UUID

from app.application.ports.chunk_repository import ChunkRepository
from app.application.ports.chunker import Chunker
from app.application.ports.document_repository import DocumentRepository
from app.application.ports.embedding_provider import EmbeddingProvider
from app.application.ports.extractor import Extractor
from app.application.ports.file_storage import FileStorage
from app.application.ports.ocr_processor import OcrProcessor
from app.domain.entities.chunk import Chunk
from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus

logger = logging.getLogger("knowledge.ingestion")


@dataclass(frozen=True)
class IngestionJob:
    """A unit of ingestion work. Carries the full document identity so the worker
    never depends on a not-yet-committed Pending row (avoids an enqueue/commit race)."""

    file_id: UUID
    classroom_id: UUID
    s3_key: str
    file_name: str
    content_type: str

    @classmethod
    def from_document(cls, document: Document) -> IngestionJob:
        return cls(
            file_id=document.file_id,
            classroom_id=document.classroom_id,
            s3_key=document.s3_key,
            file_name=document.file_name,
            content_type=document.content_type,
        )


@dataclass
class IngestionResult:
    """Outcome of one ingest() call (for the worker log, tests, and the CLI)."""

    file_id: UUID
    status: DocumentStatus
    chunk_count: int
    skipped: bool = False
    error: str | None = None


class IngestionService:
    """Orchestrates the full ingestion pipeline for one document.

    download -> extract (P2) -> OCR (P3) -> chunk (P4) -> embed -> persist -> Done.

    Depends only on ports, so it runs offline against fakes. It owns status
    transitions and error handling: any failure marks the document Failed (with a
    concise message) and returns rather than raising, so the worker survives.
    """

    def __init__(
        self,
        file_storage: FileStorage,
        extractor: Extractor,
        ocr_processor: OcrProcessor,
        chunker: Chunker,
        embedding_provider: EmbeddingProvider,
        document_repository: DocumentRepository,
        chunk_repository: ChunkRepository,
        embed_batch_size: int = 32,
    ) -> None:
        self._storage = file_storage
        self._extractor = extractor
        self._ocr = ocr_processor
        self._chunker = chunker
        self._embedder = embedding_provider
        self._documents = document_repository
        self._chunks = chunk_repository
        self._embed_batch_size = max(1, embed_batch_size)

    async def ingest(self, job: IngestionJob) -> IngestionResult:
        try:
            existing = await self._documents.get_by_file_id(job.file_id)

            file_bytes = await self._storage.get_bytes(job.s3_key)
            content_hash = hashlib.sha256(file_bytes).hexdigest()

            # Idempotency: an unchanged, already-Done document is left untouched.
            if (
                existing is not None
                and existing.status == DocumentStatus.DONE
                and existing.content_hash == content_hash
            ):
                logger.info(
                    "Skipping ingest for %s: already Done with the same content hash.",
                    job.file_id,
                )
                return IngestionResult(
                    file_id=job.file_id,
                    status=DocumentStatus.DONE,
                    chunk_count=0,
                    skipped=True,
                )

            # Mark Processing. add() upserts on file_id, so this also creates the row
            # if the Pending write has not committed yet, and returns the real id.
            document = self._document_for(
                job, existing, DocumentStatus.PROCESSING, existing_hash(existing)
            )
            document = await self._documents.add(document)

            # extract -> OCR -> chunk
            result = self._extractor.extract(file_bytes, job.file_name, job.content_type)
            result = await self._ocr.process(file_bytes, result)
            chunks = await self._chunker.chunk(result, document.id, job.classroom_id)

            embeddings = await self._embed(chunks)

            # Re-index: replace any prior chunks with the new set, together, so an
            # earlier failure never wipes existing chunks.
            await self._chunks.delete_by_document_id(document.id)
            if chunks:
                await self._chunks.add_many(chunks, embeddings)

            document.content_hash = content_hash
            document.status = DocumentStatus.DONE
            document.error = None
            await self._documents.add(document)

            logger.info("Ingested %s: %d chunk(s) persisted.", job.file_id, len(chunks))
            return IngestionResult(
                file_id=job.file_id,
                status=DocumentStatus.DONE,
                chunk_count=len(chunks),
            )
        except Exception as exc:  # noqa: BLE001 — one bad document must not kill the worker
            message = f"{type(exc).__name__}: {exc}"[:500]
            logger.exception("Ingestion failed for %s", job.file_id)
            try:
                await self._documents.update_status(
                    job.file_id, DocumentStatus.FAILED, message
                )
            except Exception:  # noqa: BLE001
                logger.exception("Could not mark %s as Failed", job.file_id)
            return IngestionResult(
                file_id=job.file_id,
                status=DocumentStatus.FAILED,
                chunk_count=0,
                error=message,
            )

    async def _embed(self, chunks: list[Chunk]) -> list[list[float]]:
        if not chunks:
            return []
        texts = [chunk.text for chunk in chunks]
        embeddings: list[list[float]] = []
        for start in range(0, len(texts), self._embed_batch_size):
            batch = texts[start : start + self._embed_batch_size]
            embeddings.extend(await self._embedder.embed_documents(batch))
        return embeddings

    @staticmethod
    def _document_for(
        job: IngestionJob,
        existing: Document | None,
        status: DocumentStatus,
        content_hash: str | None,
    ) -> Document:
        document = Document(
            classroom_id=job.classroom_id,
            file_id=job.file_id,
            s3_key=job.s3_key,
            file_name=job.file_name,
            content_type=job.content_type,
            content_hash=content_hash,
            status=status,
        )
        if existing is not None:
            document.id = existing.id
            document.created_at_utc = existing.created_at_utc
        return document


def existing_hash(document: Document | None) -> str | None:
    return document.content_hash if document is not None else None
