"""EmbeddingProvider backed by Google's Gemini embedding API.

Embeds transcript segments so the LA-3 boundary detector can measure semantic drift, without
putting a second model in local RAM the way ``OllamaEmbeddingProvider`` does. Same key and base
URL as ``GeminiBrainClient``, so enabling it costs no new credentials.

Two request fields matter a great deal here and are both configurable:

``taskType`` — ``SEMANTIC_SIMILARITY`` tunes the vector space for "are these two texts about the
same thing", which is exactly the drift question. Measured on real sentence pairs it separates
same-topic from new-topic roughly 1.9x, versus 1.5x untyped. It also COMPRESSES the absolute
distances, so it demands a much lower ``BOUNDARY_DRIFT_THRESHOLD`` (~0.23) than the untyped
default (~0.42). Changing task type without re-tuning that threshold silently breaks drift
detection in one direction or the other — see the table in settings.py.

``outputDimensionality`` — the model is Matryoshka-trained, so a truncated vector keeps its
meaning. 768 costs about half the latency of the full 3072 with no measured loss of separation.
IMPORTANT: Gemini does NOT re-normalize after truncating (a 768-dim reply comes back with norm
~0.59), so L2 normalization here is mandatory, not cosmetic — cosine drift assumes unit vectors.
"""

from __future__ import annotations

import logging
import math

import httpx

from app.application.ports.embedding_provider import EmbeddingProvider
from app.infrastructure.config.settings import Settings

logger = logging.getLogger("liveassistant.embeddings")


class GeminiEmbeddingError(RuntimeError):
    """Raised when the Gemini embedding API cannot produce a vector (transport/HTTP/auth)."""


def _l2_normalize(vector: list[float]) -> list[float]:
    """Return the unit-length version of a vector (cosine drift assumes unit norm)."""
    norm = math.sqrt(sum(v * v for v in vector))
    if norm == 0.0:
        return vector
    return [v / norm for v in vector]


class GeminiEmbeddingProvider(EmbeddingProvider):
    def __init__(self, settings: Settings) -> None:
        self._base_url = settings.gemini_base_url.rstrip("/")
        self._model = settings.gemini_embedding_model
        self._api_key = settings.gemini_api_key
        self._timeout = settings.embedding_timeout_seconds
        self._dimensions = settings.gemini_embedding_dimensions
        self._task_type = settings.gemini_embedding_task_type.strip()
        if not self._api_key:
            logger.warning(
                "GEMINI_API_KEY is empty; Gemini embedding calls will fail until it is set."
            )

    async def embed_query(self, text: str) -> list[float]:
        url = f"{self._base_url}/models/{self._model}:embedContent"
        payload: dict = {
            "model": f"models/{self._model}",
            "content": {"parts": [{"text": text}]},
        }
        # 0 => let the model use its native dimensionality (3072); slower, no measured benefit.
        if self._dimensions > 0:
            payload["outputDimensionality"] = self._dimensions
        if self._task_type:
            payload["taskType"] = self._task_type
        headers = {"x-goog-api-key": self._api_key, "Content-Type": "application/json"}

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
                f"HTTP {response.status_code}: {response.text}"
            )
        if response.status_code >= 400:
            raise GeminiEmbeddingError(
                f"Gemini embedContent failed with HTTP {response.status_code}: {response.text}"
            )

        values = (response.json().get("embedding") or {}).get("values")
        if not values:
            raise GeminiEmbeddingError(
                f"Gemini returned no embedding values for model '{self._model}'."
            )
        # Mandatory: a truncated (outputDimensionality) vector arrives un-normalized.
        return _l2_normalize([float(v) for v in values])
