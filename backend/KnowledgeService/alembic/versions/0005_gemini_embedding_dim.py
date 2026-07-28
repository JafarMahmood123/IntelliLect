"""Re-point the chunk embedding column at the configured embedder.

DESTRUCTIVE: this DROPS every stored embedding.

Vectors produced by two different models occupy unrelated spaces — a Gemini vector compared
against a qwen vector yields a plausible-looking number that means nothing, so search would return
confident nonsense rather than fail. There is no conversion; the only correct move when the
embedder changes is to discard the old vectors and re-embed. This migration nulls the column and
recreates it at the new width, and the chunk rows are expected to be re-ingested afterwards.

The column type is chosen from EMBEDDING_DIM, not hard-coded: pgvector's HNSW index refuses more
than 2000 dimensions for `vector`, but supports up to 4000 for `halfvec`. gemini-embedding-001 is
3072 natively, so above the limit the column (and the index operator class) must be halfvec.
Verified against pgvector 0.8.5: `vector(3072)` + hnsw => "column cannot have more than 2000
dimensions for hnsw index"; `halfvec(3072)` + halfvec_cosine_ops => index created.

Downgrade restores a 1024-wide `vector` column (the qwen3-embedding width) and likewise leaves it
empty — the old vectors are not recoverable from here either.
"""

from __future__ import annotations

from typing import Union

import sqlalchemy as sa
from alembic import op
from pgvector.sqlalchemy import HALFVEC, Vector

from app.infrastructure.config.settings import get_settings

# revision identifiers, used by Alembic.
revision: str = "0005_gemini_embedding_dim"
down_revision: Union[str, None] = "0004_documents_size_bytes"
branch_labels = None
depends_on = None

EMBEDDING_DIM = get_settings().embedding_dim
HNSW_VECTOR_DIM_LIMIT = 2000
_USE_HALFVEC = EMBEDDING_DIM > HNSW_VECTOR_DIM_LIMIT

_PREVIOUS_DIM = 1024  # qwen3-embedding, the width this migration replaces


def _column_type(dim: int, use_halfvec: bool):
    return HALFVEC(dim) if use_halfvec else Vector(dim)


def _ops(use_halfvec: bool) -> str:
    return "halfvec_cosine_ops" if use_halfvec else "vector_cosine_ops"


def _swap(new_dim: int, use_halfvec: bool) -> None:
    # The HNSW index is bound to the column, so it has to go first and be rebuilt after.
    op.drop_index("ix_chunks_embedding_hnsw", table_name="chunks")
    op.drop_column("chunks", "embedding")
    op.add_column(
        "chunks",
        sa.Column("embedding", _column_type(new_dim, use_halfvec), nullable=True),
    )
    op.create_index(
        "ix_chunks_embedding_hnsw",
        "chunks",
        ["embedding"],
        postgresql_using="hnsw",
        postgresql_with={"m": 16, "ef_construction": 64},
        postgresql_ops={"embedding": _ops(use_halfvec)},
    )


def upgrade() -> None:
    _swap(EMBEDDING_DIM, _USE_HALFVEC)


def downgrade() -> None:
    _swap(_PREVIOUS_DIM, _PREVIOUS_DIM > HNSW_VECTOR_DIM_LIMIT)
