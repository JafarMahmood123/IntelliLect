"""TranscriptRepository (S-0) — InMemory unit tests + a skip-clean real-DB test.

The in-memory tests run everywhere (offline). The SQLAlchemy test is DEFERRED: it is
skipped cleanly unless TRANSCRIPT_TEST_DB_URL points at a reachable Postgres, e.g.

    TRANSCRIPT_TEST_DB_URL=postgresql+asyncpg://postgres:pw@localhost:5432/testdb \
        pytest tests/test_transcript_repository.py
"""

from __future__ import annotations

import os
from uuid import uuid4

import pytest

from app.domain.transcript.transcript_segment import TranscriptSegment
from app.domain.transcript.transcript_status import TranscriptStatus
from app.infrastructure.persistence.in_memory_transcript_repository import (
    InMemoryTranscriptRepository,
)


def _final(text: str, start_ms: int = 0, end_ms: int = 1000) -> TranscriptSegment:
    return TranscriptSegment(text, start_ms, end_ms, is_final=True, followed_by_pause=False)


# --- InMemory (always runs) ---------------------------------------------------
async def test_ensure_session_is_idempotent_and_does_not_reset_status():
    repo = InMemoryTranscriptRepository()
    sid, cid = uuid4(), uuid4()

    await repo.ensure_session(sid, cid)
    await repo.append_segment(sid, _final("hello"))
    await repo.finalize(sid)

    await repo.ensure_session(sid, cid)  # repeat must NOT wipe segments or status
    header = await repo.get_session_transcript(sid)
    assert header is not None and header.status is TranscriptStatus.FINALIZED
    assert len(await repo.get_transcript(sid)) == 1


async def test_append_assigns_sequential_index_and_get_is_ordered():
    repo = InMemoryTranscriptRepository()
    sid, cid = uuid4(), uuid4()
    await repo.ensure_session(sid, cid)

    for text in ["a", "b", "c"]:
        await repo.append_segment(sid, _final(text))

    stored = await repo.get_transcript(sid)
    assert [(s.order_index, s.text) for s in stored] == [(0, "a"), (1, "b"), (2, "c")]
    assert await repo.assemble_text(sid) == "a b c"


async def test_append_before_ensure_raises():
    repo = InMemoryTranscriptRepository()
    with pytest.raises(KeyError):
        await repo.append_segment(uuid4(), _final("x"))


async def test_unknown_session_reads_are_empty_or_none():
    repo = InMemoryTranscriptRepository()
    sid = uuid4()
    assert await repo.get_transcript(sid) == []
    assert await repo.assemble_text(sid) == ""
    assert await repo.get_session_transcript(sid) is None


async def test_assemble_text_strips_and_skips_empty_segments():
    repo = InMemoryTranscriptRepository()
    sid, cid = uuid4(), uuid4()
    await repo.ensure_session(sid, cid)
    for text in ["  padded  ", "   ", "next"]:
        await repo.append_segment(sid, _final(text))
    assert await repo.assemble_text(sid) == "padded next"


# --- SQLAlchemy over a real Postgres (deferred / skip-clean) -------------------
TRANSCRIPT_TEST_DB_URL = os.getenv("TRANSCRIPT_TEST_DB_URL")

pytestmark_db = pytest.mark.skipif(
    not TRANSCRIPT_TEST_DB_URL,
    reason="TRANSCRIPT_TEST_DB_URL not set — live transcript DB test is deferred",
)


@pytestmark_db
async def test_sqlalchemy_repository_round_trip() -> None:
    from sqlalchemy.ext.asyncio import async_sessionmaker, create_async_engine

    from app.infrastructure.persistence.models import Base
    from app.infrastructure.persistence.sqlalchemy_transcript_repository import (
        SqlAlchemyTranscriptRepository,
    )

    engine = create_async_engine(TRANSCRIPT_TEST_DB_URL)
    session_factory = async_sessionmaker(engine, expire_on_commit=False)
    sid, cid = uuid4(), uuid4()

    try:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.drop_all)
            await conn.run_sync(Base.metadata.create_all)

        repo = SqlAlchemyTranscriptRepository(session_factory)
        await repo.ensure_session(sid, cid)
        await repo.ensure_session(sid, cid)  # idempotent
        for text in ["first", "second", "third"]:
            await repo.append_segment(sid, _final(text))

        stored = await repo.get_transcript(sid)
        assert [(s.order_index, s.text) for s in stored] == [
            (0, "first"), (1, "second"), (2, "third")
        ]
        assert await repo.assemble_text(sid) == "first second third"

        header = await repo.get_session_transcript(sid)
        assert header is not None and header.status is TranscriptStatus.RECORDING
        assert header.classroom_id == cid

        await repo.finalize(sid)
        header = await repo.get_session_transcript(sid)
        assert header is not None and header.status is TranscriptStatus.FINALIZED
    finally:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.drop_all)
        await engine.dispose()
