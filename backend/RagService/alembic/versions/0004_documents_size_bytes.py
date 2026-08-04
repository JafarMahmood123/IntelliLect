"""Denormalize source-file size onto documents for the knowledge-base admin view.

The super-admin content/knowledge-base management list and stats need each document's
source-file size (and total storage consumed). Size lives in ClassroomService; it is now
sent at ingest and stored here. Existing rows default to 0 until re-ingested.

Revision ID: 0004_documents_size_bytes
Revises: 0003_documents_classroom_index
"""

from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision: str = "0004_documents_size_bytes"
down_revision: Union[str, None] = "0003_documents_classroom_index"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.add_column(
        "documents",
        sa.Column("size_bytes", sa.BigInteger(), nullable=False, server_default="0"),
    )


def downgrade() -> None:
    op.drop_column("documents", "size_bytes")
