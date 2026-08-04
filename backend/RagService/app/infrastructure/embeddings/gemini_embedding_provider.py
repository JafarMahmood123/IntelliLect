"""EmbeddingProvider backed by Google's Gemini embedding API.

Replaces the local Ollama embedder with a hosted one: no model weights, no local RAM, and no
dependence on a host Ollama being up. Same key as the rest of the platform's Gemini usage.

ASYMMETRIC BY TASK TYPE, not by prompt engineering. Retrieval works best when a passage and a
question are embedded differently — the Ollama provider approximated this by prepending a
qwen-specific ``"Instruct: ..."`` string to queries only. Gemini exposes it properly:

    embed_documents -> taskType=RETRIEVAL_DOCUMENT
    embed_query     -> taskType=RETRIEVAL_QUERY

So ``retrieval_instruction`` is NOT used here; carrying a qwen prompt convention over to a
different model would at best do nothing and at worst bias the vector.

DIMENSIONS ARE SCHEMA. ``embedding_dim`` sets the pgvector column width (see models.py and the
Alembic migration, both of which read it from Settings), so it cannot be changed on a populated
database without a migration AND a full re-embed — vectors from two different models are not
comparable, and mixing them returns confident nonsense rather than an error.

At 3072 (Gemini's native width) the column must be ``halfvec``: pgvector's HNSW index rejects more
than 2000 dimensions for ``vector``, but supports up to 4000 for ``halfvec``. The fp16 precision
loss is immaterial for cosine ranking and halves the storage.
"""

from __future__ import annotations

import asyncio
import logging
import math

import httpx

from app.application.ports.embedding_provider import EmbeddingProvider
from app.infrastructure.config.settings import Settings

logger = logging.getLogger("knowledge.embeddings")


class GeminiEmbeddingError(RuntimeError):
    """Raised when the Gemini embedding API cannot produce vectors (transport/HTTP/auth)."""


def _l2_normalize(vector: list[float]) -> list[float]:
    """Return the unit-length version of a vector.

    Cosine ranking assumes unit vectors. Gemini returns a normalized vector at native width but
    NOT after Matryoshka truncation (a 768-dim reply measures norm ~0.59), so this is mandatory
    whenever ``embedding_dim`` is below native.
    """
    norm = math.sqrt(sum(v * v for v in vector))
    if norm == 0.0:
        return vector
    return [v / norm for v in vector]


class GeminiEmbeddingProvider(EmbeddingProvider):
    def __init__(self, settings: Settings, concurrency: int = 4) -> None:
        self._base_url = settings.gemini_base_url.rstrip("/")
        self._model = settings.gemini_embedding_model
        self._api_key = settings.gemini_api_key
        self._timeout = settings.embedding_timeout_seconds
        self._dimensions = settings.embedding_dim
        # embedContent takes ONE input per call (no batch endpoint), so a document batch fans out
        # into concurrent requests. Bounded, because ingestion can hand over hundreds of chunks at
        # once and an unbounded fan-out would trip rate limits and open a socket per chunk.
        self._semaphore = asyncio.Semaphore(concurrency)
        if not self._api_key:
            logger.warning(
                "GEMINI_API_KEY is empty; Gemini embedding calls will fail until it is set."
            )

    async def embed_documents(self, texts: list[str]) -> list[list[float]]:
        if not texts:
            return []
        results = await asyncio.gather(
            *(self._embed_one(text, "RETRIEVAL_DOCUMENT") for text in texts)
        )
        return list(results)

    async def embed_query(self, text: str) -> list[float]:
        return await self._embed_one(text, "RETRIEVAL_QUERY")

    async def _embed_one(self, text: str, task_type: str) -> list[float]:
        url = f"{self._base_url}/models/{self._model}:embedContent"
        payload: dict = {
            "model": f"models/{self._model}",
            "content": {"parts": [{"text": text}]},
            "taskType": task_type,
            "outputDimensionality": self._dimensions,
        }
        headers = {"x-goog-api-key": self._api_key, "Content-Type": "application/json"}

        async with self._semaphore:
            try:
                async with httpx.AsyncClient(timeout=self._timeout) as client:
                    response = await client.post(url, json=payload, headers=headers)
            except httpx.RequestError as exc:
                raise GeminiEmbeddingError(
                    f"Could not reach the Gemini embedding API at {self._base_url}. "
                    f"Check network/GEMINI_BASE_URL. Original error: {exc}"
                ) from exc

        if response.status_code in (401, 403):
            raise GeminiEmbeddingError(
                "Gemini rejected the embedding request (check GEMINI_API_KEY). "
                f"HTTP {response.status_code}: {response.text[:200]}"
            )
        if response.status_code == 429:
            raise GeminiEmbeddingError(
                "Gemini embedding rate limit hit (429). Lower the ingestion concurrency or "
                f"retry later. Detail: {response.text[:200]}"
            )
        if response.status_code >= 400:
            raise GeminiEmbeddingError(
                f"Gemini embedContent failed with HTTP {response.status_code}: "
                f"{response.text[:300]}"
            )

        values = (response.json().get("embedding") or {}).get("values")
        if not values:
            raise GeminiEmbeddingError(
                f"Gemini returned no embedding values for model '{self._model}'."
            )
        if len(values) != self._dimensions:
            # Loud, because a width mismatch against the pgvector column fails at INSERT time with
            # a far less obvious error, potentially after a long ingestion run.
            raise GeminiEmbeddingError(
                f"Gemini returned {len(values)} dimensions but EMBEDDING_DIM is "
                f"{self._dimensions}; the pgvector column would reject this. Align "
                f"EMBEDDING_DIM with the model and re-run the migration."
            )
        return _l2_normalize([float(v) for v in values])
