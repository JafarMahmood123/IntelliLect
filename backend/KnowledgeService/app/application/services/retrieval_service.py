from __future__ import annotations

import logging

from app.application.dtos.search_dtos import (
    SearchRequest,
    SearchResponse,
    SearchResultItem,
)
from app.application.ports.chunk_repository import ChunkRepository
from app.application.ports.embedding_provider import EmbeddingProvider

logger = logging.getLogger("knowledge.retrieval")


class RetrievalService:
    """Embeds a query and returns the most similar chunks within one classroom.

    Depends only on ports, so it runs offline against fakes. Retrieval only — it
    returns chunks, never a generated answer (answer synthesis is a later phase).
    """

    def __init__(
        self,
        embedding_provider: EmbeddingProvider,
        chunk_repository: ChunkRepository,
        default_top_k: int = 8,
        max_top_k: int = 50,
    ) -> None:
        self._embedder = embedding_provider
        self._chunks = chunk_repository
        self._default_top_k = max(1, default_top_k)
        self._max_top_k = max(1, max_top_k)

    async def search(self, request: SearchRequest) -> SearchResponse:
        query = request.query.strip()
        if not query:
            raise ValueError("query must not be empty")

        top_k = self._resolve_top_k(request.top_k)

        # Instruction-aware query embedding (the provider prepends the retrieval
        # instruction for queries).
        embedding = await self._embedder.embed_query(query)
        results = await self._chunks.search(request.classroom_id, embedding, top_k)

        logger.info(
            "Search in classroom %s returned %d chunk(s) (top_k=%d).",
            request.classroom_id,
            len(results),
            top_k,
        )
        return SearchResponse(
            results=[SearchResultItem.from_result(result) for result in results]
        )

    def _resolve_top_k(self, requested: int | None) -> int:
        if requested is None:
            return self._default_top_k
        return max(1, min(requested, self._max_top_k))
