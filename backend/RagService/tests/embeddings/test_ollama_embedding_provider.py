"""The Ollama embedding provider — the local alternative to Gemini.

Not what deploys today (`embedding_provider` defaults to "gemini"), but it is a supported choice
and the only one that works with no internet and no API key, which is what makes it the fallback
when the hosted key is rate-limited or blocked.

`tests/retrieval/test_embedding_dim_guard.py` already covers the width guard, which is the
defect that motivated it. This covers the rest: the asymmetry, the batching, and the error
messages — which exist to answer an operator's actual question, "is Ollama down, or is the model
just not pulled?", and are therefore worth asserting on rather than leaving to a raw transport
error.
"""

from __future__ import annotations

import math

import httpx
import pytest

from app.infrastructure.config.settings import Settings
from app.infrastructure.embeddings.ollama_embedding_provider import (
    OllamaEmbeddingError,
    OllamaEmbeddingProvider,
)
from tests.embeddings.fakes import FakeHttp, FakeResponse, Request

DIM = 4


def _settings(**overrides) -> Settings:
    base = dict(
        database_url="postgresql+asyncpg://u:p@localhost:5432/db",
        embedding_provider="ollama",
        ollama_base_url="http://ollama.example:11434",
        embedding_model="qwen3-embedding",
        embedding_dim=DIM,
    )
    base.update(overrides)
    return Settings(**base)


def _vectors(count: int, width: int = DIM) -> FakeResponse:
    """`count` distinct, unnormalised vectors — unnormalised so the L2 step is observable."""
    return FakeResponse(
        200,
        {
            "embeddings": [
                [float(i + 1)] + [0.0] * (width - 1) for i in range(count)
            ]
        },
    )


def _provider(monkeypatch, responder, *, batch_size: int = 64, **overrides):
    http = FakeHttp(responder=responder)
    monkeypatch.setattr(httpx, "AsyncClient", http.client_factory())
    return OllamaEmbeddingProvider(_settings(**overrides), batch_size=batch_size), http


def _inputs(request: Request) -> list[str]:
    return request.json["input"]


# --- what gets sent -----------------------------------------------------------------------


async def test_a_query_carries_the_retrieval_instruction_and_a_document_does_not(monkeypatch):
    """Asymmetric retrieval, faked with a prompt because qwen takes no task-type parameter.

    This is the mechanism the Gemini provider replaced with a real `taskType`. Here it is the
    only one there is, so dropping it — or applying it to documents as well, which cancels it
    out — costs retrieval quality with no other symptom.
    """
    provider, http = _provider(
        monkeypatch,
        lambda r: _vectors(len(_inputs(r))),
        retrieval_instruction="Instruct: find passages\nQuery: {query}",
    )

    await provider.embed_documents(["a passage from the lecture"])
    await provider.embed_query("what did the lecture say?")

    assert _inputs(http.requests[0]) == ["a passage from the lecture"]
    assert _inputs(http.requests[1]) == [
        "Instruct: find passages\nQuery: what did the lecture say?"
    ]


async def test_documents_are_sent_in_batches_of_the_configured_size(monkeypatch):
    # One request per batch, not per text: /api/embed takes a list, and a request per chunk
    # would turn a 300-chunk document into 300 round trips to a model that serialises anyway.
    provider, http = _provider(
        monkeypatch, lambda r: _vectors(len(_inputs(r))), batch_size=3
    )

    await provider.embed_documents([f"chunk {i}" for i in range(7)])

    assert [len(_inputs(r)) for r in http.requests] == [3, 3, 1]


async def test_batching_preserves_the_order_of_the_texts(monkeypatch):
    # The vectors are zipped positionally onto the chunk list, so a batch arriving out of
    # order pairs real vectors with the wrong chunks — a corpus that is wrong without being
    # detectably broken. The marker is a basis vector, which is already unit length and so
    # survives normalisation intact.
    texts = ["first", "second", "third", "fourth", "fifth"]
    position = {text: index for index, text in enumerate(texts)}

    def _marked(request: Request) -> FakeResponse:
        return FakeResponse(
            200,
            {
                "embeddings": [
                    [1.0 if i == position[t] else 0.0 for i in range(DIM + 1)]
                    for t in _inputs(request)
                ]
            },
        )

    provider, _ = _provider(monkeypatch, _marked, batch_size=2, embedding_dim=DIM + 1)

    vectors = await provider.embed_documents(texts)

    assert [vector.index(1.0) for vector in vectors] == list(range(len(texts)))


async def test_an_auth_token_is_sent_as_a_bearer_header_when_one_is_configured(monkeypatch):
    # Optional — a bare local Ollama has no auth — but a tunnelled or shared one does, and a
    # dropped header is a 401 that reads like a broken model.
    provider, http = _provider(
        monkeypatch,
        lambda r: _vectors(len(_inputs(r))),
        ollama_auth_token="tunnel-token",
    )

    await provider.embed_query("anything")

    assert http.client_kwargs[0]["headers"] == {"Authorization": "Bearer tunnel-token"}


async def test_no_authorization_header_is_invented_when_no_token_is_set(monkeypatch):
    provider, http = _provider(monkeypatch, lambda r: _vectors(len(_inputs(r))))

    await provider.embed_query("anything")

    assert http.client_kwargs[0]["headers"] == {}


async def test_an_empty_batch_makes_no_request(monkeypatch):
    provider, http = _provider(monkeypatch, lambda r: _vectors(len(_inputs(r))))

    assert await provider.embed_documents([]) == []
    assert http.requests == []


# --- what comes back ----------------------------------------------------------------------


async def test_vectors_are_normalised_because_ollama_does_not_normalise_them(monkeypatch):
    # pgvector's index is built with `vector_cosine_ops`, which assumes unit vectors.
    provider, _ = _provider(
        monkeypatch,
        lambda _r: FakeResponse(200, {"embeddings": [[3.0, 4.0]]}),
        embedding_dim=2,
    )

    [vector] = await provider.embed_documents(["anything"])

    assert math.isclose(math.sqrt(sum(v * v for v in vector)), 1.0, rel_tol=1e-9)
    assert vector == pytest.approx([0.6, 0.8])


async def test_a_zero_vector_does_not_divide_by_its_norm(monkeypatch):
    provider, _ = _provider(
        monkeypatch,
        lambda _r: FakeResponse(200, {"embeddings": [[0.0, 0.0]]}),
        embedding_dim=2,
    )

    assert await provider.embed_documents(["anything"]) == [[0.0, 0.0]]


# --- failure, and what the operator is told -----------------------------------------------


async def test_an_unpulled_model_says_which_model_to_pull(monkeypatch):
    """The single most common way this provider fails, and the one most easily misread.

    Ollama answers 404 for a model it does not have — which looks like a wrong URL rather than
    a missing `ollama pull`, and sends the operator to check networking that is fine.
    """
    provider, _ = _provider(monkeypatch, lambda _r: FakeResponse(404, text="not found"))

    with pytest.raises(OllamaEmbeddingError) as exc:
        await provider.embed_query("anything")

    assert "ollama pull qwen3-embedding" in str(exc.value)


async def test_an_unreachable_server_names_the_url_and_the_binding_it_needs(monkeypatch):
    # From inside a container, this is nearly always OLLAMA_HOST being left on loopback, so the
    # message says so instead of surfacing "connection refused".
    def _refuse(_request: Request):
        raise httpx.ConnectError("connection refused")

    provider, _ = _provider(monkeypatch, _refuse)

    with pytest.raises(OllamaEmbeddingError) as exc:
        await provider.embed_query("anything")

    message = str(exc.value)
    assert "ollama.example:11434" in message and "0.0.0.0" in message


async def test_any_other_http_failure_carries_the_status_and_the_body(monkeypatch):
    provider, _ = _provider(monkeypatch, lambda _r: FakeResponse(500, text="out of memory"))

    with pytest.raises(OllamaEmbeddingError) as exc:
        await provider.embed_documents(["anything"])

    assert "500" in str(exc.value) and "out of memory" in str(exc.value)


@pytest.mark.parametrize("payload", [{}, {"embeddings": []}, {"embeddings": None}])
async def test_a_reply_with_no_vectors_is_an_error_not_an_empty_list(monkeypatch, payload):
    """A generation model asked to embed answers 200 with no `embeddings` key.

    Returning [] would hand the caller fewer vectors than texts, and the misalignment would
    surface far from this call.
    """
    provider, _ = _provider(monkeypatch, lambda _r: FakeResponse(200, payload))

    with pytest.raises(OllamaEmbeddingError) as exc:
        await provider.embed_documents(["anything"])

    assert "embedding model" in str(exc.value)
