"""GeminiEmbeddingProvider contract, with the HTTP transport stubbed (no network, no key).

The behaviour worth pinning is the L2 normalization: Gemini truncates a Matryoshka embedding when
``outputDimensionality`` is set but does NOT re-normalize it (a real 768-dim reply measured norm
~0.59). Cosine drift assumes unit vectors, so skipping that step would silently shrink every
measured distance and stop DRIFT from ever firing.
"""

from __future__ import annotations

import math

import httpx
import pytest

from app.infrastructure.config.settings import Settings
from app.infrastructure.embeddings.gemini_embedding_provider import (
    GeminiEmbeddingError,
    GeminiEmbeddingProvider,
)

pytestmark = pytest.mark.asyncio


def _settings(**overrides) -> Settings:
    return Settings(
        gemini_api_key=overrides.get("gemini_api_key", "test-key"),
        gemini_embedding_model=overrides.get("gemini_embedding_model", "gemini-embedding-001"),
        gemini_embedding_dimensions=overrides.get("gemini_embedding_dimensions", 768),
        gemini_embedding_task_type=overrides.get(
            "gemini_embedding_task_type", "SEMANTIC_SIMILARITY"
        ),
        embedding_timeout_seconds=5.0,
    )


def _patch_transport(monkeypatch, handler) -> dict:
    """Route the provider's httpx client at ``handler``; return a dict capturing the request."""
    captured: dict = {}

    def _handler(request: httpx.Request) -> httpx.Response:
        captured["url"] = str(request.url)
        captured["headers"] = dict(request.headers)
        captured["json"] = __import__("json").loads(request.content.decode())
        return handler(request)

    real_client = httpx.AsyncClient

    def _factory(*args, **kwargs):
        kwargs["transport"] = httpx.MockTransport(_handler)
        return real_client(*args, **kwargs)

    monkeypatch.setattr(
        "app.infrastructure.embeddings.gemini_embedding_provider.httpx.AsyncClient", _factory
    )
    return captured


async def test_truncated_vector_is_l2_normalized(monkeypatch):
    # A deliberately un-normalized reply, as Gemini really returns for outputDimensionality<native.
    raw = [3.0, 4.0]  # norm 5.0
    captured = _patch_transport(
        monkeypatch, lambda _r: httpx.Response(200, json={"embedding": {"values": raw}})
    )

    vector = await GeminiEmbeddingProvider(_settings()).embed_query("photosynthesis")

    assert vector == pytest.approx([0.6, 0.8])
    assert math.sqrt(sum(v * v for v in vector)) == pytest.approx(1.0)
    # The request carries the drift-tuned knobs.
    assert captured["json"]["outputDimensionality"] == 768
    assert captured["json"]["taskType"] == "SEMANTIC_SIMILARITY"
    assert captured["json"]["content"]["parts"][0]["text"] == "photosynthesis"
    assert captured["headers"]["x-goog-api-key"] == "test-key"
    assert "gemini-embedding-001:embedContent" in captured["url"]


async def test_native_dimensionality_omits_the_field(monkeypatch):
    captured = _patch_transport(
        monkeypatch, lambda _r: httpx.Response(200, json={"embedding": {"values": [1.0, 0.0]}})
    )

    await GeminiEmbeddingProvider(_settings(gemini_embedding_dimensions=0)).embed_query("x")

    assert "outputDimensionality" not in captured["json"]


async def test_zero_vector_survives_normalization(monkeypatch):
    """A zero vector has no direction; normalizing must not divide by zero."""
    _patch_transport(
        monkeypatch, lambda _r: httpx.Response(200, json={"embedding": {"values": [0.0, 0.0]}})
    )

    assert await GeminiEmbeddingProvider(_settings()).embed_query("x") == [0.0, 0.0]


@pytest.mark.parametrize(
    "response",
    [
        httpx.Response(401, text="bad key"),
        httpx.Response(500, text="boom"),
        httpx.Response(200, json={}),  # no embedding at all
        httpx.Response(200, json={"embedding": {"values": []}}),  # empty values
    ],
)
async def test_bad_replies_raise(monkeypatch, response):
    _patch_transport(monkeypatch, lambda _r: response)

    with pytest.raises(GeminiEmbeddingError):
        await GeminiEmbeddingProvider(_settings()).embed_query("x")


async def test_transport_error_raises(monkeypatch):
    def _boom(_request):
        raise httpx.ConnectError("no route")

    _patch_transport(monkeypatch, _boom)

    with pytest.raises(GeminiEmbeddingError):
        await GeminiEmbeddingProvider(_settings()).embed_query("x")
