"""Real pgvector cosine-ordering test — DEFERRED.

Skipped cleanly unless TEST_DATABASE_URL points at a reachable Postgres with the
pgvector extension available (e.g. the pgvector/pgvector image). It verifies the
SQLAlchemy search() actually orders by cosine similarity and never crosses the
classroom boundary. Run it once the developer brings a test DB up:

    TEST_DATABASE_URL=postgresql+asyncpg://postgres:pw@localhost:5432/testdb \
        pytest tests/retrieval/test_pgvector_search.py
"""

from __future__ import annotations

import os
from uuid import uuid4

import pytest
from sqlalchemy import text
from sqlalchemy.ext.asyncio import async_sessionmaker, create_async_engine

from app.infrastructure.config.settings import Settings
from app.infrastructure.persistence.chunk_repository import SqlAlchemyChunkRepository
from app.infrastructure.persistence.models import Base, ChunkModel, DocumentModel

TEST_DATABASE_URL = os.getenv("TEST_DATABASE_URL")

pytestmark = pytest.mark.skipif(
    not TEST_DATABASE_URL,
    reason="TEST_DATABASE_URL not set — live pgvector test is deferred",
)

DIM = Settings().embedding_dim


def _basis(index: int) -> list[float]:
    """A unit basis vector e_index of length DIM (orthogonal for distinct indices)."""
    vector = [0.0] * DIM
    vector[index] = 1.0
    return vector


async def test_search_orders_by_cosine_and_scopes_to_classroom() -> None:
    engine = create_async_engine(TEST_DATABASE_URL)
    session_factory = async_sessionmaker(engine, expire_on_commit=False)
    classroom_a, classroom_b = uuid4(), uuid4()

    try:
        async with engine.begin() as conn:
            await conn.execute(text("CREATE EXTENSION IF NOT EXISTS vector"))
            await conn.run_sync(Base.metadata.create_all)

        async with session_factory() as session:
            document = DocumentModel(
                id=uuid4(),
                classroom_id=classroom_a,
                file_id=uuid4(),
                s3_key="k",
                file_name="f.pdf",
                content_type="application/pdf",
                status="Done",
            )
            session.add(document)
            await session.flush()

            # Two chunks in A (near/far from the query) and one in B (must be excluded).
            near = ChunkModel(id=uuid4(), document_id=document.id, classroom_id=classroom_a,
                              chunk_index=0, text="near", token_count=1, chunk_metadata={},
                              source="Text", embedding=_basis(0))
            far = ChunkModel(id=uuid4(), document_id=document.id, classroom_id=classroom_a,
                             chunk_index=1, text="far", token_count=1, chunk_metadata={},
                             source="Text", embedding=_basis(1))
            other = ChunkModel(id=uuid4(), document_id=document.id, classroom_id=classroom_b,
                               chunk_index=2, text="other", token_count=1, chunk_metadata={},
                               source="Text", embedding=_basis(0))
            session.add_all([near, far, other])
            await session.commit()

        async with session_factory() as session:
            results = await SqlAlchemyChunkRepository(session).search(
                classroom_a, _basis(0), top_k=10
            )

        texts = [r.text for r in results]
        assert texts == ["near", "far"]  # ordered by similarity, B excluded
        assert results[0].score > results[1].score
        assert results[0].score == pytest.approx(1.0, abs=1e-6)  # identical vector
    finally:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.drop_all)
        await engine.dispose()
