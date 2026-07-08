from __future__ import annotations

from collections.abc import Sequence
from uuid import UUID

from sqlalchemy import delete
from sqlalchemy.ext.asyncio import AsyncSession

from app.application.ports.chunk_repository import ChunkRepository
from app.domain.entities.chunk import Chunk
from app.infrastructure.persistence.models import ChunkModel


class SqlAlchemyChunkRepository(ChunkRepository):
    """ChunkRepository backed by PostgreSQL + pgvector via async SQLAlchemy."""

    def __init__(self, session: AsyncSession) -> None:
        self._session = session

    async def add_many(
        self, chunks: Sequence[Chunk], embeddings: Sequence[list[float]]
    ) -> None:
        if len(chunks) != len(embeddings):
            raise ValueError("chunks and embeddings must be the same length")
        models = [
            ChunkModel(
                id=chunk.id,
                document_id=chunk.document_id,
                classroom_id=chunk.classroom_id,
                chunk_index=chunk.chunk_index,
                text=chunk.text,
                token_count=chunk.token_count,
                chunk_metadata=chunk.metadata,
                source=chunk.source.value,
                embedding=embedding,
            )
            for chunk, embedding in zip(chunks, embeddings, strict=True)
        ]
        self._session.add_all(models)
        await self._session.flush()

    async def delete_by_document_id(self, document_id: UUID) -> int:
        stmt = delete(ChunkModel).where(ChunkModel.document_id == document_id)
        result = await self._session.execute(stmt)
        await self._session.flush()
        return result.rowcount or 0
