from __future__ import annotations

from collections.abc import Sequence
from uuid import UUID

from sqlalchemy import delete, func, select, update
from sqlalchemy.ext.asyncio import AsyncSession

from app.application.dtos.search_dtos import ChunkSearchResult
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

    async def delete_by_classroom_id(self, classroom_id: UUID) -> int:
        # Single statement over the indexed classroom_id column, rather than one
        # round trip per document.
        stmt = delete(ChunkModel).where(ChunkModel.classroom_id == classroom_id)
        result = await self._session.execute(stmt)
        await self._session.flush()
        return result.rowcount or 0

    # -- reindex ---------------------------------------------------------------------------
    async def count_missing_embeddings(self) -> int:
        stmt = select(func.count()).select_from(ChunkModel).where(
            ChunkModel.embedding.is_(None)
        )
        return int((await self._session.execute(stmt)).scalar_one())

    async def count_all(self) -> int:
        stmt = select(func.count()).select_from(ChunkModel)
        return int((await self._session.execute(stmt)).scalar_one())

    async def fetch_missing_embeddings(self, limit: int) -> list[tuple[UUID, str]]:
        # Ordered by primary key so a partially-failed batch does not cause the next call to
        # re-serve the same rows in a different order and stall progress.
        stmt = (
            select(ChunkModel.id, ChunkModel.text)
            .where(ChunkModel.embedding.is_(None))
            .order_by(ChunkModel.id)
            .limit(limit)
        )
        rows = (await self._session.execute(stmt)).all()
        return [(row[0], row[1]) for row in rows]

    async def set_embeddings(self, embeddings: dict[UUID, list[float]]) -> int:
        if not embeddings:
            return 0
        updated = 0
        for chunk_id, vector in embeddings.items():
            stmt = (
                update(ChunkModel)
                .where(ChunkModel.id == chunk_id)
                .values(embedding=vector)
            )
            result = await self._session.execute(stmt)
            updated += result.rowcount or 0
        await self._session.flush()
        return updated

    async def search(
        self, classroom_id: UUID, query_embedding: list[float], top_k: int
    ) -> list[ChunkSearchResult]:
        # pgvector cosine distance (`<=>`); the HNSW index uses vector_cosine_ops.
        distance = ChunkModel.embedding.cosine_distance(query_embedding)
        stmt = (
            select(ChunkModel, distance.label("distance"))
            .where(ChunkModel.classroom_id == classroom_id)  # mandatory scope
            .where(ChunkModel.embedding.is_not(None))
            .order_by(distance.asc())
            .limit(top_k)
        )
        rows = (await self._session.execute(stmt)).all()
        return [
            ChunkSearchResult(
                chunk_id=model.id,
                document_id=model.document_id,
                text=model.text,
                score=1.0 - float(distance_value),
                chunk_index=model.chunk_index,
                metadata=dict(model.chunk_metadata or {}),
            )
            for model, distance_value in rows
        ]
