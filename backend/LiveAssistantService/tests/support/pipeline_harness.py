"""Shared harness to build + run a REAL SessionPipeline wired to fakes (LA-8 tests).

A fixed 4-idea script exercises the whole loop: boundary segmentation (drift + flush),
evaluation (feedback / no-feedback / a brain error), pacing (deliver + rate-limit), and
delivery. Reused by the e2e, logging, and metrics tests.
"""

from __future__ import annotations

import itertools
from uuid import uuid4

from app.api.dependencies import build_boundary_detector, build_idea_evaluator
from app.application.ports.brain_client import BrainClient
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
from tests.support.fake_embedding_provider import FakeEmbeddingProvider
from tests.support.fake_feedback_sink import FakeFeedbackSink
from tests.support.fake_retrieval_client import FakeRetrievalClient
from tests.support.fake_speech_to_text import FakeSpeechToText

# Distinctive phrases that MUST NEVER appear in logs (privacy assertions).
SUGGESTION_TEXT = "SECRET_SUGGESTION_PHRASE reconsider the chloroplast [1]"
CHUNK_TEXT = "CHUNK_SECRET_TEXT about photosynthesis"
IDEA_PHRASE = "wrong statement"  # substring of the idea texts below

# Four topics -> four ideas: alpha/gamma/delta flagged ("wrong"/"boom"), beta consistent.
_SCRIPT = [
    ("alpha wrong statement one two", 0, 1000),
    ("beta correct statement three four", 1000, 2000),
    ("gamma boom raise five six", 2000, 3000),      # brain raises -> resilience
    ("delta wrong statement seven eight", 3000, 4000),  # flushed at stream end
]
_TOPICS = ["alpha", "beta", "gamma", "delta"]


def _segments() -> list[TranscriptSegment]:
    return [TranscriptSegment(t, s, e, is_final=True, followed_by_pause=False) for t, s, e in _SCRIPT]


def scenario_settings() -> Settings:
    return Settings(
        target_sample_rate=16000,
        boundary_min_tokens=3,
        boundary_drift_threshold=0.35,
        boundary_pause_seconds=0.8,
        retrieval_min_score=0.25,
    )


def _feedback(ftype: FeedbackType) -> EvaluationOutcome:
    chunk = RetrievedChunk(CHUNK_TEXT, 0.9, uuid4(), uuid4(), slide=1)
    return EvaluationOutcome(
        True, TeacherSuggestion(SUGGESTION_TEXT, ftype, [1], [chunk], confidence=0.9)
    )


class ScriptedBrainClient(BrainClient):
    """Feedback for 'wrong' ideas, silence for others, and a raise for 'boom'."""

    def __init__(self) -> None:
        self.calls = 0

    async def evaluate(self, idea, chunks) -> EvaluationOutcome:
        self.calls += 1
        text = idea.text.lower()
        if "boom" in text:
            raise RuntimeError("scripted brain failure")
        if "wrong" in text:
            return _feedback(FeedbackType.DISCREPANCY)
        return EvaluationOutcome.none()


def counter_clock():
    """Strictly-increasing fake clock (0, 1, 2, ...)."""
    counter = itertools.count()
    return lambda: float(next(counter))


def rate_limiting_pacer() -> FeedbackPacer:
    """Delivers the first suggestion, rate-limits the rest (dedup/confidence off)."""
    return FeedbackPacer(
        min_interval_sec=1000.0, confidence_min=0.0, dedup_window_sec=0.0,
        dedup_similarity=2.0, max_per_session=0, clock=counter_clock(),
    )


def build_scenario_pipeline(
    session: SessionContext,
    *,
    sink: FakeFeedbackSink,
    brain: ScriptedBrainClient,
    pacer: FeedbackPacer | None = None,
    settings: Settings | None = None,
) -> SessionPipeline:
    settings = settings or scenario_settings()
    audio = FakeAudioSource(settings, wav_path=None, tone_seconds=0.05)
    stt = FakeSpeechToText(_segments())
    boundary = build_boundary_detector(settings, FakeEmbeddingProvider.orthogonal_topics(_TOPICS))
    evaluator = build_idea_evaluator(
        settings, FakeRetrievalClient([RetrievedChunk(CHUNK_TEXT, 0.9, uuid4(), uuid4(), slide=1)]), brain
    )
    dispatcher = FeedbackDispatcher(sink)
    return SessionPipeline(
        session, audio, stt, boundary, evaluator, pacer or rate_limiting_pacer(), dispatcher
    )


async def run_scenario() -> tuple[SessionContext, FakeFeedbackSink, ScriptedBrainClient]:
    """Build + run one pipeline to completion; return (session, sink, brain)."""
    session = SessionContext(uuid4(), uuid4(), "teacher-1", "room-1")
    sink, brain = FakeFeedbackSink(), ScriptedBrainClient()
    pipeline = build_scenario_pipeline(session, sink=sink, brain=brain)
    await pipeline.start()  # runs to completion (finite audio)
    return session, sink, brain
