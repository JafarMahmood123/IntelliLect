"""The re-embed service — the only recovery path from an embedder change.

Changing the model, provider or dimension means an Alembic migration that DROPS every stored
vector, because vectors from two models are not comparable. Without this job the corpus is
rebuilt by re-uploading every document by hand. It had no tests at all.

Two properties carry the weight here: the dimension guard runs BEFORE any work (a mismatch found
at write time has already been paid for in embedding calls), and each chunk keeps its own vector
(a shuffle here corrupts the corpus with no error anywhere).
"""

from __future__ import annotations

import pytest

from app.application.services.reembed_service import (
    DimensionMismatchError,
    ReembedService,
)
from tests.reembed.fakes import FakeEmbedder, InMemoryChunkRepository


def _service(texts: list[str], *, width: int = 4, expected: int = 4, batch_size: int = 32):
    repository = InMemoryChunkRepository(texts)
    embedder = FakeEmbedder(width=width)
    service = ReembedService(
        repository=repository,
        embedder=embedder,
        expected_dim=expected,
        batch_size=batch_size,
    )
    return service, repository, embedder


# --- the guard ----------------------------------------------------------------------------


async def test_a_mismatched_embedder_is_refused_before_any_chunk_is_embedded():
    """The probe is the whole point of running it first.

    Without it, a wrong EMBEDDING_DIM surfaces as a pgvector column-width error on the first
    write — after a batch of embeddings has been requested and billed, and reported as a
    database error rather than a configuration one.
    """
    service, _, embedder = _service(["a chunk"], width=1024, expected=3072)

    with pytest.raises(DimensionMismatchError) as exc:
        await service.verify_dimension()

    message = str(exc.value)
    assert "1024" in message and "3072" in message
    # Names the setting and the migration, because neither alone fixes it.
    assert "EMBEDDING_DIM" in message and "migration" in message.lower()
    # And nothing was embedded for real.
    assert embedder.document_batches == []


async def test_a_matching_embedder_reports_the_width_it_verified():
    service, _, _ = _service(["a chunk"], width=768, expected=768)

    assert await service.verify_dimension() == 768


# --- one batch ----------------------------------------------------------------------------


async def test_each_chunk_keeps_its_own_vector():
    """Alignment, again — and here it is by id rather than by position.

    `set_embeddings` takes a dict keyed on chunk id, so a mis-zip does not shorten anything or
    raise: it writes real vectors against the wrong rows. Search then matches one passage and
    returns another's text, with nothing in any log to say so.
    """
    service, repository, embedder = _service(["short", "a much longer chunk of text"])

    await service.run_batch()

    for chunk_id, text in repository.texts.items():
        assert repository.embeddings[chunk_id] == embedder.vector_for(text)


async def test_a_batch_reports_what_it_wrote_and_what_is_left():
    service, _, _ = _service([f"chunk {i}" for i in range(5)], batch_size=2)

    outcome = await service.run_batch()

    assert outcome.embedded == 2
    assert outcome.remaining == 3


async def test_the_batch_size_bounds_the_embedder_call():
    # Each batch is one transaction and one fan-out of embedding calls; an unbounded fetch would
    # hold a transaction open across the whole corpus and lose everything on a crash.
    service, repository, embedder = _service(
        [f"chunk {i}" for i in range(10)], batch_size=3
    )

    await service.run_batch()

    assert repository.fetch_calls == [3]
    assert len(embedder.document_batches[0]) == 3


async def test_an_empty_batch_is_the_finish_line_not_an_error():
    # The sweep's termination condition. `embedded == 0` is how it learns there is nothing left.
    service, _, embedder = _service([])

    outcome = await service.run_batch()

    assert outcome.embedded == 0
    assert outcome.remaining == 0
    assert embedder.document_batches == []


async def test_a_short_reply_from_the_embedder_stops_the_batch():
    """A provider that returns fewer vectors than texts must not be silently zipped.

    Python's plain `zip` would truncate and write the surplus chunks nothing at all — leaving
    them NULL, which the resumable design reads as "not done yet", so the sweep would keep
    re-serving and re-paying for them without ever finishing.
    """
    service, repository, embedder = _service(["one", "two", "three"])

    async def _drops_one(texts):
        return [embedder.vector_for(text) for text in texts[:-1]]

    embedder.embed_documents = _drops_one

    with pytest.raises(RuntimeError) as exc:
        await service.run_batch()

    assert "2" in str(exc.value) and "3" in str(exc.value)
    assert repository.embeddings == {}


# --- resumability -------------------------------------------------------------------------


async def test_a_second_run_picks_up_only_what_is_still_missing():
    """Resumability comes from the schema, not from bookkeeping.

    `embedding IS NULL` is the pending marker — exactly the state the migration leaves behind —
    so a crashed or cancelled sweep is recovered by running it again. If a batch re-served rows
    it had already written, a sweep would never terminate.
    """
    service, repository, embedder = _service([f"chunk {i}" for i in range(4)], batch_size=2)

    first = await service.run_batch()
    second = await service.run_batch()
    third = await service.run_batch()

    assert (first.embedded, second.embedded, third.embedded) == (2, 2, 0)
    assert await repository.count_missing_embeddings() == 0
    # No chunk was embedded twice: four texts across the two batches, each exactly once.
    embedded_texts = [text for batch in embedder.document_batches for text in batch]
    assert sorted(embedded_texts) == sorted(repository.texts.values())


async def test_progress_serialises_to_the_shape_the_status_endpoint_returns():
    from app.application.services.reembed_service import ReembedProgress

    snapshot = ReembedProgress(state="running", total=10, embedded=4, remaining=6).as_dict()

    assert snapshot == {
        "state": "running",
        "total": 10,
        "embedded": 4,
        "remaining": 6,
        "error": None,
    }
