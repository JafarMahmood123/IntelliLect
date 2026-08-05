"""In-memory doubles for the re-embed sweep.

The repository models the one thing the sweep's resumability rests on: `embedding IS NULL` means
"still to do". So the fake stores vectors in a dict and treats absence as NULL, exactly as the
column does, rather than tracking a separate "done" flag that could agree with the sweep while
disagreeing with the database.
"""

from __future__ import annotations

from uuid import UUID, uuid4


class InMemoryChunkRepository:
    """Only the reindex half of ChunkRepository — the sweep never touches the rest."""

    def __init__(self, texts: list[str] | None = None) -> None:
        self.texts: dict[UUID, str] = {uuid4(): text for text in (texts or [])}
        self.embeddings: dict[UUID, list[float]] = {}
        self.fetch_calls: list[int] = []
        self.set_calls: list[dict[UUID, list[float]]] = []

    # -- reindex surface -------------------------------------------------------------------

    async def count_all(self) -> int:
        return len(self.texts)

    async def count_missing_embeddings(self) -> int:
        return len(self.texts) - len(self.embeddings)

    async def fetch_missing_embeddings(self, limit: int) -> list[tuple[UUID, str]]:
        self.fetch_calls.append(limit)
        pending = [
            (chunk_id, text)
            for chunk_id, text in self.texts.items()
            if chunk_id not in self.embeddings
        ]
        return pending[:limit]

    async def set_embeddings(self, embeddings: dict[UUID, list[float]]) -> int:
        self.set_calls.append(dict(embeddings))
        self.embeddings.update(embeddings)
        return len(embeddings)


class FakeEmbedder:
    """Returns a vector that encodes the text it was given, so alignment is checkable.

    `width` is what the provider claims to produce; the dimension guard is the reason this is
    settable at all.
    """

    def __init__(self, width: int = 4, fail_with: Exception | None = None) -> None:
        self.width = width
        self.fail_with = fail_with
        self.document_batches: list[list[str]] = []
        self.queries: list[str] = []

    def vector_for(self, text: str) -> list[float]:
        # Distinct per text, and stable — the point is only that it is traceable back.
        return [float(len(text))] + [0.0] * (self.width - 1)

    async def embed_documents(self, texts: list[str]) -> list[list[float]]:
        if self.fail_with:
            raise self.fail_with
        self.document_batches.append(list(texts))
        return [self.vector_for(text) for text in texts]

    async def embed_query(self, text: str) -> list[float]:
        if self.fail_with:
            raise self.fail_with
        self.queries.append(text)
        return self.vector_for(text)
