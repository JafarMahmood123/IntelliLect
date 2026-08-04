"""Offline fakes for the retrieval tests (no Ollama, no Postgres)."""

from __future__ import annotations

import hashlib
from uuid import UUID, uuid4

from app.application.dtos.search_dtos import ChunkSearchResult
from app.application.ports.chunk_repository import ChunkRepository
from app.application.ports.embedding_provider import EmbeddingProvider


class FakeEmbeddingProvider(EmbeddingProvider):
    """Deterministic embeddings of length `dim`; records the last query embedded."""

    def __init__(self, dim: int) -> None:
        self._dim = dim
        self.embed_query_calls = 0
        self.last_query: str | None = None

    async def embed_documents(self, texts: list[str]) -> list[list[float]]:
        return [self._vector(text) for text in texts]

    async def embed_query(self, text: str) -> list[float]:
        self.embed_query_calls += 1
        self.last_query = text
        return self._vector(text)

    def _vector(self, text: str) -> list[float]:
        digest = hashlib.sha256(text.encode("utf-8")).digest()
        return [digest[i % len(digest)] / 255.0 for i in range(self._dim)]


class FakeChunkRepository(ChunkRepository):
    """Returns a preset, already-ordered result set and records the search args."""

    def __init__(self, results: list[ChunkSearchResult] | None = None) -> None:
        self._results = results or []
        self.searched_classroom_id: UUID | None = None
        self.searched_top_k: int | None = None
        self.searched_embedding: list[float] | None = None

    async def add_many(self, chunks, embeddings) -> None:  # unused here
        return None

    async def delete_by_document_id(self, document_id: UUID) -> int:  # unused here
        return 0

    async def delete_by_classroom_id(self, classroom_id: UUID) -> int:  # unused here
        return 0

    # The re-embed job's half of the port. Unused by retrieval and summary tests, but the
    # port is abstract, so omitting them makes this class uninstantiable — which is how
    # adding them to ChunkRepository silently broke 61 tests across four packages.
    async def count_missing_embeddings(self) -> int:  # unused here
        return 0

    async def count_all(self) -> int:  # unused here
        return 0

    async def fetch_missing_embeddings(self, limit: int) -> list[tuple[UUID, str]]:  # unused here
        return []

    async def set_embeddings(self, embeddings: dict[UUID, list[float]]) -> int:  # unused here
        return 0

    async def search(
        self, classroom_id: UUID, query_embedding: list[float], top_k: int
    ) -> list[ChunkSearchResult]:
        self.searched_classroom_id = classroom_id
        self.searched_top_k = top_k
        self.searched_embedding = query_embedding
        return self._results[:top_k]


def make_result(score: float, *, chunk_index: int = 0, **metadata) -> ChunkSearchResult:
    return ChunkSearchResult(
        chunk_id=uuid4(),
        document_id=uuid4(),
        text=f"chunk with score {score}",
        score=score,
        chunk_index=chunk_index,
        metadata=metadata,
    )
