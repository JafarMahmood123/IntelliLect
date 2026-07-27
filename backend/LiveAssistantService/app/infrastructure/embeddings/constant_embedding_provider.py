from __future__ import annotations

from app.application.ports.embedding_provider import EmbeddingProvider


class ConstantEmbeddingProvider(EmbeddingProvider):
    """A no-op ``EmbeddingProvider`` that returns a zero vector for every text.

    The boundary detector treats a zero-norm vector as "no drift" (cosine distance 0), so with
    this provider idea segmentation falls back to PAUSE + length caps only — semantic drift is
    effectively disabled. Its purpose is operational, not semantic: on a RAM-constrained host it
    keeps the embedding MODEL out of Ollama entirely, so the chat model can stay resident and
    replies are fast (no model-swap reload). No network, no model, no latency.

    Enable via ``BOUNDARY_USE_EMBEDDER=false``. Switch back to ``OllamaEmbeddingProvider`` (the
    default) for full drift-aware segmentation once the host has room for both models.
    """

    async def embed_query(self, text: str) -> list[float]:
        return [0.0]
