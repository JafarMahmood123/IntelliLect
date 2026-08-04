from __future__ import annotations

from abc import ABC, abstractmethod


class EmbeddingProvider(ABC):
    """Port for turning text into embedding vectors.

    Used by the LA-3 boundary detector to embed transcript segments and measure
    semantic drift. Implemented by ``OllamaEmbeddingProvider`` (local Ollama over
    HTTP); tests use a deterministic ``FakeEmbeddingProvider`` instead, so no live
    model is ever required for the boundary logic. Kept symmetric with
    RagService's ``EmbeddingProvider``.
    """

    @abstractmethod
    async def embed_query(self, text: str) -> list[float]:
        """Embed a single text (a transcript segment, or a finished "idea")."""
        raise NotImplementedError
