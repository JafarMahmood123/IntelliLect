from __future__ import annotations

import asyncio
import math

from google import genai
from google.genai import types

from app.application.ports.embedding_provider import EmbeddingProvider
from app.infrastructure.config.settings import Settings

# Gemini task types (see google-genai EmbedContentConfig.task_type).
_TASK_DOCUMENT = "RETRIEVAL_DOCUMENT"
_TASK_QUERY = "RETRIEVAL_QUERY"


def _l2_normalize(vector: list[float]) -> list[float]:
    """Return the unit-length version of a vector.

    Required because a truncated output_dimensionality is not normalized by the
    API; cosine similarity assumes unit vectors.
    """
    norm = math.sqrt(sum(v * v for v in vector))
    if norm == 0.0:
        return vector
    return [v / norm for v in vector]


class GeminiEmbeddingProvider(EmbeddingProvider):
    """EmbeddingProvider implemented against the Gemini embeddings API.

    The google-genai SDK is synchronous, so each call is offloaded to a worker
    thread via asyncio.to_thread to honor the async port contract.
    """

    def __init__(self, settings: Settings, batch_size: int = 100) -> None:
        self._model = settings.embedding_model
        self._dim = settings.embedding_dim
        self._batch_size = batch_size
        self._client = genai.Client(api_key=settings.gemini_api_key)

    async def embed_documents(self, texts: list[str]) -> list[list[float]]:
        if not texts:
            return []
        results: list[list[float]] = []
        for start in range(0, len(texts), self._batch_size):
            batch = texts[start : start + self._batch_size]
            vectors = await asyncio.to_thread(self._embed_sync, batch, _TASK_DOCUMENT)
            results.extend(vectors)
        return results

    async def embed_query(self, text: str) -> list[float]:
        vectors = await asyncio.to_thread(self._embed_sync, [text], _TASK_QUERY)
        return vectors[0]

    def _embed_sync(self, contents: list[str], task_type: str) -> list[list[float]]:
        """Blocking SDK call; runs inside a thread."""
        response = self._client.models.embed_content(
            model=self._model,
            contents=contents,
            config=types.EmbedContentConfig(
                output_dimensionality=self._dim,
                task_type=task_type,
            ),
        )
        return [_l2_normalize(list(embedding.values)) for embedding in response.embeddings]
