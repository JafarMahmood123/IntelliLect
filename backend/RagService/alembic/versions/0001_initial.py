"""initial schema: documents + chunks with pgvector

Revision ID: 0001_initial
Revises:
Create Date: 2026-07-08

Creates the vector extension, the documents and chunks tables, an HNSW index on
the chunk embeddings (cosine), and btree indexes on chunks.classroom_id and
documents.file_id. The embedding column dimension is read from Settings so it
stays in sync with the app's EMBEDDING_DIM.
"""
from __future__ import annotations

from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op
from pgvector.sqlalchemy import Vector
from sqlalchemy.dialects.postgresql import JSONB, UUID as PgUUID

from app.infrastructure.config.settings import get_settings

# revision identifiers, used by Alembic.
revision: str = "0001_initial"
down_revision: Union[str, None] = None
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None

EMBEDDING_DIM = get_settings().embedding_dim


def upgrade() -> None:
    # pgvector must exist before any Vector column is created.
    op.execute("CREATE EXTENSION IF NOT EXISTS vector")

    op.create_table(
        "documents",
        sa.Column("id", PgUUID(as_uuid=True), primary_key=True, nullable=False),
        sa.Column("classroom_id", PgUUID(as_uuid=True), nullable=False),
        sa.Column("file_id", PgUUID(as_uuid=True), nullable=False),
        sa.Column("s3_key", sa.String(length=1024), nullable=False),
        sa.Column("file_name", sa.String(length=512), nullable=False),
        sa.Column("content_type", sa.String(length=255), nullable=False),
        sa.Column("content_hash", sa.String(length=128), nullable=True),
        sa.Column("status", sa.String(length=32), nullable=False, server_default="Pending"),
        sa.Column("error", sa.Text(), nullable=True),
        sa.Column(
            "created_at_utc", sa.DateTime(), nullable=False, server_default=sa.func.now()
        ),
        sa.Column(
            "updated_at_utc", sa.DateTime(), nullable=False, server_default=sa.func.now()
        ),
    )
    op.create_index(
        "ix_documents_file_id", "documents", ["file_id"], unique=True
    )

    op.create_table(
        "chunks",
        sa.Column("id", PgUUID(as_uuid=True), primary_key=True, nullable=False),
        sa.Column(
            "document_id",
            PgUUID(as_uuid=True),
            sa.ForeignKey("documents.id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column("classroom_id", PgUUID(as_uuid=True), nullable=False),
        sa.Column("chunk_index", sa.Integer(), nullable=False),
        sa.Column("text", sa.Text(), nullable=False),
        sa.Column("token_count", sa.Integer(), nullable=False, server_default="0"),
        sa.Column("metadata", JSONB(), nullable=False, server_default="{}"),
        sa.Column("source", sa.String(length=32), nullable=False, server_default="Text"),
        sa.Column("embedding", Vector(EMBEDDING_DIM), nullable=True),
        sa.Column(
            "created_at_utc", sa.DateTime(), nullable=False, server_default=sa.func.now()
        ),
    )
    op.create_index("ix_chunks_classroom_id", "chunks", ["classroom_id"])
    op.create_index("ix_chunks_document_id", "chunks", ["document_id"])

    # HNSW index for approximate nearest-neighbor search under cosine distance.
    op.create_index(
        "ix_chunks_embedding_hnsw",
        "chunks",
        ["embedding"],
        postgresql_using="hnsw",
        postgresql_with={"m": 16, "ef_construction": 64},
        postgresql_ops={"embedding": "vector_cosine_ops"},
    )


def downgrade() -> None:
    op.drop_index("ix_chunks_embedding_hnsw", table_name="chunks")
    op.drop_index("ix_chunks_document_id", table_name="chunks")
    op.drop_index("ix_chunks_classroom_id", table_name="chunks")
    op.drop_table("chunks")
    op.drop_index("ix_documents_file_id", table_name="documents")
    op.drop_table("documents")
    # The `vector` extension is left installed intentionally.
