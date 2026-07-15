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
from app.application.services.summary_prompts import INSUFFICIENT_CONTENT_MARKDOWN
from app.infrastructure.config.settings import Settings

from tests.summary.fakes import (
    DETERMINISTIC_MARKDOWN,
    FakeBrainClient,
    FakeTranscriptClient,
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
