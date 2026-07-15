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
from app.application.ports.generation_provider import GenerationProvider
from app.application.ports.ocr_processor import OcrProcessor
from app.application.ports.pdf_renderer import PdfRenderer
from app.application.ports.summary_publisher import SummaryPublisher
from app.application.ports.summary_storage import SummaryStorage
from app.application.ports.transcript_client import TranscriptClient
from app.application.services.answer_service import AnswerService
from app.application.services.clock import SystemClock
from app.application.services.context_builder import ContextBuilder
from app.application.services.ingestion_service import (
    IngestionJob,
    IngestionResult,
    IngestionService,
)
from app.application.services.ingestion_worker import IngestionWorker
from app.application.services.retrieval_service import RetrievalService
from app.application.services.stale_recovery_service import StaleRecoveryService
from app.application.services.summary_generator import SummaryGenerator
from app.application.services.summary_pipeline import SummaryPipeline
from app.application.services.summary_runner import SummaryRunner
from app.application.services.token_counter import HeuristicTokenCounter
from app.domain.enums.document_status import DocumentStatus
from app.infrastructure.chunking.factory import create_chunker
from app.infrastructure.config.settings import Settings, get_settings
from app.infrastructure.embeddings.ollama_embedding_provider import OllamaEmbeddingProvider
from app.infrastructure.extraction.router import ExtractorRouter
from app.infrastructure.generation.ollama_generation_provider import OllamaGenerationProvider
from app.infrastructure.live_assistant.transcript_client import LiveAssistantTranscriptClient
from app.infrastructure.messaging.masstransit_summary_publisher import (
    MassTransitSummaryPublisher,
)
from app.infrastructure.ocr.tesseract_ocr_processor import TesseractOcrProcessor
from app.infrastructure.persistence.chunk_repository import SqlAlchemyChunkRepository
from app.infrastructure.persistence.database import get_session, get_session_factory
from app.infrastructure.persistence.document_repository import SqlAlchemyDocumentRepository
from app.infrastructure.rendering.weasyprint_pdf_renderer import WeasyPrintPdfRenderer
from app.infrastructure.storage.s3_summary_storage import S3SummaryStorage
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

# Shared summary PDF renderer (S-2): stateless and thread-safe, so one instance serves
# every render. Registered here for the summary flow; no endpoint/trigger calls it yet
# (the session-end trigger + storage is S-3).
_pdf_renderer: PdfRenderer = WeasyPrintPdfRenderer()


def get_extractor() -> Extractor:
    return _extractor


def get_pdf_renderer() -> PdfRenderer:
    return _pdf_renderer


def get_ocr_processor() -> OcrProcessor:
    return _ocr_processor


def get_retrieval_service(
    settings: SettingsDep,
    embedding_provider: EmbeddingProviderDep,
    chunks: ChunkRepositoryDep,
) -> RetrievalService:
    """Retrieval use case for POST /api/search (embed query -> vector search)."""
    return RetrievalService(
        embedding_provider,
        chunks,
        default_top_k=settings.search_default_top_k,
        max_top_k=settings.search_max_top_k,
    )


def get_generation_provider(settings: SettingsDep) -> GenerationProvider:
    return OllamaGenerationProvider(settings)


def get_context_builder(settings: SettingsDep) -> ContextBuilder:
    return ContextBuilder(HeuristicTokenCounter(), settings.context_max_tokens)


def get_answer_service(
    settings: SettingsDep,
    retrieval: RetrievalServiceDep,
    context_builder: ContextBuilderDep,
    generation_provider: GenerationProviderDep,
) -> AnswerService:
    """Answering use case for POST /api/answer (retrieve -> pack context -> generate)."""
    return AnswerService(
        retrieval,
        context_builder,
        generation_provider,
        default_top_k=settings.answer_top_k,
    )


# --- Session summary (S-1) ----------------------------------------------------
# Registered here for the summary use case. No endpoint consumes these yet — the
# session-end trigger and any persistence live in S-3. The summary generator reuses
# the Ollama chat client with the SUMMARY_* parameters, and the RetrievalService for
# optional classroom-scoped grounding.


def get_transcript_client(settings: SettingsDep) -> TranscriptClient:
    """Fetches a session's assembled transcript from LiveAssistantService (S-0)."""
    return LiveAssistantTranscriptClient(settings)


def get_summary_generation_provider(settings: SettingsDep) -> GenerationProvider:
    """The Ollama chat client configured with the SUMMARY_* generation parameters."""
    return OllamaGenerationProvider(
        settings,
        model=settings.summary_model,
        temperature=settings.summary_temperature,
        max_tokens=settings.summary_max_tokens,
    )


def get_summary_generator(
    settings: SettingsDep,
    transcript_client: TranscriptClientDep,
    retrieval: RetrievalServiceDep,
    generation_provider: SummaryGenerationProviderDep,
) -> SummaryGenerator:
    """Summarization use case (fetch transcript -> optional grounding -> Markdown)."""
    return SummaryGenerator(
        transcript_client, retrieval, generation_provider, settings
    )


# --- Session summary storage, trigger & ready event (S-3) ---------------------


def get_summary_storage(settings: SettingsDep) -> SummaryStorage:
    """Writes the Markdown + PDF artifacts to object storage (S3)."""
    return S3SummaryStorage(settings)


def get_summary_publisher(settings: SettingsDep) -> SummaryPublisher:
    """Publishes the SessionSummaryReadyMessage onto the bus for ClassroomService (S-4)."""
    return MassTransitSummaryPublisher(settings)


def get_summary_pipeline(
    settings: SettingsDep,
    generator: SummaryGeneratorDep,
    renderer: PdfRendererDep,
    storage: SummaryStorageDep,
    publisher: SummaryPublisherDep,
) -> SummaryPipeline:
    """The end-to-end pipeline: generate -> render -> upload -> publish (non-fatal)."""
    return SummaryPipeline(generator, renderer, storage, publisher, settings)


DocumentRepositoryDep = Annotated[DocumentRepository, Depends(get_document_repository)]
ChunkRepositoryDep = Annotated[ChunkRepository, Depends(get_chunk_repository)]
EmbeddingProviderDep = Annotated[EmbeddingProvider, Depends(get_embedding_provider)]
ExtractorDep = Annotated[Extractor, Depends(get_extractor)]
OcrProcessorDep = Annotated[OcrProcessor, Depends(get_ocr_processor)]
PdfRendererDep = Annotated[PdfRenderer, Depends(get_pdf_renderer)]
ChunkerDep = Annotated[Chunker, Depends(get_chunker)]
RetrievalServiceDep = Annotated[RetrievalService, Depends(get_retrieval_service)]
GenerationProviderDep = Annotated[GenerationProvider, Depends(get_generation_provider)]
ContextBuilderDep = Annotated[ContextBuilder, Depends(get_context_builder)]
AnswerServiceDep = Annotated[AnswerService, Depends(get_answer_service)]
TranscriptClientDep = Annotated[TranscriptClient, Depends(get_transcript_client)]
SummaryGenerationProviderDep = Annotated[
    GenerationProvider, Depends(get_summary_generation_provider)
]
SummaryGeneratorDep = Annotated[SummaryGenerator, Depends(get_summary_generator)]
SummaryStorageDep = Annotated[SummaryStorage, Depends(get_summary_storage)]
SummaryPublisherDep = Annotated[SummaryPublisher, Depends(get_summary_publisher)]
SummaryPipelineDep = Annotated[SummaryPipeline, Depends(get_summary_pipeline)]


# --- Summary background runner (S-3) ------------------------------------------
# Built once at app startup (see the app factory's lifespan) and stored on app.state.
# Like the ingestion worker, each run opens its OWN DB session; the stateless renderer /
# storage / publisher are shared across runs.


def build_summary_runner() -> SummaryRunner:
    settings = get_settings()
    renderer = _pdf_renderer
    storage: SummaryStorage = S3SummaryStorage(settings)
    publisher: SummaryPublisher = MassTransitSummaryPublisher(settings)
    session_factory = get_session_factory()

    async def handle(session_id, classroom_id) -> None:
        async with session_factory() as session:
            retrieval = RetrievalService(
                OllamaEmbeddingProvider(settings),
                SqlAlchemyChunkRepository(session),
                default_top_k=settings.search_default_top_k,
                max_top_k=settings.search_max_top_k,
            )
            generator = SummaryGenerator(
                LiveAssistantTranscriptClient(settings),
                retrieval,
                OllamaGenerationProvider(
                    settings,
                    model=settings.summary_model,
                    temperature=settings.summary_temperature,
                    max_tokens=settings.summary_max_tokens,
                ),
                settings,
            )
            pipeline = SummaryPipeline(generator, renderer, storage, publisher, settings)
            # The pipeline is non-fatal on its own; the runner also guards the task.
            await pipeline.run(session_id, classroom_id)

    return SummaryRunner(handle)


def get_summary_runner(request: Request) -> SummaryRunner:
    runner: SummaryRunner | None = getattr(request.app.state, "summary_runner", None)
    if runner is None:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="Summary runner is not running.",
        )
    return runner


SummaryRunnerDep = Annotated[SummaryRunner, Depends(get_summary_runner)]


# --- Ingestion worker composition --------------------------------------------
# Built once at app startup (see the app factory's lifespan) and stored on
# app.state. Each job runs in its own DB session/transaction; stateless components
# (storage, extractor, OCR, chunker, embedder) are shared across jobs.


# Shared wall clock. Tests inject a FakeClock into the services directly instead.
_clock = SystemClock()


def build_ingestion_worker() -> IngestionWorker:
    settings = get_settings()
    storage: FileStorage = S3FileStorage(settings)
    embedder = OllamaEmbeddingProvider(settings)
    chunker = create_chunker(settings, embedder)
    session_factory = get_session_factory()

    async def handle(job: IngestionJob) -> IngestionResult | None:
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
                    clock=_clock,
                    max_attempts=settings.ingest_max_attempts,
                    retry_base_seconds=settings.ingest_retry_base_seconds,
                    retry_max_seconds=settings.ingest_retry_max_seconds,
                )
                result = await service.ingest(job)
                await session.commit()
                return result
        except Exception:  # noqa: BLE001
            # The main session may be in a broken state; record Failed independently.
            logger.exception("Ingestion job %s failed at the session boundary", job.file_id)
            with contextlib.suppress(Exception):
                async with session_factory() as session:
                    await SqlAlchemyDocumentRepository(session).update_status(
                        job.file_id, DocumentStatus.FAILED, "Ingestion failed."
                    )
                    await session.commit()
            return None

    return IngestionWorker(
        handle,
        settings.ingest_max_concurrency,
        settings.ingest_queue_max,
        clock=_clock,
    )


async def run_stale_recovery(worker: IngestionWorker) -> int:
    """Reset documents stuck in Processing and re-enqueue them. Returns the count.

    Called once on startup (see the app factory) when STALE_RECOVERY_ON_STARTUP.
    """
    settings = get_settings()
    session_factory = get_session_factory()
    async with session_factory() as session:
        recovery = StaleRecoveryService(
            SqlAlchemyDocumentRepository(session),
            clock=_clock,
            stale_minutes=settings.stale_processing_minutes,
        )
        recovered = await recovery.recover()
        await session.commit()

    for document in recovered:
        worker.enqueue(IngestionJob.from_document(document))
    if recovered:
        logger.info("Re-enqueued %d recovered document(s) on startup.", len(recovered))
    return len(recovered)


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
