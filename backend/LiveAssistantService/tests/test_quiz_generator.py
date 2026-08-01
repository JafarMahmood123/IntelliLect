"""Deterministic, OFFLINE tests for quiz generation — no KnowledgeService, no brain.

The behaviour worth protecting is where this deliberately DIVERGES from ``IdeaEvaluator``: a
teacher asked for a quiz, so nothing here may quietly return nothing. Retrieval failing must
downgrade the quiz, not cancel it; the brain failing must reach the teacher rather than be
swallowed the way a failed evaluation is.
"""

from __future__ import annotations

from uuid import uuid4

import pytest

from app.application.services.last_idea_store import LastIdeaStore
from app.application.services.quiz_generator import (
    NoIdeaAvailable,
    QuizGenerationFailed,
    QuizGenerator,
)
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.idea.boundary_trigger import BoundaryTrigger
from app.domain.idea.completed_idea import CompletedIdea
from app.domain.quiz.generated_quiz import GeneratedOption, GeneratedQuestion, GeneratedQuiz
from tests.support.fake_retrieval_client import FakeRetrievalClient

BOUNDS = {"question_count": 3, "min_options": 2, "max_options": 4}


_UNSET = object()


class RecordingBrain:
    """Captures what it was asked to generate from, and returns a canned quiz (or raises).

    ``quiz`` uses a sentinel default so that passing ``None`` explicitly — the "model produced
    nothing usable" case — is distinguishable from not passing it at all.
    """

    def __init__(self, quiz=_UNSET, *, error: Exception | None = None):
        self._quiz = _quiz() if quiz is _UNSET else quiz
        self._error = error
        self.calls: list[tuple[str, list[RetrievedChunk]]] = []

    async def generate_quiz(self, idea_text, chunks, **kwargs):
        self.calls.append((idea_text, list(chunks)))
        if self._error is not None:
            raise self._error
        return self._quiz


def _quiz() -> GeneratedQuiz:
    return GeneratedQuiz(
        title="Caching",
        questions=[
            GeneratedQuestion("What is a cache miss?", [
                GeneratedOption("Not in the cache", True),
                GeneratedOption("In the cache", False),
            ])
        ],
    )


def _idea(text: str) -> CompletedIdea:
    return CompletedIdea(text, 0, 1000, 1, BoundaryTrigger.PAUSE)


def _chunk(score: float) -> RetrievedChunk:
    return RetrievedChunk("material", score, uuid4(), uuid4())


def _generator(retrieval, brain, ideas, *, min_score=0.25, min_idea_tokens=5):
    return QuizGenerator(
        retrieval, brain, ideas, top_k=6, min_score=min_score, min_idea_tokens=min_idea_tokens
    )


async def test_generates_from_the_most_recent_idea():
    session_id, classroom_id = uuid4(), uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("an older idea entirely about something else"))
    ideas.record(session_id, _idea("a cache miss is when an item is not in the cache"))

    brain = RecordingBrain()
    generator = _generator(FakeRetrievalClient([_chunk(0.8)]), brain, ideas)

    quiz = await generator.generate(session_id, classroom_id, **BOUNDS)

    assert quiz.title == "Caching"
    # The newest idea is long enough on its own, so the older one is left out of the prompt.
    assert brain.calls[0][0] == "a cache miss is when an item is not in the cache"


async def test_a_thin_newest_idea_reaches_back_for_context():
    """A boundary firing on a pause can leave only a few words — not enough to build questions
    from. The newest idea still leads; the earlier one is prepended for context."""
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("caches store recently used items for fast access"))
    ideas.record(session_id, _idea("so that is a miss"))

    brain = RecordingBrain()
    generator = _generator(
        FakeRetrievalClient([_chunk(0.8)]), brain, ideas, min_idea_tokens=10
    )

    await generator.generate(session_id, uuid4(), **BOUNDS)

    prompt_text = brain.calls[0][0]
    assert prompt_text.startswith("caches store recently used")
    assert prompt_text.endswith("so that is a miss")


async def test_no_idea_yet_is_reported_distinctly():
    """Nothing is broken — the lecture has not said enough. The teacher fixes it by carrying on,
    so it must not be reported as a failure of the assistant."""
    generator = _generator(FakeRetrievalClient([]), RecordingBrain(), LastIdeaStore())

    with pytest.raises(NoIdeaAvailable):
        await generator.generate(uuid4(), uuid4(), **BOUNDS)


async def test_retrieval_failure_downgrades_to_ungrounded_rather_than_failing():
    """The evaluator would go silent here. A quiz written from the teacher's own words is still
    worth offering, so generation continues without material."""
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("a cache miss is when an item is not in the cache"))

    brain = RecordingBrain()
    retrieval = FakeRetrievalClient(error=RuntimeError("knowledge service down"))
    generator = _generator(retrieval, brain, ideas)

    quiz = await generator.generate(session_id, uuid4(), **BOUNDS)

    assert quiz.questions
    assert brain.calls[0][1] == []  # the brain was still called, with no material


async def test_chunks_below_the_score_threshold_are_not_sent():
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("a cache miss is when an item is not in the cache"))

    brain = RecordingBrain()
    retrieval = FakeRetrievalClient([_chunk(0.9), _chunk(0.05)])
    generator = _generator(retrieval, brain, ideas, min_score=0.25)

    await generator.generate(session_id, uuid4(), **BOUNDS)

    assert len(brain.calls[0][1]) == 1


async def test_no_relevant_material_still_generates():
    """The evaluator short-circuits and stays silent when nothing is relevant. Generation must
    not: the teacher explicitly asked for a quiz."""
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("a cache miss is when an item is not in the cache"))

    brain = RecordingBrain()
    generator = _generator(FakeRetrievalClient([_chunk(0.01)]), brain, ideas)

    quiz = await generator.generate(session_id, uuid4(), **BOUNDS)

    assert quiz.questions
    assert brain.calls[0][1] == []


async def test_brain_error_surfaces_instead_of_being_swallowed():
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("a cache miss is when an item is not in the cache"))

    generator = _generator(
        FakeRetrievalClient([_chunk(0.8)]),
        RecordingBrain(error=RuntimeError("brain unreachable")),
        ideas,
    )

    with pytest.raises(QuizGenerationFailed):
        await generator.generate(session_id, uuid4(), **BOUNDS)


async def test_unusable_reply_is_a_failure_not_an_empty_quiz():
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("a cache miss is when an item is not in the cache"))

    generator = _generator(
        FakeRetrievalClient([_chunk(0.8)]), RecordingBrain(quiz=None), ideas
    )

    with pytest.raises(QuizGenerationFailed):
        await generator.generate(session_id, uuid4(), **BOUNDS)


async def test_ideas_are_scoped_to_their_session():
    """One lecture's ideas must never be quizzed on in another's."""
    theirs, mine = uuid4(), uuid4()
    ideas = LastIdeaStore()
    ideas.record(theirs, _idea("someone else's lecture entirely"))

    generator = _generator(FakeRetrievalClient([]), RecordingBrain(), ideas)

    with pytest.raises(NoIdeaAvailable):
        await generator.generate(mine, uuid4(), **BOUNDS)
