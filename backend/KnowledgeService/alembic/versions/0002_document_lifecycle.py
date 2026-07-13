"""document lifecycle fields: attempts, processing_started_at, last_error

Revision ID: 0002_document_lifecycle
Revises: 0001_initial
Create Date: 2026-07-13

Adds ingestion-robustness columns to `documents` (Phase 8):
- attempts (int, default 0) — ingestion attempts so far
- processing_started_at (timestamptz, null) — set while Processing; drives stale
  detection
and renames the existing `error` column to `last_error` (kept, not dropped).
Applies cleanly on top of 0001_initial.
"""
from __future__ import annotations

from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision: str = "0002_document_lifecycle"
down_revision: Union[str, None] = "0001_initial"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.add_column(
        "documents",
        sa.Column("attempts", sa.Integer(), nullable=False, server_default="0"),
    )
    op.add_column(
        "documents",
        sa.Column("processing_started_at", sa.DateTime(timezone=True), nullable=True),
    )
    # Preserve any existing error text under the new name.
    op.alter_column("documents", "error", new_column_name="last_error")


def downgrade() -> None:
    op.alter_column("documents", "last_error", new_column_name="error")
    op.drop_column("documents", "processing_started_at")
    op.drop_column("documents", "attempts")
