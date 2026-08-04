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
from app.application.services.reembed_runner import ReembedRunner
from app.application.services.reembed_service import ReembedProgress, ReembedService
from app.application.services.retrieval_service import RetrievalService
from app.application.services.stale_recovery_service import StaleRecoveryService
from app.application.services.outbox_relay import OutboxRelay
from app.application.services.summary_generator import SummaryGenerator
from app.application.services.summary_pipeline import SummaryPipeline
from app.application.services.summary_retry_sweeper import SummaryRetrySweeper
from app.application.services.summary_run_service import SummaryRunService
from app.application.services.summary_runner import SummaryRunner
from app.application.services.token_counter import HeuristicTokenCounter
from app.domain.enums.document_status import DocumentStatus
from app.infrastructure.chunking.factory import create_chunker
from app.infrastructure.config.settings import Settings, get_settings
from app.infrastructure.embeddings.gemini_embedding_provider import GeminiEmbeddingProvider
from app.infrastructure.embeddings.ollama_embedding_provider import OllamaEmbeddingProvider
from app.infrastructure.extraction.router import ExtractorRouter
from app.infrastructure.generation.gemini_generation_provider import GeminiGenerationProvider
from app.infrastructure.generation.ollama_generation_provider import OllamaGenerationProvider
from app.infrastructure.live_assistant.transcript_client import LiveAssistantTranscriptClient
from app.infrastructure.messaging.amqp import AmqpConnection
from app.infrastructure.messaging.amqp_outbox_publisher import AmqpOutboxPublisher
from app.infrastructure.messaging.masstransit import is_manual_request
from app.infrastructure.messaging.outbox_summary_publisher import OutboxSummaryPublisher
from app.infrastructure.messaging.summary_request_consumer import SummaryRequestConsumer
from app.infrastructure.ocr.tesseract_ocr_processor import TesseractOcrProcessor
from app.infrastructure.persistence.chunk_repository import SqlAlchemyChunkRepository
from app.infrastructure.persistence.database import get_session, get_session_factory
from app.infrastructure.persistence.document_repository import SqlAlchemyDocumentRepository
from app.infrastructure.persistence.outbox_repository import SqlAlchemyOutboxRepository
from app.infrastructure.persistence.summary_run_repository import (
    SqlAlchemySummaryRunRepository,
)
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


def build_embedding_provider(settings: Settings) -> EmbeddingProvider:
    """The EmbeddingProvider selected by EMBEDDING_PROVIDER: 'gemini' (hosted) or 'ollama' (local).

    NOT a free switch: EMBEDDING_DIM sets the pgvector column width, so changing provider or model
    requires an Alembic migration AND re-embedding every stored chunk. Vectors from two models are
    not comparable — mixing them returns confident nonsense instead of an error.
    """
    provider = settings.embedding_provider.strip().lower()
    if provider == "gemini":
        return GeminiEmbeddingProvider(settings)
    if provider == "ollama":
        return OllamaEmbeddingProvider(settings)
    logger.warning(
        "Unknown EMBEDDING_PROVIDER '%s'; falling back to ollama.", settings.embedding_provider
    )
    return OllamaEmbeddingProvider(settings)


def get_embedding_provider(settings: SettingsDep) -> EmbeddingProvider:
    return build_embedding_provider(settings)


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


def build_generation_provider(
    settings: Settings,
    *,
    ollama_model: str,
    gemini_model: str,
    temperature: float,
    max_tokens: int,
) -> GenerationProvider:
    """The GenerationProvider selected by GENERATION_PROVIDER: 'gemini' or 'ollama'.

    The model name is per-backend (an Ollama tag and a Gemini model are not interchangeable)
    while temperature and the token budget are portable, so callers pass both names and one
    set of parameters. That keeps answering and summarization on one switch while each keeps
    its own tuning.

    Unlike EMBEDDING_PROVIDER this is a free switch — completions are plain text, so nothing
    persisted depends on which backend produced it.
    """
    provider = settings.generation_provider.strip().lower()
    if provider == "gemini":
        return GeminiGenerationProvider(
            settings, model=gemini_model, temperature=temperature, max_tokens=max_tokens
        )
    if provider == "ollama":
        return OllamaGenerationProvider(
            settings, model=ollama_model, temperature=temperature, max_tokens=max_tokens
        )
    logger.warning(
        "Unknown GENERATION_PROVIDER '%s'; falling back to ollama.", settings.generation_provider
    )
    return OllamaGenerationProvider(
        settings, model=ollama_model, temperature=temperature, max_tokens=max_tokens
    )


def get_generation_provider(settings: SettingsDep) -> GenerationProvider:
    """Answering's generation client (Phase 10 ANSWER/GENERATION_* parameters)."""
    return build_generation_provider(
        settings,
        ollama_model=settings.generation_model,
        gemini_model=settings.gemini_generation_model,
        temperature=settings.generation_temperature,
        max_tokens=settings.generation_max_tokens,
    )


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
# whichever chat client GENERATION_PROVIDER selects, with the SUMMARY_* parameters, and
# the RetrievalService for optional classroom-scoped grounding.


def get_transcript_client(settings: SettingsDep) -> TranscriptClient:
    """Fetches a session's assembled transcript from LiveAssistantService (S-0)."""
    return LiveAssistantTranscriptClient(settings)


def get_summary_generation_provider(settings: SettingsDep) -> GenerationProvider:
    """The generation client configured with the SUMMARY_* parameters.

    Same GENERATION_PROVIDER switch as answering, but its own model and budget: a summary is
    the largest single generation the service performs, so it is the use case that most
    wants to be off the shared host CPU.
    """
    return build_generation_provider(
        settings,
        ollama_model=settings.summary_model,
        gemini_model=settings.gemini_summary_model,
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
# storage are shared across runs.


def _build_summary_run_service(session, settings) -> SummaryRunService:
    """Assemble the claim/retry service around one DB session.

    Both entry points (the AMQP consumer and the retry sweep) build it this way, so a run behaves
    identically however it was triggered.
    """
    retrieval = RetrievalService(
        build_embedding_provider(settings),
        SqlAlchemyChunkRepository(session),
        default_top_k=settings.search_default_top_k,
        max_top_k=settings.search_max_top_k,
    )
    generator = SummaryGenerator(
        LiveAssistantTranscriptClient(settings),
        retrieval,
        # build_generation_provider, NOT a hardcoded Ollama client: this path ignored
        # GENERATION_PROVIDER until 2026-07-30, so summaries silently ran on the local CPU model
        # even with the Gemini switch on.
        build_generation_provider(
            settings,
            ollama_model=settings.summary_model,
            gemini_model=settings.gemini_summary_model,
            temperature=settings.summary_temperature,
            max_tokens=settings.summary_max_tokens,
        ),
        settings,
    )
    # The publisher writes to the OUTBOX, not the broker, so the ready/failure message commits
    # with the run's terminal state instead of being lost to a broker outage.
    publisher: SummaryPublisher = OutboxSummaryPublisher(
        SqlAlchemyOutboxRepository(session), settings.rabbitmq_host
    )
    pipeline = SummaryPipeline(generator, _pdf_renderer, S3SummaryStorage(settings), publisher, settings)
    return SummaryRunService(
        SqlAlchemySummaryRunRepository(session),
        pipeline,
        _clock,
        max_attempts=settings.summary_max_attempts,
        retry_base_seconds=settings.summary_retry_base_seconds,
        retry_max_seconds=settings.summary_retry_max_seconds,
        stale_minutes=settings.summary_stale_minutes,
    )


def build_summary_runner() -> SummaryRunner:
    """Fire-and-forget runner used by the ops/debug HTTP endpoint.

    Goes through SummaryRunService so the HTTP path shares the claim, dedup and retry behaviour
    of the AMQP path rather than being a second, weaker implementation.
    """
    settings = get_settings()
    session_factory = get_session_factory()

    async def handle(session_id, classroom_id) -> None:
        async with session_factory() as session:
            service = _build_summary_run_service(session, settings)
            result = await service.request(session_id, classroom_id)
            await session.commit()
            if not result.claimed:
                logger.info(
                    "Summary for session %s is already claimed; skipping.", session_id
                )

    return SummaryRunner(handle)


def build_summary_consumer() -> SummaryRequestConsumer:
    """The AMQP consumer that replaces the session-end HTTP trigger."""
    settings = get_settings()
    session_factory = get_session_factory()
    runner = SummaryRunner(_summary_handle(session_factory, settings))

    async def on_request(session_id, classroom_id, reason) -> bool:
        """Claim durably, then generate in the background.

        The claim is committed BEFORE returning so the consumer can ack immediately: holding an
        AMQP message open for a minutes-long LLM job risks the broker's consumer timeout and a
        redelivery of work already in flight. If the process dies after the ack, the stale sweep
        finds the abandoned Running row and the retry loop re-runs it.
        """
        async with session_factory() as session:
            service = _build_summary_run_service(session, settings)
            if is_manual_request(reason):
                # A human asked again, so the run must be reopened first: Failed is terminal by
                # design (unclaimable, so the sweep leaves it alone), and without this the claim
                # below would lose and the request would vanish as a duplicate.
                await service.reopen(session_id, classroom_id)
            claimed = await service.claim(session_id, classroom_id)
            await session.commit()
        if claimed:
            runner.enqueue(session_id, classroom_id)
        return claimed

    return SummaryRequestConsumer(get_amqp_connection(), settings, on_request)


def _summary_handle(session_factory, settings):
    """Executes an ALREADY-CLAIMED run. Used by the consumer and the retry sweep."""

    async def handle(session_id, classroom_id) -> None:
        async with session_factory() as session:
            service = _build_summary_run_service(session, settings)
            await service.execute_claimed(session_id, classroom_id)
            await session.commit()

    return handle


def build_summary_retry_sweeper() -> SummaryRetrySweeper:
    """Drives due retries and returns abandoned Running rows to Pending."""
    settings = get_settings()
    session_factory = get_session_factory()
    runner = SummaryRunner(_summary_handle(session_factory, settings))

    async def sweep() -> int:
        async with session_factory() as session:
            service = _build_summary_run_service(session, settings)
            # Stale recovery first: it moves abandoned runs back to Pending, so the same pass can
            # then pick them up as due rather than waiting a full interval.
            await service.sweep_stale()
            due = await service.find_due(settings.summary_retry_batch_size)
            started = 0
            for session_id, classroom_id in due:
                if await service.claim(session_id, classroom_id):
                    started += 1
            await session.commit()
        # Enqueue outside the DB session: generation is minutes long and must not hold a
        # connection from the pool for its duration.
        for session_id, classroom_id in due:
            runner.enqueue(session_id, classroom_id)
        return started

    return SummaryRetrySweeper(sweep, settings.summary_retry_poll_seconds)


def build_outbox_relay() -> OutboxRelay:
    """Drains the outbox to the broker."""
    settings = get_settings()
    session_factory = get_session_factory()
    publisher = AmqpOutboxPublisher(get_amqp_connection())

    async def drain() -> int:
        async with session_factory() as session:
            outbox = SqlAlchemyOutboxRepository(session)
            pending = await outbox.fetch_unpublished(settings.outbox_batch_size)
            published = 0
            for message in pending:
                try:
                    await publisher.publish(message)
                except Exception as exc:  # noqa: BLE001 — one bad message must not stall the rest
                    await outbox.record_failure(message.id, f"{type(exc).__name__}: {exc}")
                    # Stop the pass: a broker that just refused one message will almost certainly
                    # refuse the next, and continuing would burn the whole batch's attempt counters
                    # on a single outage.
                    break
                await outbox.mark_published(message.id, _clock.now())
                published += 1
            await session.commit()
            return published

    return OutboxRelay(drain, settings.outbox_poll_seconds)


_amqp_connection: AmqpConnection | None = None


def get_amqp_connection() -> AmqpConnection:
    """The process-wide broker connection, shared by the relay and the consumer."""
    global _amqp_connection
    if _amqp_connection is None:
        _amqp_connection = AmqpConnection(get_settings())
    return _amqp_connection


async def close_amqp_connection() -> None:
    global _amqp_connection
    if _amqp_connection is not None:
        await _amqp_connection.close()
        _amqp_connection = None


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
    embedder = build_embedding_provider(settings)
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


# --- Re-embed runner ----------------------------------------------------------
# Built once at app startup and stored on app.state, like the ingestion worker and
# summary runner. Each batch opens its OWN short-lived session and commits, so a long
# sweep never holds one transaction open across hundreds of network round trips — and a
# crash mid-sweep leaves every already-committed batch persisted.


def build_reembed_runner() -> ReembedRunner:
    settings = get_settings()
    session_factory = get_session_factory()

    async def sweep(progress: ReembedProgress) -> None:
        # Guard FIRST, on its own session: refuse a mismatched embedder before spending
        # money and time embedding chunks the column will reject at write time.
        async with session_factory() as session:
            service = ReembedService(
                SqlAlchemyChunkRepository(session),
                build_embedding_provider(settings),
                expected_dim=settings.embedding_dim,
                batch_size=settings.reembed_batch_size,
            )
            await service.verify_dimension()
            progress.total = await service.repository.count_all()
            progress.remaining = await service.repository.count_missing_embeddings()

        logger.info(
            "reembed_started", extra={"total": progress.total, "pending": progress.remaining}
        )
        while True:
            async with session_factory() as session:
                service = ReembedService(
                    SqlAlchemyChunkRepository(session),
                    build_embedding_provider(settings),
                    expected_dim=settings.embedding_dim,
                    batch_size=settings.reembed_batch_size,
                )
                outcome = await service.run_batch()
                await session.commit()
            if outcome.embedded == 0:
                progress.remaining = 0
                break
            progress.embedded += outcome.embedded
            progress.remaining = outcome.remaining
            logger.info(
                "reembed_progress",
                extra={"embedded": progress.embedded, "remaining": progress.remaining},
            )
        logger.info("reembed_completed", extra={"embedded": progress.embedded})

    return ReembedRunner(sweep)


def get_reembed_runner(request: Request) -> ReembedRunner:
    runner: ReembedRunner | None = getattr(request.app.state, "reembed_runner", None)
    if runner is None:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="Re-embed runner is not running.",
        )
    return runner


ReembedRunnerDep = Annotated[ReembedRunner, Depends(get_reembed_runner)]
