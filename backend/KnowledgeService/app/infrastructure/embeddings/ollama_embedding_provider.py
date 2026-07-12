from __future__ import annotations

import math

import httpx

from app.application.ports.embedding_provider import EmbeddingProvider
from app.infrastructure.config.settings import Settings


class OllamaEmbeddingError(RuntimeError):
    """Raised when the local Ollama server cannot produce embeddings.

    The message is written to be actionable for an operator (is Ollama running?
    is the model pulled?) rather than surfacing a raw transport error.
    """


def _l2_normalize(vector: list[float]) -> list[float]:
    """Return the unit-length version of a vector.

    Cosine similarity (the pgvector index uses `vector_cosine_ops`) assumes unit
    vectors; Ollama does not normalize its output, so we do it here.
    """
    norm = math.sqrt(sum(v * v for v in vector))
    if norm == 0.0:
        return vector
    return [v / norm for v in vector]


class OllamaEmbeddingProvider(EmbeddingProvider):
    """EmbeddingProvider backed by a LOCAL Ollama server over HTTP.

    No model weights live in this container — every call is an HTTP request to
    the host's Ollama (`POST /api/embed`). Documents are embedded raw; queries
    get a configurable retrieval instruction prepended (asymmetric embedding).
    """

    def __init__(self, settings: Settings, batch_size: int = 64) -> None:
        self._base_url = settings.ollama_base_url.rstrip("/")
        self._model = settings.embedding_model
        self._timeout = settings.embedding_timeout_seconds
        self._instruction = settings.retrieval_instruction
        self._batch_size = batch_size
        self._headers: dict[str, str] = {}
        if settings.ollama_auth_token:
            self._headers["Authorization"] = f"Bearer {settings.ollama_auth_token}"

    async def embed_documents(self, texts: list[str]) -> list[list[float]]:
        if not texts:
            return []
        results: list[list[float]] = []
        for start in range(0, len(texts), self._batch_size):
            batch = texts[start : start + self._batch_size]
            results.extend(await self._embed(batch))
        return results

    async def embed_query(self, text: str) -> list[float]:
        prompt = self._instruction.format(query=text)
        vectors = await self._embed([prompt])
        return vectors[0]

    async def _embed(self, inputs: list[str]) -> list[list[float]]:
        url = f"{self._base_url}/api/embed"
        payload = {"model": self._model, "input": inputs}
        try:
            async with httpx.AsyncClient(
                timeout=self._timeout, headers=self._headers
            ) as client:
                response = await client.post(url, json=payload)
        except httpx.RequestError as exc:
            raise OllamaEmbeddingError(
                f"Could not reach Ollama at {self._base_url}. Ensure it is running "
                f"and bound to 0.0.0.0:11434 (OLLAMA_HOST=0.0.0.0), and that "
                f"OLLAMA_BASE_URL is correct. Original error: {exc}"
            ) from exc

        if response.status_code == 404:
            raise OllamaEmbeddingError(
                f"Ollama does not have model '{self._model}'. Pull it first with "
                f"`ollama pull {self._model}`."
            )
        if response.status_code >= 400:
            raise OllamaEmbeddingError(
                f"Ollama /api/embed failed with HTTP {response.status_code}: "
                f"{response.text}"
            )

        data = response.json()
        embeddings = data.get("embeddings")
        if not embeddings:
            raise OllamaEmbeddingError(
                f"Ollama returned no embeddings. Confirm that '{self._model}' is an "
                f"embedding model and has been pulled (`ollama pull {self._model}`). "
                f"Response: {data}"
            )
        return [_l2_normalize(list(vector)) for vector in embeddings]
