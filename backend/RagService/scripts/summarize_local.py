"""Run the full session-summary pipeline for a session — LIVE, and DEFERRED (S-3).

Runs the REAL pipeline end to end: fetch the transcript from LiveAssistantService,
generate the Markdown with Ollama, render the PDF, upload BOTH artifacts to S3, and
publish the SessionSummaryReadyMessage. Prints the resulting S3 keys.

Requires the whole stack up:
  - LiveAssistantService running with the session's transcript persisted (S-0),
    LIVE_ASSISTANT_BASE_URL / INTERNAL_API_SECRET set;
  - Ollama with the summary model pulled (`ollama pull qwen2.5:7b-instruct`);
  - Postgres with indexed material for the classroom (grounding);
  - S3-compatible storage (SUMMARY_S3_* / S3_*) and the RabbitMQ broker (RABBITMQ_*).

NOT part of the offline suite; it fails fast if a dependency is unreachable.

    python scripts/summarize_local.py <session-uuid> [--classroom <uuid>]
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from uuid import UUID

from app.application.services.retrieval_service import RetrievalService
from app.application.services.summary_generator import SummaryGenerator
from app.application.services.summary_pipeline import SummaryPipeline
from app.infrastructure.config.settings import get_settings
from app.infrastructure.embeddings.ollama_embedding_provider import OllamaEmbeddingProvider
from app.infrastructure.generation.ollama_generation_provider import OllamaGenerationProvider
from app.infrastructure.live_assistant.transcript_client import LiveAssistantTranscriptClient
from app.infrastructure.messaging.masstransit_summary_publisher import (
    MassTransitSummaryPublisher,
)
from app.infrastructure.persistence.chunk_repository import SqlAlchemyChunkRepository
from app.infrastructure.persistence.database import dispose_engine, get_session_factory
from app.infrastructure.rendering.weasyprint_pdf_renderer import WeasyPrintPdfRenderer
from app.infrastructure.storage.s3_summary_storage import S3SummaryStorage


async def _run(session_id: UUID, classroom_id: UUID | None) -> int:
    settings = get_settings()
    session_factory = get_session_factory()
    async with session_factory() as session:
        generator = SummaryGenerator(
            LiveAssistantTranscriptClient(settings),
            RetrievalService(
                OllamaEmbeddingProvider(settings),
                SqlAlchemyChunkRepository(session),
                default_top_k=settings.search_default_top_k,
                max_top_k=settings.search_max_top_k,
            ),
            OllamaGenerationProvider(
                settings,
                model=settings.summary_model,
                temperature=settings.summary_temperature,
                max_tokens=settings.summary_max_tokens,
            ),
            settings,
        )
        pipeline = SummaryPipeline(
            generator,
            WeasyPrintPdfRenderer(),
            S3SummaryStorage(settings),
            MassTransitSummaryPublisher(settings),
            settings,
        )
        message = await pipeline.run(session_id, classroom_id)

    print(f"Session:   {message.session_id}")
    print(f"Classroom: {message.classroom_id}")
    print(f"Succeeded: {message.succeeded}")
    if message.succeeded:
        print(f"MD  key:   {message.md_s3_key}")
        print(f"PDF key:   {message.pdf_s3_key}")
    else:
        print(f"Error:     {message.error}", file=sys.stderr)
    return 0 if message.succeeded else 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Live session-summary pipeline (needs LiveAssistant + Ollama + S3 + broker)."
    )
    parser.add_argument("session_id", type=UUID, help="Session UUID to summarize")
    parser.add_argument(
        "--classroom", type=UUID, default=None,
        help="Classroom UUID (for failure reporting; resolved from the transcript otherwise).",
    )
    args = parser.parse_args(argv)

    async def _main() -> int:
        try:
            return await _run(args.session_id, args.classroom)
        finally:
            await dispose_engine()

    return asyncio.run(_main())


if __name__ == "__main__":
    raise SystemExit(main())
