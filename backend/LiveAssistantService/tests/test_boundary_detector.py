"""Deterministic, OFFLINE tests for the LA-3 BoundaryDetector.

Driven by FakeSpeechToText (scripted TranscriptSegments) + FakeEmbeddingProvider
(one-hot topic vectors so drift is exactly 0 within a topic and 1.0 across topics).
No STT model, no Ollama.
"""

from __future__ import annotations

from collections.abc import AsyncIterator

from app.api.dependencies import build_boundary_detector
from app.application.services.token_estimate import estimate_tokens
from app.domain.idea.boundary_trigger import BoundaryTrigger
from app.domain.idea.completed_idea import CompletedIdea
from app.domain.transcript.transcript_segment import TranscriptSegment
from app.infrastructure.config.settings import Settings
from tests.support.fake_embedding_provider import FakeEmbeddingProvider
from tests.support.fake_speech_to_text import FakeSpeechToText


def _seg(text, start_ms, end_ms, *, final=True, pause=False) -> TranscriptSegment:
    return TranscriptSegment(text, start_ms, end_ms, is_final=final, followed_by_pause=pause)


async def _empty_frames() -> AsyncIterator:
    return
    yield  # pragma: no cover — make this an async generator


async def _run(segments, embedder, **overrides) -> list[CompletedIdea]:
    # Sensible permissive defaults; each test tightens the knob it exercises.
    settings = Settings(
        boundary_drift_threshold=overrides.get("boundary_drift_threshold", 0.35),
        boundary_pause_seconds=overrides.get("boundary_pause_seconds", 0.8),
        boundary_max_seconds=overrides.get("boundary_max_seconds", 10_000.0),
        boundary_max_tokens=overrides.get("boundary_max_tokens", 10_000),
        boundary_min_tokens=overrides.get("boundary_min_tokens", 3),
    )
    detector = build_boundary_detector(settings, embedder)
    stt = FakeSpeechToText(segments)
    return [idea async for idea in detector.process(stt.transcribe(_empty_frames()))]


def _topics(*keywords) -> FakeEmbeddingProvider:
    return FakeEmbeddingProvider.orthogonal_topics(list(keywords))


# --- DRIFT -------------------------------------------------------------------
async def test_topic_shift_fires_drift_boundary():
    segments = [
        _seg("alpha one two three four", 0, 2000),
        _seg("alpha five six seven eight", 2000, 4000),
        _seg("beta a completely different subject now", 4000, 6000),  # topic shift
    ]
    ideas = await _run(segments, _topics("alpha", "beta"))

    # Idea A (alpha) closes on DRIFT; idea B (beta) flushes at stream end.
    assert len(ideas) == 2
    assert ideas[0].trigger is BoundaryTrigger.DRIFT
    assert "alpha" in ideas[0].text and "beta" not in ideas[0].text
    assert ideas[0].segment_count == 2
    assert "beta" in ideas[1].text


# --- CAPS --------------------------------------------------------------------
async def test_token_cap_forces_boundary_without_drift_or_pause():
    segments = [
        _seg("alpha a b c d e", 0, 1000),          # 6 tokens
        _seg("alpha f g h i j", 1000, 2000),        # +6 -> 12 >= max 10
    ]
    ideas = await _run(segments, _topics("alpha"), boundary_max_tokens=10)

    assert len(ideas) == 1
    assert ideas[0].trigger is BoundaryTrigger.TOKEN_CAP
    assert estimate_tokens(ideas[0].text) >= 10


async def test_time_cap_forces_boundary_on_long_monologue():
    segments = [
        _seg("alpha one two three", 0, 2000),        # 2s
        _seg("alpha four five six", 2000, 5000),      # spans to 5s (>= 3s cap)
    ]
    ideas = await _run(segments, _topics("alpha"), boundary_max_seconds=3.0)

    assert len(ideas) == 1
    assert ideas[0].trigger is BoundaryTrigger.TIME_CAP


# --- PAUSE -------------------------------------------------------------------
async def test_followed_by_pause_flag_alone_does_not_split_an_idea():
    """A breath is not a change of subject.

    The STT finalizes a segment after STT_PAUSE_SECONDS of silence, so in production essentially
    EVERY segment carries followed_by_pause. Honouring the flag made an idea equal one sentence
    and left semantic drift with nothing to decide. Boundaries are semantic (DRIFT) or a
    genuinely long silence (the inter-segment gap rule) — never the flag on its own.

    Three same-topic segments with the pause flag set on the middle one must stay ONE idea. The
    third segment is what makes this test meaningful: with only two, the end-of-stream flush also
    yields a single PAUSE idea, so the assertion would hold either way and the test would pass
    even if the flag still split the buffer.
    """
    segments = [
        _seg("alpha one two three", 0, 1000),
        _seg("alpha four five six", 1000, 2000, pause=True),  # breath mid-explanation
        _seg("alpha seven eight nine", 2000, 3000),
    ]
    ideas = await _run(segments, _topics("alpha"))

    assert len(ideas) == 1, "a pause flag must not split a continuing explanation"
    assert ideas[0].segment_count == 3
    assert ideas[0].trigger is BoundaryTrigger.PAUSE  # terminal flush at stream end


async def test_context_spans_sentences_and_splits_only_on_topic_change():
    """The production shape: EVERY segment is pause-flagged (that is how STT finalizes).

    Four sentences, all flagged, three about one topic and one about another. The result must be
    two ideas split at the MEANING change — not four ideas split at every breath. This is the
    difference between sentence boundaries and context boundaries, and it is the whole point of
    the drift detector.
    """
    segments = [
        _seg("alpha one two three", 0, 1000, pause=True),
        _seg("alpha four five six", 1000, 2000, pause=True),
        _seg("alpha seven eight nine", 2000, 3000, pause=True),
        _seg("beta a completely different subject", 3000, 4000, pause=True),
    ]
    ideas = await _run(segments, _topics("alpha", "beta"))

    assert len(ideas) == 2, "expected one idea per TOPIC, not one per sentence"
    assert ideas[0].segment_count == 3  # the three alpha sentences held together
    assert ideas[0].trigger is BoundaryTrigger.DRIFT
    assert "beta" not in ideas[0].text
    assert "beta" in ideas[1].text


async def test_silent_gap_between_segments_closes_idea():
    # No pause flag, but a 2s silent gap (>= boundary_pause_seconds) before seg 2.
    segments = [
        _seg("alpha one two three", 0, 1000, pause=False),
        _seg("alpha four five six", 3000, 4000, pause=False),  # gap = 2000ms
    ]
    ideas = await _run(segments, _topics("alpha"), boundary_pause_seconds=0.8)

    # Gap closes idea 1 (PAUSE); seg 2 starts idea 2, flushed at stream end.
    assert len(ideas) == 2
    assert ideas[0].trigger is BoundaryTrigger.PAUSE
    assert ideas[0].segment_count == 1


# --- MIN TOKENS --------------------------------------------------------------
async def test_stray_word_does_not_create_its_own_idea_and_merges_forward():
    segments = [
        _seg("okay", 0, 500, pause=True),  # 1 token, below min -> must not emit
        _seg("alpha two three four five", 500, 2000),  # merges the stray forward
    ]
    ideas = await _run(segments, _topics("alpha"), boundary_min_tokens=3)

    assert len(ideas) == 1  # the stray never became its own idea
    assert ideas[0].segment_count == 2
    assert ideas[0].text.startswith("okay")
    assert "alpha" in ideas[0].text


async def test_pause_on_tiny_buffer_is_ignored():
    segments = [_seg("okay", 0, 500, pause=True)]  # 1 token < min, pause flagged
    ideas = await _run(segments, _topics("alpha"), boundary_min_tokens=3)

    assert ideas == []  # nothing emittable; flush also skips it


# --- STREAM END --------------------------------------------------------------
async def test_trailing_idea_flushed_on_stream_end():
    segments = [
        _seg("alpha one two three", 0, 1000),
        _seg("alpha four five six", 1000, 2000),  # no pause, no cap, no drift
    ]
    ideas = await _run(segments, _topics("alpha"))

    assert len(ideas) == 1
    assert ideas[0].trigger is BoundaryTrigger.PAUSE  # end-of-stream = terminal pause
    assert ideas[0].segment_count == 2


# --- INTERIM -----------------------------------------------------------------
async def test_interim_segments_do_not_trigger_drift():
    embedder = _topics("alpha", "beta")
    # The two alpha finals are contiguous (no real pause); only an interim 'beta'
    # sits between them and must be ignored (not embedded, no drift split).
    segments = [
        _seg("alpha one two three", 0, 1000),
        _seg("beta unstable interim guess", 500, 1000, final=False),  # must be ignored
        _seg("alpha four five six", 1000, 2000),
    ]
    ideas = await _run(segments, embedder)

    # The interim 'beta' neither split the idea nor was embedded.
    assert len(ideas) == 1
    assert ideas[0].trigger is not BoundaryTrigger.DRIFT
    assert ideas[0].segment_count == 2
    assert "beta" not in ideas[0].text
    assert "beta unstable interim guess" not in embedder.calls


async def test_representative_vector_is_populated():
    segments = [_seg("alpha one two three", 0, 1000), _seg("alpha four five", 1000, 2000)]
    ideas = await _run(segments, _topics("alpha"))

    assert ideas[0].representative_vector is not None
    assert len(ideas[0].representative_vector) == 2  # one topic + default axis
