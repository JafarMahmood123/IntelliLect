from __future__ import annotations

import contextlib
import logging
from typing import Annotated

from fastapi import Depends, Header, HTTPException, Request, status
from sqlalchemy.ext.asyncio import AsyncSession

from app.application.ports.chunk_repository import ChunkRepository
from app.application.ports.chunker import Chunker
from app.application.ports.document_repository import DocumentRepository
from app.application.ports.embedding_provider import EmbeddingProvider
from app.application.ports.extractor import Extractor
from app.application.ports.file_storage import FileStorage
from app.application.ports.ocr_processor import OcrProcessor
from app.application.services.ingestion_service import IngestionJob, IngestionService
from app.application.services.ingestion_worker import IngestionWorker
from app.domain.enums.document_status import DocumentStatus
from app.infrastructure.chunking.factory import create_chunker
from app.infrastructure.config.settings import Settings, get_settings
from app.infrastructure.embeddings.ollama_embedding_provider import OllamaEmbeddingProvider
from app.infrastructure.extraction.router import ExtractorRouter
from app.infrastructure.ocr.tesseract_ocr_processor import TesseractOcrProcessor
from app.infrastructure.persistence.chunk_repository import SqlAlchemyChunkRepository
from app.infrastructure.persistence.database import get_session, get_session_factory
from app.infrastructure.persistence.document_repository import SqlAlchemyDocumentRepository
from app.infrastructure.storage.s3_file_storage import S3FileStorage

logger = logging.getLogger("knowledge.api")

# --- Composition root ---------------------------------------------------------
# This module is the only place where API code names concrete infrastructure
# classes. Everything downstream depends on the port abstractions.

SettingsDep = Annotated[Settings, Depends(get_settings)]
SessionDep = Annotated[AsyncSession, Depends(get_session)]


def get_document_repository(session: SessionDep) -> DocumentRepository:
    return SqlAlchemyDocumentRepository(session)


def get_chunk_repository(session: SessionDep) -> ChunkRepository:
    return SqlAlchemyChunkRepository(session)


def get_embedding_provider(settings: SettingsDep) -> EmbeddingProvider:
    return OllamaEmbeddingProvider(settings)


def get_chunker(
    settings: SettingsDep, embedding_provider: EmbeddingProviderDep
) -> Chunker:
    """Chunker chosen by CHUNKING_STRATEGY. The default (structural) is offline and
    ignores the embedding provider. Registered for later phases; no endpoint uses it
    yet."""
    return create_chunker(settings, embedding_provider)


# The router is stateless and thread-safe, so one shared instance serves every
# request. Registered here for later phases to inject; no endpoint calls it yet.
_extractor: Extractor = ExtractorRouter.default()

# Shared OCR processor: config is read once and OCR runs on its own bounded pool.
# Registered for later phases to inject; no endpoint calls it yet.
_ocr_processor: OcrProcessor = TesseractOcrProcessor(get_settings())


def get_extractor() -> Extractor:
    return _extractor


def get_ocr_processor() -> OcrProcessor:
    return _ocr_processor


DocumentRepositoryDep = Annotated[DocumentRepository, Depends(get_document_repository)]
ChunkRepositoryDep = Annotated[ChunkRepository, Depends(get_chunk_repository)]
EmbeddingProviderDep = Annotated[EmbeddingProvider, Depends(get_embedding_provider)]
ExtractorDep = Annotated[Extractor, Depends(get_extractor)]
OcrProcessorDep = Annotated[OcrProcessor, Depends(get_ocr_processor)]
ChunkerDep = Annotated[Chunker, Depends(get_chunker)]


# --- Ingestion worker composition --------------------------------------------
# Built once at app startup (see the app factory's lifespan) and stored on
# app.state. Each job runs in its own DB session/transaction; stateless components
# (storage, extractor, OCR, chunker, embedder) are shared across jobs.


def build_ingestion_worker() -> IngestionWorker:
    settings = get_settings()
    storage: FileStorage = S3FileStorage(settings)
    embedder = OllamaEmbeddingProvider(settings)
    chunker = create_chunker(settings, embedder)
    session_factory = get_session_factory()

    async def handle(job: IngestionJob) -> None:
        try:
            async with session_factory() as session:
                service = IngestionService(
                    file_storage=storage,
                    extractor=_extractor,
                    ocr_processor=_ocr_processor,
                    chunker=chunker,
                    embedding_provider=embedder,
                    document_repository=SqlAlchemyDocumentRepository(session),
                    chunk_repository=SqlAlchemyChunkRepository(session),
                    embed_batch_size=settings.embed_batch_size,
                )
                await service.ingest(job)
                await session.commit()
        except Exception:  # noqa: BLE001
            # The main session may be in a broken state; record Failed independently.
            logger.exception("Ingestion job %s failed at the session boundary", job.file_id)
            with contextlib.suppress(Exception):
                async with session_factory() as session:
                    await SqlAlchemyDocumentRepository(session).update_status(
                        job.file_id, DocumentStatus.FAILED, "Ingestion failed."
                    )
                    await session.commit()

    return IngestionWorker(
        handle, settings.ingest_max_concurrency, settings.ingest_queue_max
    )


def get_ingestion_worker(request: Request) -> IngestionWorker:
    worker: IngestionWorker | None = getattr(
        request.app.state, "ingestion_worker", None
    )
    if worker is None:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="Ingestion worker is not running.",
        )
    return worker


IngestionWorkerDep = Annotated[IngestionWorker, Depends(get_ingestion_worker)]


async def require_internal_secret(
    settings: SettingsDep,
    x_internal_secret: Annotated[str | None, Header(alias="X-Internal-Secret")] = None,
) -> None:
    """Guard for /api/internal/* routes.

    Rejects the request unless the caller presents the shared secret. Fails closed
    if the server has no secret configured.
    """
    expected = settings.internal_api_secret
    if not expected or x_internal_secret != expected:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid or missing internal API secret.",
        )
