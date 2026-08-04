from __future__ import annotations

from uuid import uuid4

import pytest

from app.application.dtos.search_dtos import SearchRequest
from app.application.services.retrieval_service import RetrievalService
from app.infrastructure.config.settings import Settings

from tests.retrieval.fakes import FakeChunkRepository, FakeEmbeddingProvider, make_result

DIM = Settings().embedding_dim


def _service(
    repo: FakeChunkRepository,
    *,
    default_top_k: int = 8,
    max_top_k: int = 50,
) -> tuple[RetrievalService, FakeEmbeddingProvider]:
    embedder = FakeEmbeddingProvider(DIM)
    return (
        RetrievalService(embedder, repo, default_top_k=default_top_k, max_top_k=max_top_k),
        embedder,
    )


async def test_results_are_returned_best_first() -> None:
    ordered = [make_result(0.95), make_result(0.80), make_result(0.55)]
    repo = FakeChunkRepository(ordered)
    service, _ = _service(repo)

    response = await service.search(
        SearchRequest(classroom_id=uuid4(), query="what is photosynthesis?")
    )

    assert [item.score for item in response.results] == [0.95, 0.80, 0.55]
    assert [item.chunk_id for item in response.results] == [r.chunk_id for r in ordered]


async def test_default_top_k_applied_when_none() -> None:
    repo = FakeChunkRepository([make_result(0.9)])
    service, _ = _service(repo, default_top_k=8)

    await service.search(SearchRequest(classroom_id=uuid4(), query="q", top_k=None))

    assert repo.searched_top_k == 8


async def test_top_k_clamped_to_max() -> None:
    repo = FakeChunkRepository([make_result(0.9)])
    service, _ = _service(repo, max_top_k=5)

    await service.search(SearchRequest(classroom_id=uuid4(), query="q", top_k=1000))

    assert repo.searched_top_k == 5


async def test_top_k_clamped_to_at_least_one() -> None:
    repo = FakeChunkRepository([make_result(0.9)])
    service, _ = _service(repo)

    await service.search(SearchRequest(classroom_id=uuid4(), query="q", top_k=0))
    assert repo.searched_top_k == 1

    await service.search(SearchRequest(classroom_id=uuid4(), query="q", top_k=-4))
    assert repo.searched_top_k == 1


async def test_classroom_id_is_passed_to_repository() -> None:
    repo = FakeChunkRepository([make_result(0.9)])
    service, _ = _service(repo)
    classroom_id = uuid4()

    await service.search(SearchRequest(classroom_id=classroom_id, query="q"))

    assert repo.searched_classroom_id == classroom_id


async def test_query_is_embedded_as_a_query() -> None:
    repo = FakeChunkRepository([make_result(0.9)])
    service, embedder = _service(repo)

    await service.search(SearchRequest(classroom_id=uuid4(), query="  spaced query  "))

    assert embedder.embed_query_calls == 1
    assert embedder.last_query == "spaced query"  # trimmed
    assert repo.searched_embedding is not None
    assert len(repo.searched_embedding) == DIM


@pytest.mark.parametrize("bad_query", ["", "   ", "\n\t"])
async def test_empty_query_is_rejected(bad_query: str) -> None:
    repo = FakeChunkRepository([make_result(0.9)])
    service, _ = _service(repo)

    with pytest.raises(ValueError, match="query must not be empty"):
        await service.search(SearchRequest(classroom_id=uuid4(), query=bad_query))

    assert repo.searched_top_k is None  # never reached the repository
