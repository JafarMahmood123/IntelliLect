"""What happens when a session ends badly — a crash, a cancellation, a source that never connects.

Every session ends. Most end because the audio ran out, and that path is well covered. These are
the other endings, and they share one property that makes them worth their own file: the cleanup
they skip is invisible at the time and only shows up later, in another service.

A transcript that is never finalized stays mid-write forever, and ClassroomService's summary
never finds it. Pacing state that is never released keeps a dead session's rate limit and
dedup window in memory for the life of the process — which, on a re-run of the same session id,
silences real feedback. And a fault that escapes the run loop takes the agent out of a room
where a teacher is still talking.
"""

from __future__ import annotations

import asyncio
import itertools
from uuid import uuid4

from app.api.dependencies import build_boundary_detector, build_idea_evaluator
from app.application.ports.audio_source import AudioSource
from app.application.services.feedback_dispatcher import FeedbackDispatcher
from app.application.services.feedback_pacer import FeedbackPacer
from app.application.services.last_idea_store import LastIdeaStore
from app.application.services.session_pipeline import SessionPipeline
from app.application.services.transcript_recorder import TranscriptRecorder
from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from app.domain.idea.boundary_trigger import BoundaryTrigger
from app.domain.idea.completed_idea import CompletedIdea
from app.domain.transcript.transcript_segment import TranscriptSegment
from app.domain.transcript.transcript_status import TranscriptStatus
from app.infrastructure.config.settings import Settings
from app.infrastructure.persistence.in_memory_transcript_repository import (
    InMemoryTranscriptRepository,
)
from tests.support.fake_brain_client import FakeBrainClient
from tests.support.fake_embedding_provider import FakeEmbeddingProvider
from tests.support.fake_feedback_sink import FakeFeedbackSink
from tests.support.fake_retrieval_client import FakeRetrievalClient
from tests.support.fake_speech_to_text import FakeSpeechToText

# A phrase that must never reach a log line: the transcript is course content, and the run loop's
# error handler deliberately records the exception TYPE only.
SECRET_PHRASE = "SECRET_LECTURE_CONTENT_alpha"


def _settings() -> Settings:
    return Settings(
        target_sample_rate=16000,
        boundary_min_tokens=3,
        boundary_drift_threshold=0.35,
        boundary_pause_seconds=0.8,
        retrieval_min_score=0.25,
    )


def _segments() -> list[TranscriptSegment]:
    return [
        TranscriptSegment(f"alpha {SECRET_PHRASE} one two", 0, 1000, is_final=True,
                          followed_by_pause=True),
        TranscriptSegment("beta different topic five", 1000, 2000, is_final=True,
                          followed_by_pause=False),
    ]


def _chunk() -> RetrievedChunk:
    return RetrievedChunk("material", 0.9, uuid4(), uuid4(), slide=1)


def _outcome() -> EvaluationOutcome:
    return EvaluationOutcome(
        True, TeacherSuggestion("fix [1]", FeedbackType.GAP, [1], [_chunk()])
    )


def _pacer() -> FeedbackPacer:
    counter = itertools.count()
    return FeedbackPacer(
        min_interval_sec=0.0, confidence_min=0.0, dedup_window_sec=0.0,
        dedup_similarity=2.0, max_per_session=0, clock=lambda: float(next(counter)),
    )


class RecordingAudioSource(AudioSource):
    """An audio source whose connect/frames/disconnect behaviour a test dictates.

    `disconnect` is what returns the agent's seat in the LiveKit room, so whether it was called
    is the whole question in most of these tests.
    """

    def __init__(self, *, connect_error: Exception | None = None,
                 frames_error: Exception | None = None, hang: bool = False,
                 disconnect_error: Exception | None = None) -> None:
        self.connect_error = connect_error
        self.frames_error = frames_error
        self.hang = hang
        self.disconnect_error = disconnect_error
        self.connected = False
        self.disconnect_calls = 0

    async def connect(self, session: SessionContext) -> None:
        if self.connect_error:
            raise self.connect_error
        self.connected = True

    async def frames(self):
        if self.frames_error:
            raise self.frames_error
        if self.hang:
            # Never yields and never returns — stands in for a room that stays silent, so a
            # cancellation lands while the loop is genuinely mid-session.
            await asyncio.Event().wait()
        return
        yield  # pragma: no cover — makes this an async generator

    async def disconnect(self) -> None:
        self.disconnect_calls += 1
        if self.disconnect_error:
            raise self.disconnect_error


def _build(audio, *, recorder=None, last_ideas=None, pacer=None, session=None):
    settings = _settings()
    session = session or SessionContext(uuid4(), uuid4(), "teacher-1", "room-1")
    stt = FakeSpeechToText(_segments())
    boundary = build_boundary_detector(
        settings, FakeEmbeddingProvider.orthogonal_topics(["alpha", "beta"])
    )
    evaluator = build_idea_evaluator(
        settings, FakeRetrievalClient([_chunk()]), FakeBrainClient(_outcome())
    )
    pipeline = SessionPipeline(
        session, audio, stt, boundary, evaluator,
        pacer or _pacer(), FeedbackDispatcher(FakeFeedbackSink()),
        recorder, None, last_ideas,
    )
    return pipeline, session


# --- a crash mid-session ------------------------------------------------------------------


async def test_a_crash_mid_session_does_not_propagate_out_of_the_run_loop():
    # The agent is a background task in a live room. An exception escaping here kills the task
    # with no one awaiting it, and the teacher simply notices the assistant went quiet.
    audio = RecordingAudioSource(frames_error=RuntimeError("the room went away"))
    pipeline, _ = _build(audio)

    await pipeline.start()  # must not raise


async def test_a_crash_still_returns_the_agent_s_seat_in_the_room():
    audio = RecordingAudioSource(frames_error=RuntimeError("the room went away"))
    pipeline, _ = _build(audio)

    await pipeline.start()

    assert audio.disconnect_calls == 1


async def test_a_crash_still_finalizes_the_transcript():
    """The failure that is invisible until somebody asks for the summary.

    A transcript left un-finalized is not an error anywhere — it simply never becomes readable,
    and ClassroomService's summary for that session comes back empty with nothing to explain it.
    """
    repository = InMemoryTranscriptRepository()
    audio = RecordingAudioSource(frames_error=RuntimeError("the room went away"))
    session = SessionContext(uuid4(), uuid4(), "teacher-1", "room-1")
    recorder = TranscriptRecorder(repository, session.session_id, session.classroom_id)
    pipeline, _ = _build(audio, recorder=recorder, session=session)

    await pipeline.start()

    header = await repository.get_session_transcript(session.session_id)
    assert header is not None
    assert header.status is TranscriptStatus.FINALIZED


async def test_a_crash_logs_the_error_type_and_never_the_lecture_content():
    # The transcript is course material. A traceback or a raw message in the logs puts it
    # somewhere it was never meant to be, and logs outlive the session by a long way.
    audio = RecordingAudioSource(frames_error=RuntimeError(f"failed on: {SECRET_PHRASE}"))
    pipeline, _ = _build(audio)

    import logging

    records: list[logging.LogRecord] = []

    class _Capture(logging.Handler):
        def emit(self, record):
            records.append(record)

    handler = _Capture()
    logger = logging.getLogger("liveassistant.pipeline")
    logger.addHandler(handler)
    try:
        await pipeline.start()
    finally:
        logger.removeHandler(handler)

    crashed = [r for r in records if r.getMessage() == "session_crashed"]
    assert crashed, "the crash should have been logged"
    assert getattr(crashed[0], "error_type", None) == "RuntimeError"
    assert all(SECRET_PHRASE not in str(getattr(r, "args", "")) for r in records)
    assert all(SECRET_PHRASE not in r.getMessage() for r in records)


async def test_a_crash_releases_the_ideas_retained_for_that_session():
    """State keyed by session id, held in memory for the life of the process.

    Leaked, this is not a leak that shows up as memory. It shows up as the quiz generator, on a
    later run of the same session id, being offered ideas from a lecture that already ended.

    The store is seeded first ON PURPOSE: in a crash the loop may never complete an idea of its
    own, so an assertion on an empty store would pass whether or not anything was ever released.
    """
    audio = RecordingAudioSource(frames_error=RuntimeError("the room went away"))
    last_ideas = LastIdeaStore(history=4)
    session = SessionContext(uuid4(), uuid4(), "teacher-1", "room-1")
    last_ideas.record(
        session.session_id,
        CompletedIdea("an idea from earlier in the lecture", 0, 1000, 1, BoundaryTrigger.PAUSE),
    )
    assert last_ideas.recent(session.session_id) != []  # the seed really is there

    pipeline, _ = _build(audio, last_ideas=last_ideas, session=session)
    await pipeline.start()

    assert last_ideas.recent(session.session_id) == []


async def test_a_crash_releases_the_session_s_pacing_state():
    """The rate limit, the dedup window and the per-session cap all live in the pacer.

    Left behind, a re-run of the same session id starts already rate-limited and already holding
    a dedup window — so the first real feedback of the new run is silently dropped.
    """
    class _RecordingPacer(FeedbackPacer):
        def __init__(self) -> None:
            counter = itertools.count()
            super().__init__(
                min_interval_sec=0.0, confidence_min=0.0, dedup_window_sec=0.0,
                dedup_similarity=2.0, max_per_session=0, clock=lambda: float(next(counter)),
            )
            self.reset_calls: list[object] = []

        def reset(self, session_id) -> None:
            self.reset_calls.append(session_id)
            super().reset(session_id)

    audio = RecordingAudioSource(frames_error=RuntimeError("the room went away"))
    pacer = _RecordingPacer()
    pipeline, session = _build(audio, pacer=pacer)

    await pipeline.start()

    assert pacer.reset_calls == [session.session_id]


# --- cancellation -------------------------------------------------------------------------


async def test_stopping_a_running_session_cleans_up_and_stays_stopped():
    # The ordinary end: the teacher ends the class, or the service shuts down.
    audio = RecordingAudioSource(hang=True)
    pipeline, _ = _build(audio)

    task = pipeline.start()
    await asyncio.sleep(0)  # let it reach the audio source
    await pipeline.stop()

    assert audio.disconnect_calls == 1
    # `stop` awaits the cancelled task itself, so it is settled by the time it returns.
    assert task.cancelled()


async def test_a_cancelled_session_still_finalizes_its_transcript():
    # Shutdown is not a reason to lose what was already said.
    repository = InMemoryTranscriptRepository()
    audio = RecordingAudioSource(hang=True)
    session = SessionContext(uuid4(), uuid4(), "teacher-1", "room-1")
    recorder = TranscriptRecorder(repository, session.session_id, session.classroom_id)
    pipeline, _ = _build(audio, recorder=recorder, session=session)

    task = pipeline.start()
    await asyncio.sleep(0)
    await pipeline.stop()  # awaits the cancelled task itself

    header = await repository.get_session_transcript(session.session_id)
    assert header is not None
    assert header.status is TranscriptStatus.FINALIZED


async def test_stopping_a_session_that_never_started_still_releases_the_source():
    # `stop` runs on shutdown regardless, and a partially-acquired room seat still has to go
    # back. Already covered for the no-op case; this pins that disconnect is the reason.
    audio = RecordingAudioSource()
    pipeline, _ = _build(audio)

    await pipeline.stop()

    assert audio.disconnect_calls == 1


# --- cleanup that itself fails --------------------------------------------------------------


async def test_a_disconnect_that_fails_does_not_hide_the_rest_of_the_cleanup():
    """Teardown runs when things are already going wrong, so its own steps can fail too.

    If a failing disconnect propagated, the finalize and the state release after it would never
    run — the cleanup for the failure would be broken by the failure.
    """
    repository = InMemoryTranscriptRepository()
    audio = RecordingAudioSource(
        frames_error=RuntimeError("the room went away"),
        disconnect_error=RuntimeError("and the disconnect failed too"),
    )
    session = SessionContext(uuid4(), uuid4(), "teacher-1", "room-1")
    recorder = TranscriptRecorder(repository, session.session_id, session.classroom_id)
    last_ideas = LastIdeaStore(history=4)
    pipeline, _ = _build(audio, recorder=recorder, last_ideas=last_ideas, session=session)

    await pipeline.start()  # must not raise

    assert last_ideas.recent(session.session_id) == []


async def test_a_source_that_never_connects_is_still_torn_down():
    # LiveKit is unreachable, or the token was refused. Nothing was acquired, but the cleanup
    # path still has to run rather than being skipped because connect never returned.
    audio = RecordingAudioSource(connect_error=RuntimeError("livekit refused the token"))
    pipeline, _ = _build(audio)

    await pipeline.start()

    assert audio.disconnect_calls == 1
    assert audio.connected is False
