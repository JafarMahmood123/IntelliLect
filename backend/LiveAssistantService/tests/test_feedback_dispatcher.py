"""FeedbackDispatcher connector — teacher-only routing, no-feedback drop, resilience."""

from __future__ import annotations

from collections.abc import AsyncIterator
from uuid import uuid4

from app.api.dependencies import build_feedback_dispatcher
from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from tests.support.fake_feedback_sink import FakeFeedbackSink


def _session(teacher="teacher-1") -> SessionContext:
    return SessionContext(uuid4(), uuid4(), teacher, "room")


def _feedback() -> EvaluationOutcome:
    return EvaluationOutcome(True, TeacherSuggestion("do X", FeedbackType.GAP, [], []))


async def test_no_feedback_outcome_is_dropped_without_sending():
    sink = FakeFeedbackSink()
    sent = await build_feedback_dispatcher(sink).dispatch(EvaluationOutcome.none(), _session())

    assert sent is False
    assert sink.called is False  # FeedbackSink.send is NOT called


async def test_feedback_is_delivered_to_the_teacher_only():
    sink = FakeFeedbackSink()
    session = _session("teacher-42")

    sent = await build_feedback_dispatcher(sink).dispatch(_feedback(), session)

    assert sent is True
    assert len(sink.calls) == 1
    # THE invariant: the suggestion is addressed to the teacher identity and only it.
    assert sink.target_identities == ["teacher-42"]
    assert "student-1" not in sink.target_identities
    assert sink.calls[0][0] is session


async def test_sink_error_is_swallowed_and_loop_survives():
    sink = FakeFeedbackSink(error=RuntimeError("publish failed"))

    sent = await build_feedback_dispatcher(sink).dispatch(_feedback(), _session())

    assert sent is False  # delivery failed but no exception escaped


async def test_process_stream_dispatches_each_outcome():
    sink = FakeFeedbackSink()
    session = _session()

    async def _outcomes() -> AsyncIterator[EvaluationOutcome]:
        yield EvaluationOutcome.none()  # dropped
        yield _feedback()               # delivered
        yield _feedback()               # delivered

    results = [r async for r in build_feedback_dispatcher(sink).process(_outcomes(), session)]

    assert results == [False, True, True]
    assert len(sink.calls) == 2  # only the two feedback outcomes were sent
