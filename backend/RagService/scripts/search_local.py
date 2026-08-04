"""Run a retrieval query against the real stack — LIVE, and DEFERRED.

Embeds the query with the REAL EmbeddingProvider (Ollama) and searches the REAL
database's pgvector index, then prints the top-k chunks with scores and locations.

Requires:
  - Ollama running with the configured embedding model (qwen3-embedding), and
  - Postgres up with migrations applied AND some ingested/indexed data.

Intended for validation once the developer is back home with both services running.
It is NOT part of the offline test suite and will fail fast if either is unreachable.

Usage (from the RagService directory, with .env pointing at a live DB/Ollama):

    python scripts/search_local.py <classroom-uuid> "your natural language query" [--top-k 8]
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from uuid import UUID

from app.application.dtos.search_dtos import SearchRequest
from app.application.services.retrieval_service import RetrievalService
from app.infrastructure.config.settings import get_settings
from app.infrastructure.embeddings.ollama_embedding_provider import (
    OllamaEmbeddingError,
    OllamaEmbeddingProvider,
)
from app.infrastructure.persistence.chunk_repository import SqlAlchemyChunkRepository
from app.infrastructure.persistence.database import dispose_engine, get_session_factory


def _location(metadata: dict) -> str:
    if "page" in metadata:
        return f"page {metadata['page']}"
    if "slide" in metadata:
        return f"slide {metadata['slide']}"
    if "section" in metadata:
        return f"[{metadata['section']}]"
    return "-"


async def _run(classroom_id: UUID, query: str, top_k: int | None) -> int:
    settings = get_settings()
    embedder = OllamaEmbeddingProvider(settings)
    session_factory = get_session_factory()

    async with session_factory() as session:
        service = RetrievalService(
            embedder,
            SqlAlchemyChunkRepository(session),
            default_top_k=settings.search_default_top_k,
            max_top_k=settings.search_max_top_k,
        )
        try:
            response = await service.search(
                SearchRequest(classroom_id=classroom_id, query=query, top_k=top_k)
            )
        except OllamaEmbeddingError as exc:
            print(f"Embedding failed (is Ollama running?): {exc}", file=sys.stderr)
            return 1

    print(f"Classroom: {classroom_id}")
    print(f"Query:     {query!r}")
    print(f"Results:   {len(response.results)}")
    print("\n--- Top-k chunks ---")
    if not response.results:
        print("  (none — is anything indexed for this classroom?)")
    for rank, item in enumerate(response.results, start=1):
        snippet = " ".join(item.text.split())
        if len(snippet) > 100:
            snippet = snippet[:99] + "…"
        print(
            f"  {rank:>2}. score={item.score:.4f} {_location(item.metadata):<12} "
            f"doc={str(item.document_id)[:8]} #{item.chunk_index}  {snippet}"
        )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Live retrieval query (needs Ollama + Postgres).")
    parser.add_argument("classroom_id", type=UUID, help="Classroom UUID to search within")
    parser.add_argument("query", type=str, help="Natural-language query")
    parser.add_argument("--top-k", type=int, default=None, help="Number of chunks to return")
    args = parser.parse_args(argv)

    async def _main() -> int:
        try:
            return await _run(args.classroom_id, args.query, args.top_k)
        finally:
            await dispose_engine()

    return asyncio.run(_main())


if __name__ == "__main__":
    raise SystemExit(main())
