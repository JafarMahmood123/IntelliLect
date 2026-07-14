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
from app.application.services.clock import Clock, SystemClock
from app.application.services.ingestion_errors import is_transient
from app.domain.entities.chunk import Chunk
from app.domain.entities.document import Document
from app.domain.enums.document_status import DocumentStatus
from app.domain.extraction.text_block import TextBlockSource
from app.observability import metrics
from app.observability.correlation import correlation_scope

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
    """Outcome of one ingest() call (for the worker, tests, and the CLI)."""

    file_id: UUID
    status: DocumentStatus
    chunk_count: int = 0
    skipped: bool = False
    error: str | None = None
    attempts: int = 0
    retry: bool = False  # transient failure — the worker should re-enqueue after a delay
    retry_delay_seconds: float = 0.0


class IngestionService:
    """Orchestrates the full ingestion pipeline for one document.

    claim -> download -> extract (P2) -> OCR (P3) -> chunk (P4) -> embed ->
    atomic chunk replace -> Done.

    Robustness (Phase 8): a concurrency-safe claim prevents double-processing;
    transient failures are retried with backoff (up to INGEST_MAX_ATTEMPTS) while
    permanent ones fail fast; chunk writes are atomic; unchanged content is a no-op.
    Never raises for a pipeline failure — returns an IngestionResult so the worker
    survives and can schedule a retry.
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
        clock: Clock | None = None,
        max_attempts: int = 3,
        retry_base_seconds: float = 2.0,
        retry_max_seconds: float = 30.0,
    ) -> None:
        self._storage = file_storage
        self._extractor = extractor
        self._ocr = ocr_processor
        self._chunker = chunker
        self._embedder = embedding_provider
        self._documents = document_repository
        self._chunks = chunk_repository
        self._embed_batch_size = max(1, embed_batch_size)
        self._clock = clock or SystemClock()
        self._max_attempts = max(1, max_attempts)
        self._retry_base = retry_base_seconds
        self._retry_max = retry_max_seconds

    async def ingest(self, job: IngestionJob) -> IngestionResult:
        # Bind correlation ids (file_id + a generated run id) for every log emitted
        # while this document is processed. Instrumentation is additive: it never
        # changes control flow or results.
        with correlation_scope(job.file_id):
            return await self._run_ingest(job)

    async def _run_ingest(self, job: IngestionJob) -> IngestionResult:
        # 1) Claim the document atomically. If we don't win the claim, another worker
        #    owns it or it's terminal — exit cleanly without reprocessing.
        template = Document(
            classroom_id=job.classroom_id,
            file_id=job.file_id,
            s3_key=job.s3_key,
            file_name=job.file_name,
            content_type=job.content_type,
            status=DocumentStatus.PROCESSING,
        )
        claimed = await self._documents.claim_for_processing(template, self._clock.now())
        if claimed is None:
            existing = await self._documents.get_by_file_id(job.file_id)
            status = existing.status if existing is not None else DocumentStatus.DONE
            logger.info("ingest skipped: not claimable", extra={"claim_status": status.value})
            metrics.record_document("skipped")
            return IngestionResult(file_id=job.file_id, status=status, skipped=True)

        attempts = claimed.attempts
        logger.info("claimed", extra={"attempt": attempts})
        with metrics.track_inflight(), metrics.ingestion_timer():
            try:
                file_bytes = await self._storage.get_bytes(job.s3_key)
                content_hash = hashlib.sha256(file_bytes).hexdigest()

                # 6) Idempotency: content unchanged since the last successful index ->
                #    the existing chunks are still valid; just mark Done.
                if claimed.content_hash == content_hash:
                    await self._mark_done(claimed, content_hash)
                    logger.info("ingest skipped: unchanged content", extra={"attempt": attempts})
                    metrics.record_document("skipped")
                    return IngestionResult(
                        file_id=job.file_id, status=DocumentStatus.DONE,
                        skipped=True, attempts=attempts,
                    )

                with metrics.stage_timer("extract"):
                    result = self._extractor.extract(file_bytes, job.file_name, job.content_type)
                logger.info(
                    "extracted",
                    extra={
                        "blocks": len(result.blocks),
                        "images": len(result.images),
                        "pages_without_text": len(result.pages_without_text),
                    },
                )

                with metrics.stage_timer("ocr"):
                    result = await self._ocr.process(file_bytes, result)
                ocr_invoked = int(getattr(self._ocr, "last_ocr_invocations", 0) or 0)
                ocr_units = sum(
                    1 for block in result.blocks if block.source == TextBlockSource.OCR
                )
                metrics.record_ocr_invocations(ocr_invoked)
                logger.info("ocr", extra={"units": ocr_units, "invoked": ocr_invoked})

                with metrics.stage_timer("chunk"):
                    chunks = await self._chunker.chunk(result, claimed.id, job.classroom_id)
                logger.info("chunked", extra={"chunks": len(chunks)})

                with metrics.stage_timer("embed"):
                    embeddings = await self._embed(chunks)
                metrics.record_embeddings(len(chunks))
                logger.info(
                    "embedded",
                    extra={"chunks": len(chunks), "batches": self._batch_count(len(chunks))},
                )

                # 3) Atomic replace, then mark Done only after chunks commit.
                with metrics.stage_timer("persist"):
                    await self._chunks.replace_for_document(claimed.id, chunks, embeddings)
                    await self._mark_done(claimed, content_hash)

                metrics.record_chunks(len(chunks))
                metrics.record_document("done")
                logger.info("done", extra={"chunks": len(chunks), "attempt": attempts})
                return IngestionResult(
                    file_id=job.file_id, status=DocumentStatus.DONE,
                    chunk_count=len(chunks), attempts=attempts,
                )
            except Exception as exc:  # noqa: BLE001 — classify, then retry or fail
                return await self._handle_failure(job, attempts, exc)

    def _batch_count(self, count: int) -> int:
        if count <= 0:
            return 0
        return (count + self._embed_batch_size - 1) // self._embed_batch_size

    async def _handle_failure(
        self, job: IngestionJob, attempts: int, exc: Exception
    ) -> IngestionResult:
        transient = is_transient(exc)
        # The full detail is persisted to documents.last_error; logs carry only the
        # error TYPE (class name), never the raw message, contents, or secrets.
        message = f"{type(exc).__name__}: {exc}"[:500]
        error_type = type(exc).__name__

        if transient and attempts < self._max_attempts:
            delay = self._backoff(attempts)
            logger.warning(
                "retry",
                extra={
                    "attempt": attempts,
                    "max_attempts": self._max_attempts,
                    "delay_seconds": delay,
                    "error_type": error_type,
                },
            )
            # Back to Pending so the re-enqueued job can claim it again.
            await self._documents.update_status(
                job.file_id, DocumentStatus.PENDING, message
            )
            metrics.record_failure("transient")
            return IngestionResult(
                file_id=job.file_id,
                status=DocumentStatus.PENDING,
                error=message,
                attempts=attempts,
                retry=True,
                retry_delay_seconds=delay,
            )

        reason = "permanent" if not transient else "attempts_exhausted"
        logger.error(
            "failed",
            extra={
                "attempt": attempts,
                "max_attempts": self._max_attempts,
                "reason": reason,
                "error_type": error_type,
            },
        )
        await self._documents.update_status(job.file_id, DocumentStatus.FAILED, message)
        metrics.record_failure("permanent" if not transient else "transient")
        metrics.record_document("failed")
        return IngestionResult(
            file_id=job.file_id,
            status=DocumentStatus.FAILED,
            error=message,
            attempts=attempts,
        )

    def _backoff(self, attempts: int) -> float:
        """Exponential backoff for the given (1-based) attempt number, capped."""
        delay = self._retry_base * (2 ** (attempts - 1))
        return min(delay, self._retry_max)

    async def _mark_done(self, document: Document, content_hash: str) -> None:
        document.content_hash = content_hash
        document.status = DocumentStatus.DONE
        document.last_error = None
        await self._documents.add(document)

    async def _embed(self, chunks: list[Chunk]) -> list[list[float]]:
        if not chunks:
            return []
        texts = [chunk.text for chunk in chunks]
        embeddings: list[list[float]] = []
        for start in range(0, len(texts), self._embed_batch_size):
            batch = texts[start : start + self._embed_batch_size]
            embeddings.extend(await self._embedder.embed_documents(batch))
        return embeddings
