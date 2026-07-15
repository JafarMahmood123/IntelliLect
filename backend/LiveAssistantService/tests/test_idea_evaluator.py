"""Deterministic, OFFLINE tests for the LA-4 IdeaEvaluator orchestration.

Uses FakeRetrievalClient + FakeBrainClient (spies) — no KnowledgeService, no Ollama.
"""

from __future__ import annotations

from collections.abc import AsyncIterator
from uuid import uuid4

from app.api.dependencies import build_idea_evaluator
from app.application.services.idea_evaluator import IdeaEvaluationPipeline
from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from app.domain.idea.boundary_trigger import BoundaryTrigger
from app.domain.idea.completed_idea import CompletedIdea
from app.infrastructure.config.settings import Settings
from tests.support.fake_brain_client import FakeBrainClient
from tests.support.fake_retrieval_client import FakeRetrievalClient


def _idea(text: str = "some explanation") -> CompletedIdea:
    return CompletedIdea(text, 0, 1000, 1, BoundaryTrigger.PAUSE)


def _session() -> SessionContext:
    return SessionContext(uuid4(), uuid4(), "teacher-1", "room-1")


def _chunk(score: float, **loc) -> RetrievedChunk:
    return RetrievedChunk("material", score, uuid4(), uuid4(), **loc)


def _evaluator(retrieval, brain, *, min_score=0.25, top_k=6):
    settings = Settings(retrieval_min_score=min_score, retrieval_top_k=top_k)
    return build_idea_evaluator(settings, retrieval, brain)


async def test_happy_path_returns_brain_feedback_over_relevant_chunks():
    chunks = [_chunk(0.80, slide=4), _chunk(0.60, page=12)]
    suggestion = TeacherSuggestion("clarify X [1]", FeedbackType.DISCREPANCY, [1], [chunks[0]])
    brain = FakeBrainClient(EvaluationOutcome(True, suggestion))
    retrieval = FakeRetrievalClient(chunks)
    idea, session = _idea("photosynthesis is in the mitochondria"), _session()

    outcome = await _evaluator(retrieval, brain).evaluate(idea, session)

    assert outcome.has_feedback is True
    assert outcome.suggestion is suggestion
    # Retrieval was queried with the idea text + configured top_k.
    assert retrieval.calls == [(session.classroom_id, idea.text, 6)]
    # Brain saw exactly the relevant chunks.
    assert brain.calls[0][1] == chunks


async def test_no_results_short_circuit_never_calls_brain():
    # Every chunk is below RETRIEVAL_MIN_SCORE -> nothing relevant remains.
    brain = FakeBrainClient(EvaluationOutcome(True, TeacherSuggestion("x", FeedbackType.GAP)))
    retrieval = FakeRetrievalClient([_chunk(0.10), _chunk(0.24)])

    outcome = await _evaluator(retrieval, brain, min_score=0.25).evaluate(_idea(), _session())

    assert outcome.has_feedback is False
    assert outcome.suggestion is None
    assert brain.called is False  # the brain must NOT be invoked


async def test_empty_retrieval_short_circuits():
    brain = FakeBrainClient(EvaluationOutcome(True, TeacherSuggestion("x", FeedbackType.GAP)))
    outcome = await _evaluator(FakeRetrievalClient([]), brain).evaluate(_idea(), _session())

    assert outcome.has_feedback is False
    assert brain.called is False


async def test_score_equal_to_min_is_kept():
    brain = FakeBrainClient(EvaluationOutcome(True, TeacherSuggestion("x", FeedbackType.UNCLEAR)))
    retrieval = FakeRetrievalClient([_chunk(0.25)])  # exactly the threshold

    outcome = await _evaluator(retrieval, brain, min_score=0.25).evaluate(_idea(), _session())

    assert brain.called is True
    assert outcome.has_feedback is True


async def test_consistent_idea_yields_no_feedback_but_calls_brain():
    brain = FakeBrainClient(EvaluationOutcome.none())
    retrieval = FakeRetrievalClient([_chunk(0.9)])

    outcome = await _evaluator(retrieval, brain).evaluate(_idea(), _session())

    assert outcome.has_feedback is False
    assert outcome.suggestion is None
    assert brain.called is True  # relevant material existed; brain decided "no feedback"


async def test_retrieval_error_degrades_to_no_feedback():
    brain = FakeBrainClient(EvaluationOutcome(True, TeacherSuggestion("x", FeedbackType.GAP)))
    retrieval = FakeRetrievalClient(error=RuntimeError("KnowledgeService down"))

    outcome = await _evaluator(retrieval, brain).evaluate(_idea(), _session())

    assert outcome.has_feedback is False
    assert brain.called is False  # never reached the brain


async def test_brain_error_degrades_to_no_feedback():
    brain = FakeBrainClient(error=RuntimeError("Ollama timeout"))
    retrieval = FakeRetrievalClient([_chunk(0.9)])

    outcome = await _evaluator(retrieval, brain).evaluate(_idea(), _session())

    assert outcome.has_feedback is False  # a failed evaluation never breaks the loop


async def test_pipeline_evaluates_a_stream_of_ideas():
    brain = FakeBrainClient(EvaluationOutcome(True, TeacherSuggestion("y", FeedbackType.GAP)))
    pipeline = IdeaEvaluationPipeline(_evaluator(FakeRetrievalClient([_chunk(0.9)]), brain))
    session = _session()

    async def _ideas() -> AsyncIterator[CompletedIdea]:
        yield _idea("first")
        yield _idea("second")

    outcomes = [o async for o in pipeline.process(_ideas(), session)]

    assert len(outcomes) == 2
    assert all(o.has_feedback for o in outcomes)
    assert len(brain.calls) == 2
