"""The embedding width must match EMBEDDING_DIM, and must fail LOUDLY when it does not.

A mismatch is not hypothetical: the defaults once said provider=ollama (qwen3-embedding, 1024
dims) while embedding_dim said 3072. Caught here, it is a clear message at the first embed call.
Uncaught, it surfaces as a pgvector column-width error at INSERT — after a full extract, OCR,
chunk and embed run, and naming the column rather than the configuration.
"""

from __future__ import annotations

import httpx
import pytest

from app.infrastructure.config.settings import Settings
from app.infrastructure.embeddings.ollama_embedding_provider import (
    OllamaEmbeddingError,
    OllamaEmbeddingProvider,
)


def _provider(monkeypatch, *, returns_dims: int, configured_dims: int):
    settings = Settings(
        database_url="postgresql+asyncpg://u:p@localhost:5432/db",
        embedding_provider="ollama",
        embedding_dim=configured_dims,
    )

    class _Response:
        status_code = 200

        @staticmethod
        def json():
            return {"embeddings": [[0.1] * returns_dims]}

    class _Client:
        async def __aenter__(self):
            return self

        async def __aexit__(self, *_):
            return False

        async def post(self, *_args, **_kwargs):
            return _Response()

    monkeypatch.setattr(httpx, "AsyncClient", lambda *a, **k: _Client())
    return OllamaEmbeddingProvider(settings)


async def test_mismatched_width_raises_at_embed_time(monkeypatch):
    provider = _provider(monkeypatch, returns_dims=1024, configured_dims=3072)

    with pytest.raises(OllamaEmbeddingError) as exc:
        await provider.embed_documents(["anything"])

    # The message must name both numbers and the setting to change.
    assert "1024" in str(exc.value)
    assert "3072" in str(exc.value)
    assert "EMBEDDING_DIM" in str(exc.value)


async def test_matching_width_is_accepted(monkeypatch):
    provider = _provider(monkeypatch, returns_dims=1024, configured_dims=1024)

    vectors = await provider.embed_documents(["anything"])

    assert len(vectors) == 1
    assert len(vectors[0]) == 1024
