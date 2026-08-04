"""Re-embed stored chunks with the currently configured EmbeddingProvider.

DISTINCT FROM the per-document /reindex endpoints, which re-INGEST a file end to end (fetch from
S3, extract, OCR, chunk, embed). This only recomputes vectors from chunk text already in the
database: no object storage, no extraction, no re-chunking. Use this when the EMBEDDER changed;
use /reindex when the FILE or its extraction changed.

WHY THIS EXISTS. ``EMBEDDING_DIM`` is schema — it sets the pgvector column width — so changing the
embedding provider, model, or dimension requires an Alembic migration that DROPS every stored
vector (vectors from two models are not comparable; mixing them returns confident nonsense rather
than an error). Without this job the only recovery is re-uploading every document by hand, which
does not scale and leaves a half-populated corpus if it is interrupted.

RESUMABILITY COMES FROM THE SCHEMA, not from bookkeeping. ``embedding IS NULL`` means "not yet
embedded", which is exactly the state the migration leaves behind. So a crashed or cancelled run
is recovered by simply running it again — it picks up whatever is still NULL. There is no progress
table to get out of sync with reality.

THE DIMENSION GUARD IS THE IMPORTANT PART. The provider is asked for one probe vector BEFORE any
work starts, and the run is refused if its width does not match the column. Without that check a
mismatch surfaces as a database error on the first write — potentially after embedding hundreds of
chunks and paying for every one of them.
"""

from __future__ import annotations

import logging
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field

from app.application.ports.chunk_repository import ChunkRepository
from app.application.ports.embedding_provider import EmbeddingProvider

logger = logging.getLogger("knowledge.reembed")

# Opens a session, builds (repository, provider), runs one batch, commits. Supplied by the
# composition root so this service stays free of infrastructure.
BatchRunner = Callable[[int], Awaitable["BatchOutcome"]]


@dataclass
class BatchOutcome:
    """Result of processing one batch."""

    embedded: int = 0
    remaining: int = 0


@dataclass
class ReembedProgress:
    """Snapshot of a run, safe to serialize to the status endpoint."""

    state: str = "idle"  # idle | running | completed | failed
    total: int = 0
    embedded: int = 0
    remaining: int = 0
    error: str | None = None

    def as_dict(self) -> dict:
        return {
            "state": self.state,
            "total": self.total,
            "embedded": self.embedded,
            "remaining": self.remaining,
            "error": self.error,
        }


class DimensionMismatchError(RuntimeError):
    """The configured provider does not produce vectors of the column's width."""


@dataclass
class ReembedService:
    """Fills in missing chunk embeddings using the configured provider."""

    repository: ChunkRepository
    embedder: EmbeddingProvider
    expected_dim: int
    batch_size: int = 32
    progress: ReembedProgress = field(default_factory=ReembedProgress)

    async def verify_dimension(self) -> int:
        """Embed a probe string and confirm its width matches the column. Raises if not."""
        probe = await self.embedder.embed_query("dimension probe")
        if len(probe) != self.expected_dim:
            raise DimensionMismatchError(
                f"The configured embedder returns {len(probe)}-dimensional vectors but the "
                f"chunks.embedding column is {self.expected_dim}-wide. Align EMBEDDING_DIM with "
                f"the model and run the Alembic migration before reindexing."
            )
        return len(probe)

    async def run_batch(self) -> BatchOutcome:
        """Embed one batch of NULL-embedding chunks. Returns how many were written."""
        pending = await self.repository.fetch_missing_embeddings(self.batch_size)
        if not pending:
            return BatchOutcome(embedded=0, remaining=0)

        ids = [chunk_id for chunk_id, _ in pending]
        texts = [text for _, text in pending]
        vectors = await self.embedder.embed_documents(texts)
        if len(vectors) != len(ids):
            raise RuntimeError(
                f"Embedder returned {len(vectors)} vectors for {len(ids)} chunks."
            )

        written = await self.repository.set_embeddings(dict(zip(ids, vectors, strict=True)))
        remaining = await self.repository.count_missing_embeddings()
        return BatchOutcome(embedded=written, remaining=remaining)
