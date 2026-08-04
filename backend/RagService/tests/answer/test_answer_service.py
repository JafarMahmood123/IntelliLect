from __future__ import annotations

from uuid import uuid4

import pytest

from app.application.dtos.search_dtos import ChunkSearchResult
from app.application.services.answer_prompts import NO_CONTEXT_ANSWER

from tests.answer.fakes import build_answer_service
from tests.retrieval.fakes import make_result


async def test_happy_path_packs_context_and_maps_citations() -> None:
    results = [make_result(0.9, chunk_index=1, page=2), make_result(0.8, chunk_index=5, slide=4)]
    service, _repo, generator = build_answer_service(results)
    classroom_id = uuid4()

    response = await service.answer(classroom_id, "What is photosynthesis?")

    # The model was called once, and its answer is returned verbatim.
    assert generator.calls == 1
    assert response.answer == "Deterministic answer citing [1]."

    # The prompt contains the numbered, source-tagged context and the question.
    assert "[1] (page 2):" in generator.last_prompt
    assert "[2] (slide 4):" in generator.last_prompt
    assert "What is photosynthesis?" in generator.last_prompt
    # The system prompt is the grounded, refusal-safe one.
    assert "ONLY the provided context" in generator.last_system

    # Sources match the included chunks with the correct [n] mapping.
    assert [s.citation for s in response.sources] == [1, 2]
    first, second = response.sources
    assert first.chunk_id == results[0].chunk_id
    assert first.page == 2 and first.slide is None
    assert first.score == 0.9
    assert second.chunk_id == results[1].chunk_id
    assert second.slide == 4


async def test_no_results_short_circuits_without_calling_the_model() -> None:
    service, _repo, generator = build_answer_service([])

    response = await service.answer(uuid4(), "anything at all")

    assert response.answer == NO_CONTEXT_ANSWER
    assert response.sources == []
    assert generator.calls == 0  # the generator was never invoked


async def test_context_budget_drops_lowest_ranked_sources() -> None:
    text = "word " * 20  # ~100 chars -> ~29-token entries
    results = [
        ChunkSearchResult(
            chunk_id=uuid4(), document_id=uuid4(), text=text,
            score=0.9 - 0.05 * i, chunk_index=i, metadata={"page": i + 1},
        )
        for i in range(5)
    ]
    service, _repo, _gen = build_answer_service(results, context_max_tokens=70)

    response = await service.answer(uuid4(), "q")

    # Fewer sources than retrieved, kept best-first with contiguous [n] numbering.
    assert 0 < len(response.sources) < len(results)
    assert [s.citation for s in response.sources] == list(range(1, len(response.sources) + 1))
    assert [s.chunk_id for s in response.sources] == [
        r.chunk_id for r in results[: len(response.sources)]
    ]


async def test_empty_question_is_rejected() -> None:
    service, _repo, generator = build_answer_service([make_result(0.9)])

    with pytest.raises(ValueError, match="question must not be empty"):
        await service.answer(uuid4(), "   ")

    assert generator.calls == 0


async def test_classroom_id_is_always_passed_to_retrieval() -> None:
    service, repo, _gen = build_answer_service([make_result(0.9)])
    classroom_id = uuid4()

    await service.answer(classroom_id, "q")

    assert repo.searched_classroom_id == classroom_id


async def test_top_k_defaults_to_answer_top_k_and_is_overridable() -> None:
    service, repo, _gen = build_answer_service([make_result(0.9)], answer_top_k=6)

    await service.answer(uuid4(), "q")
    assert repo.searched_top_k == 6  # ANSWER_TOP_K default

    await service.answer(uuid4(), "q", top_k=3)
    assert repo.searched_top_k == 3
