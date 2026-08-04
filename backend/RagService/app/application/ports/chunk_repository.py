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
    async def delete_by_classroom_id(self, classroom_id: UUID) -> int:
        """Delete every chunk belonging to a classroom. Returns the number removed.

        Used when a classroom is deleted wholesale: looping the per-file delete would
        cost one round trip per document, while `chunks.classroom_id` is indexed and
        answers this in a single statement. Idempotent — a second call returns 0.
        """
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

    # -- reindex ---------------------------------------------------------------------------
    # A chunk with embedding IS NULL is "not yet embedded". That makes reindex naturally
    # resumable: the migration that changes EMBEDDING_DIM nulls the column, and re-running the
    # job simply continues from whatever is still NULL. No separate progress table needed.

    @abstractmethod
    async def count_missing_embeddings(self) -> int:
        """How many chunks still have no embedding (i.e. remain to be reindexed)."""
        raise NotImplementedError

    @abstractmethod
    async def count_all(self) -> int:
        """Total chunk count, for reporting progress as done/total."""
        raise NotImplementedError

    @abstractmethod
    async def fetch_missing_embeddings(self, limit: int) -> list[tuple[UUID, str]]:
        """Return up to ``limit`` (chunk_id, text) pairs whose embedding is NULL.

        Ordered deterministically so repeated calls make forward progress rather than
        re-serving the same rows after a partial failure.
        """
        raise NotImplementedError

    @abstractmethod
    async def set_embeddings(self, embeddings: dict[UUID, list[float]]) -> int:
        """Attach embeddings to existing chunks by id. Returns the number updated."""
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
