"""Give session summaries durable state: a run table (dedup + retry) and a transactional outbox.

Summaries had no persistence at all. Dedup was an in-memory set inside SummaryRunner that died
with the process, and a failed run was simply gone — one attempt, no record, no recovery. Two real
sessions on 2026-07-30 produced no summary and nothing anywhere said one was owed.

`summary_runs` fixes that by giving a summary the same shape ingestion already has: an atomic
claim, an attempt counter, an error field, and a stale sweep. The UNIQUE index on session_id is
load-bearing rather than cosmetic — the claim is an upsert whose ON CONFLICT target is that index,
so it is what makes two concurrent deliveries of the same session collapse into one winner. Losing
that race is how the service avoids paying for a second whole-lecture LLM call.

`outbox_messages` fixes the worse failure. Publishing used to happen inline after the upload, so a
broker that was unreachable for thirty seconds threw away a summary that had already been generated,
rendered and stored — and the failure notice could not publish either, leaving the classroom on a
Generating row forever. Writing the envelope here in the same transaction that marks the run
terminal makes the two atomic; a relay drains the table whenever the broker comes back.

Both tables follow the conventions in models.py: status as String(32) rather than a PG enum (so
adding a state needs no DDL), and explicit timezone-aware timestamps.

Revision ID: 0006_summary_runs_and_outbox
Revises: 0005_gemini_embedding_dim
"""

from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

# revision identifiers, used by Alembic.
revision: str = "0006_summary_runs_and_outbox"
down_revision: Union[str, None] = "0005_gemini_embedding_dim"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        "summary_runs",
        sa.Column("id", postgresql.UUID(as_uuid=True), primary_key=True, nullable=False),
        sa.Column("session_id", postgresql.UUID(as_uuid=True), nullable=False),
        # Nullable: a manual request may not carry it, and the pipeline learns it from the transcript.
        sa.Column("classroom_id", postgresql.UUID(as_uuid=True), nullable=True),
        sa.Column("status", sa.String(32), nullable=False, server_default="Pending"),
        sa.Column("attempts", sa.Integer(), nullable=False, server_default="0"),
        sa.Column("last_error", sa.Text(), nullable=True),
        sa.Column("next_attempt_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("started_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("completed_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column(
            "created_at_utc",
            sa.DateTime(timezone=True),
            nullable=False,
            server_default=sa.func.now(),
        ),
        sa.Column(
            "updated_at_utc",
            sa.DateTime(timezone=True),
            nullable=False,
            server_default=sa.func.now(),
        ),
    )
    # UNIQUE, and the claim depends on it: it is the ON CONFLICT target that serializes concurrent
    # deliveries. A plain index here would let duplicate runs through.
    op.create_index(
        "ix_summary_runs_session_id", "summary_runs", ["session_id"], unique=True
    )
    # The retry sweep scans by status and due-time.
    op.create_index("ix_summary_runs_status", "summary_runs", ["status"])
    op.create_index("ix_summary_runs_next_attempt_at", "summary_runs", ["next_attempt_at"])

    op.create_table(
        "outbox_messages",
        sa.Column("id", sa.BigInteger(), primary_key=True, autoincrement=True, nullable=False),
        sa.Column("message_id", postgresql.UUID(as_uuid=True), nullable=False),
        sa.Column("exchange", sa.String(512), nullable=False),
        sa.Column("message_type", sa.String(512), nullable=False),
        # The COMPLETE MassTransit envelope, so the relay never needs the domain object and a
        # message queued before a deploy still publishes correctly after it.
        sa.Column("payload", postgresql.JSONB(), nullable=False),
        sa.Column("correlation_id", postgresql.UUID(as_uuid=True), nullable=True),
        sa.Column("attempts", sa.Integer(), nullable=False, server_default="0"),
        sa.Column("last_error", sa.Text(), nullable=True),
        sa.Column("published_at_utc", sa.DateTime(timezone=True), nullable=True),
        sa.Column(
            "created_at_utc",
            sa.DateTime(timezone=True),
            nullable=False,
            server_default=sa.func.now(),
        ),
    )
    # The relay's work queue is "published_at_utc IS NULL, oldest first".
    op.create_index(
        "ix_outbox_messages_published_at_utc", "outbox_messages", ["published_at_utc"]
    )


def downgrade() -> None:
    op.drop_index("ix_outbox_messages_published_at_utc", table_name="outbox_messages")
    op.drop_table("outbox_messages")
    op.drop_index("ix_summary_runs_next_attempt_at", table_name="summary_runs")
    op.drop_index("ix_summary_runs_status", table_name="summary_runs")
    op.drop_index("ix_summary_runs_session_id", table_name="summary_runs")
    op.drop_table("summary_runs")
