from __future__ import annotations

from datetime import datetime
from uuid import UUID

from sqlalchemy import BigInteger, ForeignKey, Integer, String, Text, UniqueConstraint, func
from sqlalchemy.dialects.postgresql import UUID as PgUUID
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, relationship

from app.domain.transcript.transcript_status import TranscriptStatus


class Base(DeclarativeBase):
    pass


class SessionTranscriptModel(Base):
    """Header row for one session's transcript (S-0). Keyed by session_id."""

    __tablename__ = "session_transcripts"

    session_id: Mapped[UUID] = mapped_column(PgUUID(as_uuid=True), primary_key=True)
    classroom_id: Mapped[UUID] = mapped_column(
        PgUUID(as_uuid=True), nullable=False, index=True
    )
    status: Mapped[str] = mapped_column(
        String(32), nullable=False, default=TranscriptStatus.RECORDING.value
    )
    created_at: Mapped[datetime] = mapped_column(
        nullable=False, server_default=func.now()
    )
    updated_at: Mapped[datetime] = mapped_column(
        nullable=False, server_default=func.now(), onupdate=func.now()
    )

    segments: Mapped[list[TranscriptSegmentModel]] = relationship(
        back_populates="transcript", cascade="all, delete-orphan", passive_deletes=True
    )


class TranscriptSegmentModel(Base):
    """One persisted FINAL segment, ordered within a session by ``order_index`` (S-0)."""

    __tablename__ = "transcript_segments"
    __table_args__ = (
        # Ordered retrieval + the natural per-session sequence guard.
        UniqueConstraint("session_id", "order_index", name="uq_transcript_segment_order"),
    )

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    session_id: Mapped[UUID] = mapped_column(
        PgUUID(as_uuid=True),
        ForeignKey("session_transcripts.session_id", ondelete="CASCADE"),
        nullable=False,
        index=True,
    )
    order_index: Mapped[int] = mapped_column(Integer, nullable=False)
    text: Mapped[str] = mapped_column(Text, nullable=False)
    start_ms: Mapped[int] = mapped_column(Integer, nullable=False)
    end_ms: Mapped[int] = mapped_column(Integer, nullable=False)
    created_at: Mapped[datetime] = mapped_column(
        nullable=False, server_default=func.now()
    )

    transcript: Mapped[SessionTranscriptModel] = relationship(back_populates="segments")
