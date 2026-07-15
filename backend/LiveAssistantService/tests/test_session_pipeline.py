"""SessionPipeline end to end with fakes — the full LA-1..LA-7 loop, no models."""

from __future__ import annotations

import itertools
from uuid import uuid4

from app.api.dependencies import build_boundary_detector, build_idea_evaluator
from app.application.services.feedback_dispatcher import FeedbackDispatcher
from app.application.services.feedback_pacer import FeedbackPacer
from app.application.services.session_pipeline import SessionPipeline
from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from app.domain.transcript.transcript_segment import TranscriptSegment
from app.infrastructure.audio.fake_audio_source import FakeAudioSource
from app.infrastructure.config.settings import Settings
from tests.support.fake_brain_client import FakeBrainClient
from tests.support.fake_embedding_provider import FakeEmbeddingProvider
from tests.support.fake_feedback_sink import FakeFeedbackSink
from tests.support.fake_retrieval_client import FakeRetrievalClient
from tests.support.fake_speech_to_text import FakeSpeechToText


def _seg(text, start_ms, end_ms, *, pause=False) -> TranscriptSegment:
    return TranscriptSegment(text, start_ms, end_ms, is_final=True, followed_by_pause=pause)


# Two ideas: 'alpha' closes on a pause; 'beta' is flushed at stream end.
def _scripted_segments() -> list[TranscriptSegment]:
    return [
        _seg("alpha idea one two", 0, 1000),
        _seg("alpha idea three four", 1000, 2000, pause=True),  # closes idea 1 (PAUSE)
        _seg("beta different topic five", 2000, 3000),          # idea 2, flushed at end
    ]


def _settings() -> Settings:
    return Settings(
        target_sample_rate=16000,
        boundary_min_tokens=3,
        boundary_drift_threshold=0.35,
        boundary_pause_seconds=0.8,
        retrieval_min_score=0.25,
    )


def _chunk(score=0.9) -> RetrievedChunk:
    return RetrievedChunk("material", score, uuid4(), uuid4(), slide=1)


def _feedback_outcome() -> EvaluationOutcome:
    return EvaluationOutcome(True, TeacherSuggestion("fix [1]", FeedbackType.GAP, [1], [_chunk()]))


def _counter_clock():
    """A strictly-increasing fake clock (0, 1, 2, ...) — deterministic, no real time."""
    counter = itertools.count()
    return lambda: float(next(counter))


def _permissive_pacer() -> FeedbackPacer:
    """Delivers everything: no rate limit, no confidence floor, no dedup, no cap."""
    return FeedbackPacer(
        min_interval_sec=0.0, confidence_min=0.0, dedup_window_sec=0.0,
        dedup_similarity=2.0, max_per_session=0, clock=_counter_clock(),
    )


def _build_pipeline(settings, sink, *, brain=None, evaluator=None, pacer=None):
    session = SessionContext(uuid4(), uuid4(), "teacher-1", "room-1")
    audio = FakeAudioSource(settings, wav_path=None, tone_seconds=0.05)
    stt = FakeSpeechToText(_scripted_segments())
    boundary = build_boundary_detector(settings, FakeEmbeddingProvider.orthogonal_topics(["alpha", "beta"]))
    if evaluator is None:
        evaluator = build_idea_evaluator(
            settings, FakeRetrievalClient([_chunk()]), brain or FakeBrainClient(_feedback_outcome())
        )
    dispatcher = FeedbackDispatcher(sink)
    return SessionPipeline(
        session, audio, stt, boundary, evaluator, pacer or _permissive_pacer(), dispatcher
    ), session


async def test_pipeline_delivers_feedback_for_each_idea_and_flushes_trailing():
    settings, sink = _settings(), FakeFeedbackSink()
    pipeline, session = _build_pipeline(settings, sink)

    await pipeline.start()  # runs to completion (finite audio)

    # Two ideas (pause-closed 'alpha' + end-flushed 'beta') -> two teacher-only sends.
    assert len(sink.calls) == 2
    assert sink.target_identities == [session.teacher_identity, session.teacher_identity]


async def test_pipeline_drops_no_feedback_ideas():
    settings, sink = _settings(), FakeFeedbackSink()
    pipeline, _ = _build_pipeline(settings, sink, brain=FakeBrainClient(EvaluationOutcome.none()))

    await pipeline.start()

    assert sink.called is False  # relevant material existed, but the brain stayed silent


async def test_pipeline_survives_a_bad_idea_and_keeps_running():
    class _RaiseOnceEvaluator:
        def __init__(self) -> None:
            self.calls = 0

        async def evaluate(self, idea, session):
            self.calls += 1
            if self.calls == 1:
                raise RuntimeError("evaluation blew up on the first idea")
            return _feedback_outcome()

    settings, sink = _settings(), FakeFeedbackSink()
    evaluator = _RaiseOnceEvaluator()
    pipeline, _ = _build_pipeline(settings, sink, evaluator=evaluator)

    await pipeline.start()  # must not raise despite the first idea failing

    assert evaluator.calls == 2          # both ideas were attempted
    assert len(sink.calls) == 1          # only the second (good) idea was delivered


async def test_stop_before_start_is_safe():
    settings, sink = _settings(), FakeFeedbackSink()
    pipeline, _ = _build_pipeline(settings, sink)

    await pipeline.stop()  # never started -> no error


async def test_pipeline_routes_feedback_through_the_pacer_and_suppresses():
    # Rate-limit everything after the first: two ideas -> only the first is delivered.
    settings, sink = _settings(), FakeFeedbackSink()
    rate_limiting_pacer = FeedbackPacer(
        min_interval_sec=1000.0, confidence_min=0.0, dedup_window_sec=0.0,
        dedup_similarity=2.0, max_per_session=0, clock=_counter_clock(),
    )
    pipeline, _ = _build_pipeline(settings, sink, pacer=rate_limiting_pacer)

    await pipeline.start()

    # The second idea's suggestion was suppressed by the pacer -> never reached the sink.
    assert len(sink.calls) == 1
