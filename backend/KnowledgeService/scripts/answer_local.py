"""Answer a question over a classroom's materials — LIVE, and DEFERRED.

Runs the real RAG pipeline: the real Ollama embedder + pgvector retrieval, then the
real qwen2.5:7b-instruct generator. Prints the grounded answer and its cited sources.

Requires:
  - Ollama running with BOTH models pulled:
      ollama pull qwen3-embedding
      ollama pull qwen2.5:7b-instruct
  - Postgres up with migrations applied AND indexed data for the classroom.

Intended for validation once the developer is back home. NOT part of the offline
suite; it fails fast if Ollama or Postgres is unreachable.

Usage (from the KnowledgeService directory, with .env pointing at a live DB/Ollama):

    python scripts/answer_local.py <classroom-uuid> "your question" [--top-k 6]
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from uuid import UUID

from app.application.services.answer_service import AnswerService
from app.application.services.context_builder import ContextBuilder
from app.application.services.retrieval_service import RetrievalService
from app.application.services.token_counter import HeuristicTokenCounter
from app.infrastructure.config.settings import get_settings
from app.infrastructure.embeddings.ollama_embedding_provider import (
    OllamaEmbeddingError,
    OllamaEmbeddingProvider,
)
from app.infrastructure.generation.ollama_generation_provider import (
    OllamaGenerationError,
    OllamaGenerationProvider,
)
from app.infrastructure.persistence.chunk_repository import SqlAlchemyChunkRepository
from app.infrastructure.persistence.database import dispose_engine, get_session_factory


def _location(source) -> str:
    if source.page is not None:
        return f"page {source.page}"
    if source.slide is not None:
        return f"slide {source.slide}"
    if source.section:
        return f"[{source.section}]"
    return "-"


async def _run(classroom_id: UUID, question: str, top_k: int | None) -> int:
    settings = get_settings()
    embedder = OllamaEmbeddingProvider(settings)
    generator = OllamaGenerationProvider(settings)
    session_factory = get_session_factory()

    async with session_factory() as session:
        service = AnswerService(
            RetrievalService(
                embedder,
                SqlAlchemyChunkRepository(session),
                default_top_k=settings.search_default_top_k,
                max_top_k=settings.search_max_top_k,
            ),
            ContextBuilder(HeuristicTokenCounter(), settings.context_max_tokens),
            generator,
            default_top_k=settings.answer_top_k,
        )
        try:
            response = await service.answer(classroom_id, question, top_k)
        except (OllamaEmbeddingError, OllamaGenerationError) as exc:
            print(
                f"Failed (is Ollama up with both models?): {exc}", file=sys.stderr
            )
            return 1

    print(f"Classroom: {classroom_id}")
    print(f"Question:  {question!r}\n")
    print("--- Answer ---")
    print(response.answer)
    print("\n--- Sources ---")
    if not response.sources:
        print("  (none)")
    for source in response.sources:
        print(
            f"  [{source.citation}] {_location(source):<12} "
            f"score={source.score:.4f} doc={str(source.document_id)[:8]} "
            f"chunk={str(source.chunk_id)[:8]}"
        )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Live grounded answering (needs Ollama + Postgres)."
    )
    parser.add_argument("classroom_id", type=UUID, help="Classroom UUID to answer within")
    parser.add_argument("question", type=str, help="Natural-language question")
    parser.add_argument("--top-k", type=int, default=None, help="Chunks to retrieve")
    args = parser.parse_args(argv)

    async def _main() -> int:
        try:
            return await _run(args.classroom_id, args.question, args.top_k)
        finally:
            await dispose_engine()

    return asyncio.run(_main())


if __name__ == "__main__":
    raise SystemExit(main())
