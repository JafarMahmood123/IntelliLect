from __future__ import annotations

from datetime import datetime
from typing import Any
from uuid import UUID, uuid4

from pgvector.sqlalchemy import HALFVEC, Vector
from sqlalchemy import (
    BigInteger,
    DateTime,
    ForeignKey,
    Integer,
    String,
    Text,
    func,
)
from sqlalchemy.dialects.postgresql import JSONB, UUID as PgUUID
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, relationship

from app.domain.enums.chunk_source import ChunkSource
from app.domain.enums.document_status import DocumentStatus
from app.domain.enums.summary_run_status import SummaryRunStatus
from app.infrastructure.config.settings import get_settings

# The pgvector column dimension is bound to the configured EMBEDDING_DIM at import
# time. The Alembic migration reads the same setting, so schema and models agree.
EMBEDDING_DIM = get_settings().embedding_dim

# pgvector's HNSW index refuses more than 2000 dimensions for `vector`, but supports up to 4000
# for `halfvec` (fp16). gemini-embedding-001 is 3072 natively, so anything above the limit MUST
# use halfvec or the index cannot be created at all. Both types expose cosine_distance(), so the
# repository query is identical either way.
HNSW_VECTOR_DIM_LIMIT = 2000
EmbeddingColumnType = (
    HALFVEC(EMBEDDING_DIM) if EMBEDDING_DIM > HNSW_VECTOR_DIM_LIMIT else Vector(EMBEDDING_DIM)
)


class Base(DeclarativeBase):
    pass


class DocumentModel(Base):
    __tablename__ = "documents"

    id: Mapped[UUID] = mapped_column(PgUUID(as_uuid=True), primary_key=True, default=uuid4)
    classroom_id: Mapped[UUID] = mapped_column(
        PgUUID(as_uuid=True), nullable=False, index=True
    )
    file_id: Mapped[UUID] = mapped_column(
        PgUUID(as_uuid=True), nullable=False, unique=True, index=True
    )
    s3_key: Mapped[str] = mapped_column(String(1024), nullable=False)
    file_name: Mapped[str] = mapped_column(String(512), nullable=False)
    content_type: Mapped[str] = mapped_column(String(255), nullable=False)
    # Source-file size in bytes, denormalized from ClassroomService at ingest (see Document).
    size_bytes: Mapped[int] = mapped_column(
        BigInteger, nullable=False, default=0, server_default="0"
    )
    content_hash: Mapped[str | None] = mapped_column(String(128), nullable=True)
    status: Mapped[DocumentStatus] = mapped_column(
        String(32), nullable=False, default=DocumentStatus.PENDING.value
    )
    last_error: Mapped[str | None] = mapped_column(Text, nullable=True)
    attempts: Mapped[int] = mapped_column(
        Integer, nullable=False, default=0, server_default="0"
    )
    processing_started_at: Mapped[datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True
    )
    created_at_utc: Mapped[datetime] = mapped_column(
        nullable=False, server_default=func.now()
    )
    updated_at_utc: Mapped[datetime] = mapped_column(
        nullable=False, server_default=func.now(), onupdate=func.now()
    )

    chunks: Mapped[list[ChunkModel]] = relationship(
        back_populates="document", cascade="all, delete-orphan", passive_deletes=True
    )


class ChunkModel(Base):
    __tablename__ = "chunks"

    id: Mapped[UUID] = mapped_column(PgUUID(as_uuid=True), primary_key=True, default=uuid4)
    document_id: Mapped[UUID] = mapped_column(
        PgUUID(as_uuid=True),
        ForeignKey("documents.id", ondelete="CASCADE"),
        nullable=False,
    )
    classroom_id: Mapped[UUID] = mapped_column(
        PgUUID(as_uuid=True), nullable=False, index=True
    )
    chunk_index: Mapped[int] = mapped_column(Integer, nullable=False)
    text: Mapped[str] = mapped_column(Text, nullable=False)
    token_count: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    chunk_metadata: Mapped[dict[str, Any]] = mapped_column(
        "metadata", JSONB, nullable=False, default=dict
    )
    source: Mapped[ChunkSource] = mapped_column(
        String(32), nullable=False, default=ChunkSource.TEXT.value
    )
    # Truncated, L2-normalized embedding. Populated once chunking/embedding lands.
    embedding: Mapped[list[float] | None] = mapped_column(EmbeddingColumnType, nullable=True)
    created_at_utc: Mapped[datetime] = mapped_column(
        nullable=False, server_default=func.now()
    )

    document: Mapped[DocumentModel] = relationship(back_populates="chunks")


class SummaryRunModel(Base):
    """One row per session whose summary has been requested. The unit of dedup and retry.

    Before this table the service kept nothing about a summary: dedup was an in-memory set in
    SummaryRunner that died with the process, and a failed run was simply gone. Deliberately
    shaped like ``documents`` so the ingestion idioms transfer unchanged — the atomic claim,
    ``attempts``, ``last_error``, and the stale-RUNNING sweep.

    ``session_id`` is UNIQUE and that is load-bearing, not tidiness: the claim is an upsert whose
    ON CONFLICT target is this index, so it is what serializes two concurrent deliveries of the
    same session into one winner. Without it both would generate, and a summary is a whole-lecture
    LLM call.
    """

    __tablename__ = "summary_runs"

    id: Mapped[UUID] = mapped_column(PgUUID(as_uuid=True), primary_key=True, default=uuid4)
    session_id: Mapped[UUID] = mapped_column(
        PgUUID(as_uuid=True), nullable=False, unique=True, index=True
    )
    # Nullable because a manual request may not know it; the pipeline learns it from the transcript.
    classroom_id: Mapped[UUID | None] = mapped_column(PgUUID(as_uuid=True), nullable=True)
    status: Mapped[SummaryRunStatus] = mapped_column(
        String(32), nullable=False, default=SummaryRunStatus.PENDING.value, index=True
    )
    attempts: Mapped[int] = mapped_column(
        Integer, nullable=False, default=0, server_default="0"
    )
    last_error: Mapped[str | None] = mapped_column(Text, nullable=True)
    # When the retry sweep may claim this row again. NULL means "not scheduled" — either it is
    # running, terminal, or waiting on a fresh request.
    next_attempt_at: Mapped[datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True, index=True
    )
    # Set on claim; how the stale sweep recognises a RUNNING row whose process died.
    started_at: Mapped[datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True
    )
    completed_at: Mapped[datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True
    )
    created_at_utc: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, server_default=func.now()
    )
    updated_at_utc: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        nullable=False,
        server_default=func.now(),
        onupdate=func.now(),
    )


class OutboxMessageModel(Base):
    """Transactional outbox: messages committed with the work that produced them.

    THE PROBLEM THIS SOLVES. Publishing used to happen inline at the end of the pipeline. On
    2026-07-30 the broker hostname was wrong, so summaries were generated, rendered, and uploaded
    to S3 — and then the publish threw. The artifacts existed, nobody was told, and the classroom
    sat on a Generating row forever. The *failure* notice could not publish either, so the run did
    not even reach Failed. A whole-lecture LLM call, paid for and discarded.

    Writing the message here in the SAME transaction that marks the run terminal makes that
    impossible: either both land or neither does. A relay drains the table afterwards and can take
    as long as the broker needs.

    ``id`` is a bigserial rather than a UUID because the relay publishes in insertion order, and
    ``published_at_utc IS NULL`` is the work queue.
    """

    __tablename__ = "outbox_messages"

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    message_id: Mapped[UUID] = mapped_column(
        PgUUID(as_uuid=True), nullable=False, default=uuid4
    )
    # Fanout exchange name, which is also MassTransit's message-type string.
    exchange: Mapped[str] = mapped_column(String(512), nullable=False)
    message_type: Mapped[str] = mapped_column(String(512), nullable=False)
    # The COMPLETE MassTransit envelope, not just the payload. Serializing at write time means the
    # relay is a dumb pipe: it never needs the domain object, so a message queued by an older
    # version still publishes correctly after a deploy.
    payload: Mapped[dict[str, Any]] = mapped_column(JSONB, nullable=False)
    correlation_id: Mapped[UUID | None] = mapped_column(PgUUID(as_uuid=True), nullable=True)
    attempts: Mapped[int] = mapped_column(
        Integer, nullable=False, default=0, server_default="0"
    )
    last_error: Mapped[str | None] = mapped_column(Text, nullable=True)
    # NULL = still owed. Kept rather than deleted so a publish can be audited after the fact.
    published_at_utc: Mapped[datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True, index=True
    )
    created_at_utc: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, server_default=func.now()
    )
