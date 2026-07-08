from abc import ABC, abstractmethod
from collections.abc import Sequence
from uuid import UUID

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
