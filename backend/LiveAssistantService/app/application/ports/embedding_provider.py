from __future__ import annotations

from abc import ABC, abstractmethod


class EmbeddingProvider(ABC):
    """Port for turning text into embedding vectors.

    STUB — NOT IMPLEMENTED THIS PHASE (later phase: retrieval). Kept symmetric with
    KnowledgeService's ``EmbeddingProvider`` so a "finished idea" can be embedded and
    matched against the classroom's indexed material. Every method raises
    ``NotImplementedError``.
    """

    @abstractmethod
    async def embed_query(self, text: str) -> list[float]:
        """Embed a single query text (a finished teacher "idea") for retrieval."""
        raise NotImplementedError
