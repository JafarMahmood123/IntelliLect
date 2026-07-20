"""Index documents.classroom_id for classroom-scoped de-indexing.

Deleting a classroom removes every document belonging to it in one statement.
`chunks.classroom_id` was already indexed; `documents.classroom_id` was not, so
that delete (and any future classroom-scoped document query) was a seq scan.

Revision ID: 0003_documents_classroom_index
Revises: 0002_document_lifecycle
"""

from typing import Sequence, Union

from alembic import op

# revision identifiers, used by Alembic.
revision: str = "0003_documents_classroom_index"
down_revision: Union[str, None] = "0002_document_lifecycle"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_index(
        "ix_documents_classroom_id", "documents", ["classroom_id"], unique=False
    )


def downgrade() -> None:
    op.drop_index("ix_documents_classroom_id", table_name="documents")
