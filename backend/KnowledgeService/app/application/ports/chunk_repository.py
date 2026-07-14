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

    async def replace_for_document(
        self,
        document_id: UUID,
        chunks: Sequence[Chunk],
        embeddings: Sequence[list[float]],
    ) -> None:
        """Atomically replace a document's chunks with a new set.

        Default implementation deletes then inserts within the caller's transaction,
        so a failure rolls both back and never leaves a partial/duplicated set. In-
        memory implementations should override this to be all-or-nothing.
        """
        await self.delete_by_document_id(document_id)
        if chunks:
            await self.add_many(chunks, embeddings)

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
