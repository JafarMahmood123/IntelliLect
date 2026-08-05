"""The Gemini embedding provider — the embedder this platform actually deploys.

`embedding_provider` defaults to "gemini" and every compose file keeps it there, yet this file
was the least-covered module in the service. That matters more than the percentage does,
because almost nothing here fails loudly. A vector that is subtly wrong — unnormalised, built
from the wrong task type, or paired with the wrong chunk — produces no error anywhere. It
produces retrieval that quietly returns the wrong passage, and an assistant that confidently
answers from it.

So these tests are mostly about the request that gets *sent* and the arithmetic applied to the
reply, not about the happy path returning a list of the right length.
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
from tests.embeddings.fakes import FakeHttp, FakeResponse, Request

API_KEY = "test-gemini-key"


def _settings(**overrides) -> Settings:
    base = dict(
        database_url="postgresql+asyncpg://u:p@localhost:5432/db",
        embedding_provider="gemini",
        gemini_api_key=API_KEY,
        gemini_base_url="https://gemini.example/v1beta",
        gemini_embedding_model="gemini-embedding-001",
        embedding_dim=4,
    )
    base.update(overrides)
    return Settings(**base)


def _unit(index: int, dim: int = 4) -> list[float]:
    """A unit vector with its 1.0 at `index` — already normalised, so it survives unchanged."""
    return [1.0 if i == index else 0.0 for i in range(dim)]


def _ok(values: list[float]) -> FakeResponse:
    return FakeResponse(200, {"embedding": {"values": values}})


def _install(monkeypatch, http: FakeHttp) -> None:
    monkeypatch.setattr(httpx, "AsyncClient", http.client_factory())


def _provider(monkeypatch, responder, *, concurrency: int = 4, **overrides):
    http = FakeHttp(responder=responder)
    _install(monkeypatch, http)
    return GeminiEmbeddingProvider(_settings(**overrides), concurrency=concurrency), http


# --- what gets sent -----------------------------------------------------------------------


async def test_documents_and_queries_are_embedded_as_different_task_types(monkeypatch):
    """The whole reason this provider replaced the Ollama one.

    Asymmetric retrieval means a passage and a question are projected differently. Send both as
    RETRIEVAL_DOCUMENT and nothing breaks — no error, no warning, the vectors are the right
    width and the search still returns results. They are just worse results, for every query,
    forever. This is the failure the module docstring is about, and it is invisible in
    production.
    """
    provider, http = _provider(monkeypatch, lambda _r: _ok(_unit(0)))

    await provider.embed_documents(["a passage from the lecture"])
    await provider.embed_query("what did the lecture say?")

    assert [r.json["taskType"] for r in http.requests] == [
        "RETRIEVAL_DOCUMENT",
        "RETRIEVAL_QUERY",
    ]


async def test_the_ollama_retrieval_instruction_never_reaches_gemini(monkeypatch):
    """`retrieval_instruction` is a qwen prompt convention and is set for the whole service.

    Carrying it over would embed the instruction text along with the question — biasing the
    vector with a sentence the corpus never contains.
    """
    provider, http = _provider(
        monkeypatch,
        lambda _r: _ok(_unit(0)),
        retrieval_instruction="Instruct: qwen-only preamble\nQuery: {query}",
    )

    await provider.embed_query("photosynthesis")

    assert http.requests[0].text == "photosynthesis"
    assert "Instruct" not in http.requests[0].text


async def test_the_configured_width_is_requested_explicitly(monkeypatch):
    # Gemini returns its native 3072 unless asked otherwise, and the pgvector column is whatever
    # EMBEDDING_DIM says. Omitting this makes every insert fail on a truncated deployment.
    provider, http = _provider(monkeypatch, lambda _r: _ok(_unit(0, 4)), embedding_dim=4)

    await provider.embed_query("anything")

    assert http.requests[0].json["outputDimensionality"] == 4


async def test_the_api_key_travels_in_a_header_and_not_the_url(monkeypatch):
    # A key in the query string is copied into proxy logs, browser history and error reports by
    # everything it passes through; a header is not.
    provider, http = _provider(monkeypatch, lambda _r: _ok(_unit(0)))

    await provider.embed_query("anything")

    assert http.requests[0].headers["x-goog-api-key"] == API_KEY
    assert API_KEY not in http.requests[0].url


async def test_an_empty_batch_costs_nothing(monkeypatch):
    # Ingestion hands over whatever chunking produced, which can be nothing at all for an empty
    # or image-only file. A request per empty document is billed and rate-limited like any other.
    provider, http = _provider(monkeypatch, lambda _r: _ok(_unit(0)))

    assert await provider.embed_documents([]) == []
    assert http.requests == []


# --- what comes back ----------------------------------------------------------------------


async def test_vectors_come_back_in_the_order_of_the_texts_they_came_from(monkeypatch):
    """The alignment that everything downstream assumes and nothing downstream can check.

    `embed_documents` fans out into one concurrent request per text, and the caller zips the
    result straight onto the chunk list. If the order followed *completion* rather than input,
    chunk 3's text would be stored with chunk 7's vector — same count, same widths, so the
    `strict=True` zip in the repository passes and the database accepts it. The corpus is then
    silently wrong: search matches a passage and returns a different one's text.

    The first text is made the slowest so completion order is genuinely reversed here.
    """
    texts = ["first", "second", "third", "fourth"]
    position = {text: index for index, text in enumerate(texts)}
    http = FakeHttp(
        responder=lambda r: _ok(_unit(position[r.text])),
        # Earlier texts finish later.
        delay_for=lambda r: 0.02 * (len(texts) - position[r.text]),
    )
    _install(monkeypatch, http)
    provider = GeminiEmbeddingProvider(_settings(), concurrency=4)

    vectors = await provider.embed_documents(texts)

    # Completion order really was not input order, otherwise this proves nothing.
    assert [r.text for r in http.requests] == texts
    assert vectors == [_unit(0), _unit(1), _unit(2), _unit(3)]


async def test_a_truncated_vector_is_normalised_before_it_is_stored(monkeypatch):
    """Cosine ranking assumes unit vectors; Matryoshka truncation does not preserve the norm.

    pgvector's `<=>` divides by the norms, so an unnormalised vector still *ranks* — it simply
    ranks by a slightly different function than the one the index was built for. Wrong order,
    no error.
    """
    provider, _ = _provider(monkeypatch, lambda _r: _ok([3.0, 4.0]), embedding_dim=2)

    [vector] = await provider.embed_documents(["anything"])

    assert math.isclose(math.sqrt(sum(v * v for v in vector)), 1.0, rel_tol=1e-9)
    # Direction preserved, not just magnitude: 3/5, 4/5.
    assert vector == pytest.approx([0.6, 0.8])


async def test_a_zero_vector_is_returned_rather_than_dividing_by_its_norm(monkeypatch):
    # Degenerate, but a ZeroDivisionError here would abort a whole ingestion run over one chunk.
    provider, _ = _provider(monkeypatch, lambda _r: _ok([0.0, 0.0]), embedding_dim=2)

    assert await provider.embed_documents(["anything"]) == [[0.0, 0.0]]


async def test_integers_from_the_api_become_floats(monkeypatch):
    # JSON has one number type; a reply of exact zeros and ones decodes as int, and pgvector's
    # binding is typed.
    provider, _ = _provider(monkeypatch, lambda _r: _ok([1, 0]), embedding_dim=2)

    [vector] = await provider.embed_documents(["anything"])

    assert all(isinstance(v, float) for v in vector)


# --- the width guard ----------------------------------------------------------------------


async def test_a_reply_of_the_wrong_width_is_refused_by_name(monkeypatch):
    """The mismatch that used to reach the database.

    Uncaught, this surfaces at INSERT as a pgvector column-width error — after the extract, OCR,
    chunk and embed run has already been paid for, and naming the column rather than the setting.
    """
    provider, _ = _provider(
        monkeypatch, lambda _r: _ok([0.1] * 768), embedding_dim=3072
    )

    with pytest.raises(GeminiEmbeddingError) as exc:
        await provider.embed_documents(["anything"])

    message = str(exc.value)
    assert "768" in message and "3072" in message and "EMBEDDING_DIM" in message


@pytest.mark.parametrize(
    ("payload", "case"),
    [
        ({}, "no embedding object"),
        ({"embedding": {}}, "no values key"),
        ({"embedding": {"values": []}}, "an empty vector"),
    ],
)
async def test_a_reply_with_no_vector_is_an_error_not_an_empty_list(
    monkeypatch, payload, case
):
    # Returning [] here would hand the caller fewer vectors than texts, and the misalignment
    # would land on the chunk list rather than on this call.
    provider, _ = _provider(monkeypatch, lambda _r: FakeResponse(200, payload))

    with pytest.raises(GeminiEmbeddingError):
        await provider.embed_documents([f"triggering {case}"])


# --- failure, and what the operator is told -----------------------------------------------


@pytest.mark.parametrize("status", [401, 403])
async def test_a_rejected_key_says_so(monkeypatch, status):
    provider, _ = _provider(
        monkeypatch, lambda _r: FakeResponse(status, text="API key not valid")
    )

    with pytest.raises(GeminiEmbeddingError) as exc:
        await provider.embed_query("anything")

    assert "GEMINI_API_KEY" in str(exc.value)


async def test_a_rate_limit_is_named_as_one(monkeypatch):
    # Distinct from the generic 4xx path because the remedy is different: lower the concurrency
    # or wait, rather than go looking for a configuration mistake.
    provider, _ = _provider(monkeypatch, lambda _r: FakeResponse(429, text="quota"))

    with pytest.raises(GeminiEmbeddingError) as exc:
        await provider.embed_query("anything")

    assert "429" in str(exc.value)
    assert "concurrency" in str(exc.value) or "retry" in str(exc.value)


async def test_a_server_error_carries_the_status_and_the_body(monkeypatch):
    provider, _ = _provider(
        monkeypatch, lambda _r: FakeResponse(500, text="upstream exploded")
    )

    with pytest.raises(GeminiEmbeddingError) as exc:
        await provider.embed_query("anything")

    assert "500" in str(exc.value) and "upstream exploded" in str(exc.value)


async def test_an_unreachable_api_names_the_endpoint_it_could_not_reach(monkeypatch):
    def _refuse(_request: Request):
        raise httpx.ConnectError("connection refused")

    provider, _ = _provider(monkeypatch, _refuse)

    with pytest.raises(GeminiEmbeddingError) as exc:
        await provider.embed_query("anything")

    assert "gemini.example" in str(exc.value)


async def test_one_failed_text_fails_the_whole_batch(monkeypatch):
    """A partial batch is worse than a failed one.

    Swallowing the failure and returning the vectors that succeeded gives the caller fewer
    vectors than texts — and the caller pairs them positionally, so every chunk after the
    failure would be stored against the wrong vector.
    """
    def _one_bad(request: Request):
        if request.text == "second":
            return FakeResponse(500, text="nope")
        return _ok(_unit(0))

    provider, _ = _provider(monkeypatch, _one_bad)

    with pytest.raises(GeminiEmbeddingError):
        await provider.embed_documents(["first", "second", "third"])


# --- the fan-out --------------------------------------------------------------------------


async def test_the_fan_out_is_bounded_by_the_configured_concurrency(monkeypatch):
    """embedContent takes one text per call, so a 300-chunk document is 300 requests.

    Unbounded, that opens a socket per chunk and trips the rate limit the test above describes.
    The semaphore is the only thing stopping it, and a semaphore that is created but never
    awaited looks identical in the source.
    """
    texts = [f"chunk {i}" for i in range(12)]
    http = FakeHttp(responder=lambda _r: _ok(_unit(0)), delay_for=lambda _r: 0.01)
    _install(monkeypatch, http)

    await GeminiEmbeddingProvider(_settings(), concurrency=3).embed_documents(texts)

    assert http.peak_in_flight <= 3


async def test_the_bound_is_the_configured_one_and_not_an_accident_of_the_test(monkeypatch):
    # The companion to the test above: without this, a provider that ran everything strictly
    # one at a time — or a fake that never overlapped — would satisfy `peak <= 3` for the
    # wrong reason.
    texts = [f"chunk {i}" for i in range(12)]
    http = FakeHttp(responder=lambda _r: _ok(_unit(0)), delay_for=lambda _r: 0.01)
    _install(monkeypatch, http)

    await GeminiEmbeddingProvider(_settings(), concurrency=8).embed_documents(texts)

    assert http.peak_in_flight == 8


async def test_a_missing_api_key_is_warned_about_at_construction(monkeypatch, caplog):
    # It cannot be fatal — Settings has a default of "" and the service starts with Ollama
    # configured too — so a log line at construction is the only warning before the first
    # 401 arrives, potentially long after startup.
    _install(monkeypatch, FakeHttp(responder=lambda _r: _ok(_unit(0))))

    with caplog.at_level("WARNING", logger="knowledge.embeddings"):
        GeminiEmbeddingProvider(_settings(gemini_api_key=""))

    assert "GEMINI_API_KEY" in caplog.text
