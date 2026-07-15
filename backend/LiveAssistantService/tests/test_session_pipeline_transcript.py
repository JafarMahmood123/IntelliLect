"""SessionPipeline transcript persistence (S-0), wired to fakes — no models, no DB.

Verifies that as the (faked) STT emits segments, the pipeline persists FINAL segments
incrementally and in order, excludes interim segments, finalizes on session end, and
that a failing store is NON-FATAL to the live feedback loop.
"""

from __future__ import annotations

import logging
from uuid import uuid4

from app.api.dependencies import build_boundary_detector, build_idea_evaluator
from app.application.services.feedback_dispatcher import FeedbackDispatcher
from app.application.services.feedback_pacer import FeedbackPacer
from app.application.services.session_pipeline import SessionPipeline
from app.application.services.transcript_recorder import TranscriptRecorder
from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from app.domain.transcript.transcript_segment import TranscriptSegment
from app.domain.transcript.transcript_status import TranscriptStatus
from app.infrastructure.audio.fake_audio_source import FakeAudioSource
from app.infrastructure.config.settings import Settings
from app.infrastructure.persistence.in_memory_transcript_repository import (
    InMemoryTranscriptRepository,
)
from tests.support.fake_brain_client import FakeBrainClient
from tests.support.fake_embedding_provider import FakeEmbeddingProvider
from tests.support.fake_feedback_sink import FakeFeedbackSink
from tests.support.fake_retrieval_client import FakeRetrievalClient
from tests.support.fake_speech_to_text import FakeSpeechToText


def _seg(text, start_ms, end_ms, *, final=True, pause=False) -> TranscriptSegment:
    return TranscriptSegment(text, start_ms, end_ms, is_final=final, followed_by_pause=pause)


# One interim (must be excluded) then three finals across two ideas.
def _scripted_segments() -> list[TranscriptSegment]:
    return [
        _seg("alpha idea one", 0, 1000, final=False),          # INTERIM -> not persisted
        _seg("alpha idea one two", 0, 1000),
        _seg("alpha idea three four", 1000, 2000, pause=True),  # closes idea 1 (PAUSE)
        _seg("beta different topic five", 2000, 3000),         # idea 2, flushed at end
    ]


_EXPECTED_FINAL_TEXTS = [
    "alpha idea one two",
    "alpha idea three four",
    "beta different topic five",
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


def _permissive_pacer() -> FeedbackPacer:
    import itertools

    counter = itertools.count()
    return FeedbackPacer(
        min_interval_sec=0.0, confidence_min=0.0, dedup_window_sec=0.0,
        dedup_similarity=2.0, max_per_session=0, clock=lambda: float(next(counter)),
    )


def _build_pipeline(repo, *, sink=None):
    settings = _settings()
    sink = sink or FakeFeedbackSink()
    session = SessionContext(uuid4(), uuid4(), "teacher-1", "room-1")
    audio = FakeAudioSource(settings, wav_path=None, tone_seconds=0.05)
    stt = FakeSpeechToText(_scripted_segments())
    boundary = build_boundary_detector(
        settings, FakeEmbeddingProvider.orthogonal_topics(["alpha", "beta"])
    )
    evaluator = build_idea_evaluator(
        settings, FakeRetrievalClient([_chunk()]), FakeBrainClient(_feedback_outcome())
    )
    dispatcher = FeedbackDispatcher(sink)
    recorder = TranscriptRecorder(repo, session.session_id, session.classroom_id)
    pipeline = SessionPipeline(
        session, audio, stt, boundary, evaluator, _permissive_pacer(), dispatcher, recorder
    )
    return pipeline, session, sink


async def test_final_segments_persisted_in_order_interim_excluded_and_finalized():
    repo = InMemoryTranscriptRepository()
    pipeline, session, sink = _build_pipeline(repo)

    await pipeline.start()  # runs to completion (finite audio)

    stored = await repo.get_transcript(session.session_id)
    assert [s.order_index for s in stored] == [0, 1, 2]           # sequential
    assert [s.text for s in stored] == _EXPECTED_FINAL_TEXTS       # ordered, interim excluded
    assert await repo.assemble_text(session.session_id) == " ".join(_EXPECTED_FINAL_TEXTS)

    header = await repo.get_session_transcript(session.session_id)
    assert header is not None
    assert header.classroom_id == session.classroom_id
    assert header.status is TranscriptStatus.FINALIZED            # finalize on session end

    # Feedback still flowed normally (two ideas -> two teacher-only sends).
    assert len(sink.calls) == 2


class _FailingAppendRepository(InMemoryTranscriptRepository):
    async def append_segment(self, session_id, segment) -> None:
        raise RuntimeError("store is down")


async def test_persistence_failure_is_non_fatal_to_the_feedback_loop(caplog):
    repo = _FailingAppendRepository()
    pipeline, session, sink = _build_pipeline(repo)

    with caplog.at_level(logging.WARNING, logger="liveassistant.transcript"):
        await pipeline.start()  # must NOT raise despite every append failing

    # The session ran to completion and delivered feedback for both ideas.
    assert len(sink.calls) == 2
    # The lost segments were logged, and the transcript was still finalized.
    assert any("transcript_append_failed" in r.message for r in caplog.records)
    header = await repo.get_session_transcript(session.session_id)
    assert header is not None and header.status is TranscriptStatus.FINALIZED
