"""A deterministic ``RetrievalClient`` for testing LA-4 without KnowledgeService.

Returns a fixed list of ``RetrievedChunk``s (or raises a configured error) and records
each call, so tests can assert the query/top_k passed and drive the evaluator's paths.
"""

from __future__ import annotations

from uuid import UUID

from app.application.ports.retrieval_client import RetrievalClient
from app.domain.evaluation.retrieved_chunk import RetrievedChunk


class FakeRetrievalClient(RetrievalClient):
    def __init__(
        self, chunks: list[RetrievedChunk] | None = None, *, error: Exception | None = None
    ) -> None:
        self._chunks = list(chunks or [])
        self._error = error
        self.calls: list[tuple[UUID, str, int]] = []  # (classroom_id, query, top_k)

    async def retrieve(
        self, classroom_id: UUID, query_text: str, top_k: int
    ) -> list[RetrievedChunk]:
        self.calls.append((classroom_id, query_text, top_k))
        if self._error is not None:
            raise self._error
        return list(self._chunks)
