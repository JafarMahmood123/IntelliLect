from __future__ import annotations

from typing import Annotated

from fastapi import Depends, Header, HTTPException, status
from sqlalchemy.ext.asyncio import AsyncSession

from app.application.ports.chunk_repository import ChunkRepository
from app.application.ports.document_repository import DocumentRepository
from app.application.ports.embedding_provider import EmbeddingProvider
from app.application.ports.extractor import Extractor
from app.application.ports.ocr_processor import OcrProcessor
from app.infrastructure.config.settings import Settings, get_settings
from app.infrastructure.embeddings.ollama_embedding_provider import OllamaEmbeddingProvider
from app.infrastructure.extraction.router import ExtractorRouter
from app.infrastructure.ocr.tesseract_ocr_processor import TesseractOcrProcessor
from app.infrastructure.persistence.chunk_repository import SqlAlchemyChunkRepository
from app.infrastructure.persistence.database import get_session
from app.infrastructure.persistence.document_repository import SqlAlchemyDocumentRepository

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
