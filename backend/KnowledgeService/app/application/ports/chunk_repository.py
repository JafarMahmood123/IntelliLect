from abc import ABC, abstractmethod
from collections.abc import Sequence
from uuid import UUID

from app.application.dtos.search_dtos import ChunkSearchResult
from app.domain.entities.chunk import Chunk


class ChunkRepository(ABC):
    """Persistence port for Chunk entities.

    Embedding vectors are supplied alongside chunks at persistence time; the
    concrete implementation stores them on the ORM model's pgvector column.
    """

    @abstractmethod
    async def add_many(
        self, chunks: Sequence[Chunk], embeddings: Sequence[list[float]]
    ) -> None:
        """Persist a batch of chunks with their aligned embedding vectors."""
        raise NotImplementedError

    @abstractmethod
    async def delete_by_document_id(self, document_id: UUID) -> int:
        """Delete all chunks for a document. Returns the number removed."""
        raise NotImplementedError

    @abstractmethod
    async def search(
        self, classroom_id: UUID, query_embedding: list[float], top_k: int
    ) -> list[ChunkSearchResult]:
        """Approximate-nearest-neighbour search within a single classroom.

        Orders by cosine distance ascending and returns the top_k best hits with a
        similarity score. The classroom filter is mandatory — implementations must
        never return chunks from another classroom.
        """
        raise NotImplementedError
