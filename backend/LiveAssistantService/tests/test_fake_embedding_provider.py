"""FakeEmbeddingProvider (test support) must give deterministic, topic-keyed vectors
so LA-3 drift boundaries are predictable without a live embedder.
"""

from __future__ import annotations

from tests.support.fake_embedding_provider import FakeEmbeddingProvider


async def test_same_keyword_same_vector_different_keyword_differs():
    provider = FakeEmbeddingProvider(
        {"alpha": [1.0, 0.0], "beta": [0.0, 1.0]}, default=[0.0, 0.0]
    )

    a1 = await provider.embed_query("this is about ALPHA physics")
    a2 = await provider.embed_query("more alpha content here")
    b1 = await provider.embed_query("now beta topic")

    assert a1 == a2 == [1.0, 0.0]
    assert b1 == [0.0, 1.0]
    assert await provider.embed_query("no keyword here") == [0.0, 0.0]
    assert provider.calls  # inputs recorded


async def test_orthogonal_topics_are_unit_and_mutually_orthogonal():
    provider = FakeEmbeddingProvider.orthogonal_topics(["alpha", "beta"])

    alpha = await provider.embed_query("alpha")
    beta = await provider.embed_query("beta")
    other = await provider.embed_query("unmatched")

    assert len(alpha) == 3  # two topics + default axis
    assert sum(x * y for x, y in zip(alpha, beta)) == 0.0  # orthogonal
    assert sum(x * y for x, y in zip(alpha, other)) == 0.0
    assert alpha != beta != other
