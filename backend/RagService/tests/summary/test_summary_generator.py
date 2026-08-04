"""SummaryGenerator (S-1) — structure, grounding, short-circuit, map-reduce, degradation.

All offline: FakeTranscriptClient + a real RetrievalService over fakes + FakeBrainClient
(deterministic Markdown that records every prompt). No Ollama, no LiveAssistant, no DB.
"""

from __future__ import annotations

import logging
from datetime import datetime
from uuid import uuid4

from app.application.services.summary_generator import (
    SummaryGenerationError,
    SummaryGenerator,
)
from app.application.services.summary_prompts import (
    INSUFFICIENT_CONTENT_MARKDOWN,
    SYSTEM_PROMPT,
)
from app.infrastructure.config.settings import Settings

from tests.summary.fakes import (
    DETERMINISTIC_MARKDOWN,
    FakeBrainClient,
    FakeTranscriptClient,
    build_grounding_generator,
    build_summary_generator,
    make_chunk,
)

_LECTURE = (
    "Today we covered photosynthesis, the process by which plants turn light energy "
    "into glucose inside chloroplasts using chlorophyll, across the light-dependent "
    "reactions and the Calvin cycle."
)


# --- structure ----------------------------------------------------------------
async def test_normal_transcript_returns_structured_markdown_intact():
    generator, transcript_client, _repo, brain = build_summary_generator(
        transcript_text=_LECTURE
    )

    session_id = uuid4()
    result = await generator.generate(session_id)

    # The generator returns the model's Markdown verbatim, with the stable sections.
    assert result.markdown == DETERMINISTIC_MARKDOWN
    for section in ("# Session Summary", "## Overview", "## Key Points", "## Key Terms"):
        assert section in result.markdown
    assert brain.call_count == 1

    # SummaryResult metadata.
    assert result.session_id == session_id
    assert transcript_client.fetched_session_id == session_id
    assert result.classroom_id == transcript_client.classroom_id
    assert result.model == Settings().summary_model
    assert isinstance(result.generated_at, datetime)


async def test_generate_from_text_has_no_session_id():
    generator, _tc, _repo, _brain = build_summary_generator(transcript_text=_LECTURE)
    classroom_id = uuid4()

    result = await generator.generate_from_text(_LECTURE, classroom_id)

    assert result.session_id is None
    assert result.classroom_id == classroom_id
    assert result.markdown == DETERMINISTIC_MARKDOWN


# --- grounding on / off -------------------------------------------------------
async def test_grounding_on_calls_retrieval_and_includes_chunks_in_prompt():
    chunk_text = "Chlorophyll reflects green light and absorbs red and blue."
    generator, transcript_client, repo, brain = build_summary_generator(
        transcript_text=_LECTURE,
        chunks=[make_chunk(chunk_text, page=3)],
        settings=Settings(summary_grounding_enabled=True, summary_grounding_top_k=4),
    )

    await generator.generate(uuid4())

    # Retrieval was invoked, classroom-scoped, with the configured top_k.
    assert repo.searched_classroom_id == transcript_client.classroom_id
    assert repo.searched_top_k == 4
    # The supporting material is present in the (single-pass) prompt.
    assert "Supporting classroom material" in brain.last_prompt
    assert chunk_text in brain.last_prompt


async def test_grounding_off_does_not_call_retrieval():
    generator, _tc, repo, brain = build_summary_generator(
        transcript_text=_LECTURE,
        chunks=[make_chunk("unused material")],
        settings=Settings(summary_grounding_enabled=False),
    )

    result = await generator.generate(uuid4())

    assert repo.searched_classroom_id is None  # retrieval never called
    assert "Supporting classroom material" not in brain.last_prompt
    assert result.markdown == DETERMINISTIC_MARKDOWN


# --- grounding coverage across the whole transcript ---------------------------
# Long enough to be sampled into several windows, with distinctive markers at each end.
_LONG_LECTURE = (
    "We open the session with OPENINGTOPIC. "
    + "The lecture continues with ordinary filler content. " * 130
    + "Finally we close with CLOSINGTOPIC."
)


async def test_grounding_queries_span_the_whole_transcript():
    generator, embedder, repo, _brain = build_grounding_generator(
        transcript_text=_LONG_LECTURE,
        per_window_chunks=[],
        settings=Settings(summary_grounding_query_windows=4),
    )

    await generator.generate(uuid4())

    assert len(embedder.queries) == 4
    assert repo.search_count == 4
    assert "OPENINGTOPIC" in embedder.queries[0]
    # The regression this guards: querying only the transcript's opening retrieves
    # material about whatever the lecture started with, so anything taught later is
    # "grounded" against chunks that never mention it.
    assert "CLOSINGTOPIC" in embedder.queries[-1]


async def test_short_transcript_uses_a_single_grounding_query():
    generator, embedder, repo, _brain = build_grounding_generator(
        transcript_text=_LECTURE,
        per_window_chunks=[[make_chunk("material")]],
    )

    await generator.generate(uuid4())

    # A short lecture fits one window: no extra embed + search cost for the common case.
    assert len(embedder.queries) == 1
    assert repo.search_count == 1


async def test_grounding_deduplicates_chunks_retrieved_by_several_windows():
    shared = make_chunk("Shared material about eviction.", score=0.8)
    late_only = make_chunk("Late material about least recently used.", score=0.7)
    generator, _embedder, _repo, brain = build_grounding_generator(
        transcript_text=_LONG_LECTURE,
        per_window_chunks=[[shared], [shared], [shared, late_only], [late_only]],
        settings=Settings(summary_grounding_query_windows=4),
    )

    await generator.generate(uuid4())

    # Overlapping windows must not repeat a chunk three times in the supporting block.
    assert brain.last_prompt.count("Shared material about eviction.") == 1
    assert "Late material about least recently used." in brain.last_prompt


async def test_grounding_keeps_only_the_highest_scoring_chunks():
    windows = [[make_chunk(f"material {i}", score=0.5 + i / 10)] for i in range(4)]
    generator, _embedder, _repo, brain = build_grounding_generator(
        transcript_text=_LONG_LECTURE,
        per_window_chunks=windows,
        settings=Settings(
            summary_grounding_query_windows=4, summary_grounding_max_chunks=2
        ),
    )

    await generator.generate(uuid4())

    # windows * top_k must not grow the prompt without bound on a long lecture.
    assert "material 3" in brain.last_prompt
    assert "material 2" in brain.last_prompt
    assert "material 1" not in brain.last_prompt
    assert "material 0" not in brain.last_prompt


async def test_partial_grounding_failure_still_uses_the_surviving_windows(caplog):
    survivor = make_chunk("Material retrieved despite the failure.")
    generator, _embedder, _repo, brain = build_grounding_generator(
        transcript_text=_LONG_LECTURE,
        per_window_chunks=[RuntimeError("vector search is down"), [survivor], [], []],
        settings=Settings(summary_grounding_query_windows=4),
    )

    with caplog.at_level(logging.WARNING, logger="knowledge.summary"):
        result = await generator.generate(uuid4())

    # One dead window degrades grounding, it does not discard it.
    assert "Material retrieved despite the failure." in brain.last_prompt
    assert any("Grounding retrieval failed" in r.message for r in caplog.records)
    assert result.markdown == DETERMINISTIC_MARKDOWN


# --- prompt contract ----------------------------------------------------------
def test_prompt_lets_course_material_correct_the_transcript():
    # The bug this guards: supporting material was restricted to "terminology only", so a
    # misspoken figure went into the students' PDF as fact — and got promoted to Notable
    # Moments as the lecture's key takeaway.
    assert "## Corrections" in SYSTEM_PROMPT
    assert "AUTHORITATIVE" in SYSTEM_PROMPT
    # It must stay optional, or the model manufactures a conflict to fill the section.
    assert "OMIT this whole section if there are no contradictions" in SYSTEM_PROMPT


# --- recorded model -----------------------------------------------------------
async def test_gemini_provider_records_the_gemini_model_name():
    # summary_model names the Ollama model; on Gemini it would otherwise credit every
    # hosted summary to qwen2.5:7b-instruct.
    settings = Settings(generation_provider="gemini", summary_grounding_enabled=False)
    generator, _tc, _repo, _brain = build_summary_generator(
        transcript_text=_LECTURE, settings=settings
    )

    result = await generator.generate(uuid4())

    assert result.model == settings.gemini_summary_model
    assert result.model != settings.summary_model


# --- empty / short short-circuit ----------------------------------------------
async def test_empty_transcript_short_circuits_without_calling_the_brain():
    generator, _tc, repo, brain = build_summary_generator(transcript_text="   ")

    result = await generator.generate(uuid4())

    assert result.markdown == INSUFFICIENT_CONTENT_MARKDOWN
    assert brain.call_count == 0            # model never called
    assert repo.searched_classroom_id is None  # retrieval never called


async def test_trivially_short_transcript_short_circuits():
    generator, _tc, _repo, brain = build_summary_generator(transcript_text="Hi class.")

    result = await generator.generate(uuid4())

    assert result.markdown == INSUFFICIENT_CONTENT_MARKDOWN
    assert brain.call_count == 0


# --- long transcript -> map-reduce --------------------------------------------
async def test_long_transcript_triggers_map_reduce():
    # A tiny per-pass cap forces several chunk summaries + one synthesis.
    long_text = " ".join(f"word{i}" for i in range(200))
    generator, _tc, _repo, brain = build_summary_generator(
        transcript_text=long_text,
        settings=Settings(summary_transcript_max_tokens=20, summary_grounding_enabled=False),
    )

    result = await generator.generate(uuid4())

    # Multiple chunk (map) calls then exactly one synthesis (reduce) call.
    chunk_calls = [p for p in brain.prompts if "of a longer lecture transcript" in p]
    synthesis_calls = [p for p in brain.prompts if p.startswith("Below are ordered notes")]
    assert len(chunk_calls) >= 2
    assert len(synthesis_calls) == 1
    assert brain.call_count == len(chunk_calls) + 1  # maps + one reduce
    assert result.markdown == DETERMINISTIC_MARKDOWN  # final = synthesis output


# --- retrieval-failure degradation --------------------------------------------
async def test_retrieval_failure_degrades_to_ungrounded_summary(caplog):
    generator, _tc, _repo, brain = build_summary_generator(
        transcript_text=_LECTURE,
        chunks=[make_chunk("material")],
        raising_retrieval=True,  # RetrievalService.search will raise
        settings=Settings(summary_grounding_enabled=True),
    )

    with caplog.at_level(logging.WARNING, logger="knowledge.summary"):
        result = await generator.generate(uuid4())

    # Summary still produced (single pass), just ungrounded; failure logged, not raised.
    assert result.markdown == DETERMINISTIC_MARKDOWN
    assert brain.call_count == 1
    assert "Supporting classroom material" not in brain.last_prompt
    assert any("Grounding retrieval failed" in r.message for r in caplog.records)


# --- generation failure -> catchable error ------------------------------------
class _RaisingBrain(FakeBrainClient):
    async def generate(self, system: str, prompt: str) -> str:
        raise RuntimeError("ollama exploded")


async def test_generation_failure_raises_summary_generation_error():
    from tests.retrieval.fakes import FakeChunkRepository, FakeEmbeddingProvider
    from app.application.services.retrieval_service import RetrievalService

    settings = Settings(summary_grounding_enabled=False)
    generator = SummaryGenerator(
        FakeTranscriptClient(_LECTURE),
        RetrievalService(FakeEmbeddingProvider(settings.embedding_dim), FakeChunkRepository([])),
        _RaisingBrain(),
        settings,
    )

    try:
        await generator.generate(uuid4())
        raise AssertionError("expected SummaryGenerationError")
    except SummaryGenerationError as exc:
        assert "ollama exploded" in str(exc)
