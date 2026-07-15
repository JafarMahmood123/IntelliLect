"""initial schema: session transcripts + ordered segments

Revision ID: 0001_initial
Revises:
Create Date: 2026-07-15

Creates the transcript store (S-0): a session_transcripts header table keyed by
session_id, and a transcript_segments table holding FINAL segments ordered per session
by order_index. Indexes support ordered retrieval (session_id + order_index) and the
classroom scoping used by the summary feature later.
"""
from __future__ import annotations

from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects.postgresql import UUID as PgUUID

# revision identifiers, used by Alembic.
revision: str = "0001_initial"
down_revision: Union[str, None] = None
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        "session_transcripts",
        sa.Column("session_id", PgUUID(as_uuid=True), primary_key=True, nullable=False),
        sa.Column("classroom_id", PgUUID(as_uuid=True), nullable=False),
        sa.Column("status", sa.String(length=32), nullable=False, server_default="Recording"),
        sa.Column(
            "created_at", sa.DateTime(), nullable=False, server_default=sa.func.now()
        ),
        sa.Column(
            "updated_at", sa.DateTime(), nullable=False, server_default=sa.func.now()
        ),
    )
    op.create_index(
        "ix_session_transcripts_classroom_id", "session_transcripts", ["classroom_id"]
    )

    op.create_table(
        "transcript_segments",
        sa.Column("id", sa.BigInteger(), primary_key=True, autoincrement=True),
        sa.Column(
            "session_id",
            PgUUID(as_uuid=True),
            sa.ForeignKey("session_transcripts.session_id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column("order_index", sa.Integer(), nullable=False),
        sa.Column("text", sa.Text(), nullable=False),
        sa.Column("start_ms", sa.Integer(), nullable=False),
        sa.Column("end_ms", sa.Integer(), nullable=False),
        sa.Column(
            "created_at", sa.DateTime(), nullable=False, server_default=sa.func.now()
        ),
        sa.UniqueConstraint(
            "session_id", "order_index", name="uq_transcript_segment_order"
        ),
    )
    # Ordered retrieval per session.
    op.create_index(
        "ix_transcript_segments_session_order",
        "transcript_segments",
        ["session_id", "order_index"],
    )


def downgrade() -> None:
    op.drop_index(
        "ix_transcript_segments_session_order", table_name="transcript_segments"
    )
    op.drop_table("transcript_segments")
    op.drop_index(
        "ix_session_transcripts_classroom_id", table_name="session_transcripts"
    )
    op.drop_table("session_transcripts")
