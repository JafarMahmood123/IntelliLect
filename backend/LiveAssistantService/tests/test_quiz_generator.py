"""Deterministic, OFFLINE tests for quiz generation — no RagService, no brain.

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
        self.answer_calls: list[tuple[str, str, list[RetrievedChunk]]] = []
        self.kwargs: list[dict] = []

    async def generate_quiz(self, idea_text, chunks, **kwargs):
        self.calls.append((idea_text, list(chunks)))
        self.kwargs.append(kwargs)
        if self._error is not None:
            raise self._error
        return self._quiz

    async def generate_answers(self, question_text, idea_text, chunks, **kwargs):
        self.answer_calls.append((question_text, idea_text, list(chunks)))
        if self._error is not None:
            raise self._error
        return None if self._quiz is None else self._quiz.questions[0]


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


_next_start = 0


def _idea(text: str) -> CompletedIdea:
    """Ideas get consecutive, non-overlapping spans, as the buffer emits them.

    The span is an idea's identity for the used-already check, so a fixture that gave every idea
    the same one would make two different explanations look like the same explanation.
    """
    global _next_start
    _next_start += 1000
    return CompletedIdea(text, _next_start, _next_start + 900, 1, BoundaryTrigger.PAUSE)


def _chunk(score: float) -> RetrievedChunk:
    return RetrievedChunk("material", score, uuid4(), uuid4())


class FakeTranscripts:
    """Only ``assemble_text`` is exercised — it is the whole of what a full quiz reads."""

    def __init__(self, text: str = "", error: Exception | None = None):
        self.text = text
        self.error = error
        self.calls: list[UUID] = []

    async def assemble_text(self, session_id):
        self.calls.append(session_id)
        if self.error is not None:
            raise self.error
        return self.text


def _generator(
    retrieval,
    brain,
    ideas,
    *,
    min_score=0.25,
    min_idea_tokens=5,
    transcripts=None,
    full_max_chars=24_000,
    full_top_k=None,
):
    return QuizGenerator(
        retrieval,
        brain,
        ideas,
        transcripts or FakeTranscripts(),
        top_k=6,
        min_score=min_score,
        min_idea_tokens=min_idea_tokens,
        full_max_chars=full_max_chars,
        full_top_k=full_top_k,
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


# --- answers for a question the teacher wrote --------------------------------


async def test_answers_are_written_for_the_teachers_question():
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("a cache miss is when an item is not in the cache"))

    brain = RecordingBrain()
    generator = _generator(FakeRetrievalClient([_chunk(0.8)]), brain, ideas)

    question = await generator.generate_answers(
        session_id, uuid4(), "What is a cache miss?", min_options=2, max_options=4
    )

    assert question.options
    assert brain.answer_calls[0][0] == "What is a cache miss?"


async def test_answer_retrieval_is_keyed_on_the_question_as_well_as_the_idea():
    """The teacher may ask about a detail the idea only touches on; material matching the question
    is what keeps the options correct rather than merely plausible."""
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("caches store recently used items"))

    retrieval = FakeRetrievalClient([_chunk(0.8)])
    generator = _generator(retrieval, RecordingBrain(), ideas)

    await generator.generate_answers(
        session_id, uuid4(), "What is an eviction policy?", min_options=2, max_options=4
    )

    query = retrieval.calls[0][1]
    assert "eviction policy" in query
    assert "caches store recently used items" in query


async def test_answers_without_an_idea_are_reported_distinctly():
    generator = _generator(FakeRetrievalClient([]), RecordingBrain(), LastIdeaStore())

    with pytest.raises(NoIdeaAvailable):
        await generator.generate_answers(
            uuid4(), uuid4(), "A question", min_options=2, max_options=4
        )


async def test_unusable_answers_are_a_failure():
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("a cache miss is when an item is not in the cache"))

    generator = _generator(
        FakeRetrievalClient([_chunk(0.8)]), RecordingBrain(quiz=None), ideas
    )

    with pytest.raises(QuizGenerationFailed):
        await generator.generate_answers(
            session_id, uuid4(), "A question", min_options=2, max_options=4
        )


async def test_already_written_questions_are_passed_through_so_a_new_one_varies():
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("a cache miss is when an item is not in the cache"))

    brain = RecordingBrain()
    generator = _generator(FakeRetrievalClient([_chunk(0.8)]), brain, ideas)

    await generator.generate(
        session_id, uuid4(), question_count=1, min_options=2, max_options=4,
        avoid=["What is a cache hit?"],
    )

    assert brain.kwargs[0]["avoid"] == ["What is a cache hit?"]


# --- ideas are spent once they have been quizzed -------------------------------


async def test_an_idea_is_not_quizzed_twice():
    """Pressing Generate again should ask about what the teacher has said SINCE, not reword the
    same explanation into a second quiz."""
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("a cache miss is when an item is not in the cache"))

    brain = RecordingBrain()
    generator = _generator(FakeRetrievalClient([_chunk(0.8)]), brain, ideas)

    await generator.generate(session_id, uuid4(), **BOUNDS)
    ideas.record(session_id, _idea("eviction decides what leaves when the cache is full"))
    await generator.generate(session_id, uuid4(), **BOUNDS)

    assert brain.calls[0][0] == "a cache miss is when an item is not in the cache"
    assert brain.calls[1][0] == "eviction decides what leaves when the cache is full"


async def test_everything_already_quizzed_is_reported_differently_from_nothing_said():
    """Both are 409s, but the teacher acts on them differently — one means keep talking, the other
    means talk about something new. The same sentence for both would read as a broken assistant."""
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("a cache miss is when an item is not in the cache"))

    generator = _generator(FakeRetrievalClient([_chunk(0.8)]), RecordingBrain(), ideas)
    await generator.generate(session_id, uuid4(), **BOUNDS)

    with pytest.raises(NoIdeaAvailable) as spent:
        await generator.generate(session_id, uuid4(), **BOUNDS)
    assert "already been used" in str(spent.value)

    with pytest.raises(NoIdeaAvailable) as silent:
        await generator.generate(uuid4(), uuid4(), **BOUNDS)
    assert "Nothing has been transcribed" in str(silent.value)


async def test_a_failed_generation_does_not_spend_the_idea():
    """Otherwise a brain hiccup would cost the teacher the explanation they wanted to quiz, and no
    amount of retrying would get it back."""
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("a cache miss is when an item is not in the cache"))

    failing = _generator(
        FakeRetrievalClient([_chunk(0.8)]), RecordingBrain(error=RuntimeError("boom")), ideas
    )
    with pytest.raises(QuizGenerationFailed):
        await failing.generate(session_id, uuid4(), **BOUNDS)

    brain = RecordingBrain()
    working = _generator(FakeRetrievalClient([_chunk(0.8)]), brain, ideas)
    quiz = await working.generate(session_id, uuid4(), **BOUNDS)

    assert quiz.title == "Caching"
    assert brain.calls[0][0] == "a cache miss is when an item is not in the cache"


async def test_writing_answers_still_works_for_an_idea_already_quizzed():
    """The teacher is composing ONE quiz and wrote the question themselves. The explanation is
    context for answering it, not a topic being spent — so a used idea is still the right one."""
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("a cache miss is when an item is not in the cache"))

    brain = RecordingBrain()
    generator = _generator(FakeRetrievalClient([_chunk(0.8)]), brain, ideas)
    await generator.generate(session_id, uuid4(), **BOUNDS)

    question = await generator.generate_answers(
        session_id, uuid4(), "What is a cache miss?", min_options=2, max_options=4
    )

    assert question is not None
    assert brain.answer_calls[0][1] == "a cache miss is when an item is not in the cache"


# --- a full quiz over the whole lesson ------------------------------------------


async def test_a_full_quiz_reads_the_transcript_not_the_recent_ideas():
    """The idea history is bounded and holds minutes; the transcript is durable and holds the
    lesson. A whole-lesson quiz built from the ideas would silently be a quiz on the last five
    minutes."""
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("and finally, eviction"))

    brain = RecordingBrain()
    transcripts = FakeTranscripts("the entire lesson, from caches to eviction")
    generator = _generator(
        FakeRetrievalClient([_chunk(0.8)]), brain, ideas, transcripts=transcripts
    )

    await generator.generate(session_id, uuid4(), **BOUNDS, whole_session=True)

    assert brain.calls[0][0] == "the entire lesson, from caches to eviction"
    assert brain.kwargs[0]["whole_session"] is True


async def test_a_full_quiz_works_on_ideas_already_quizzed():
    """The whole point: a teacher runs quick tests through the lesson, then a full quiz at the end
    over everything — including the parts the quick tests already used up."""
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("a cache miss is when an item is not in the cache"))

    generator = _generator(
        FakeRetrievalClient([_chunk(0.8)]),
        RecordingBrain(),
        ideas,
        transcripts=FakeTranscripts("the whole lesson"),
    )
    await generator.generate(session_id, uuid4(), **BOUNDS)

    quiz = await generator.generate(session_id, uuid4(), **BOUNDS, whole_session=True)

    assert quiz.questions


async def test_a_full_quiz_spends_every_retained_idea():
    """It has just asked about all of them, so a quick test straight afterwards should say there
    is nothing new rather than re-ask what the full quiz covered."""
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("caches store recently used items"))
    ideas.record(session_id, _idea("eviction decides what leaves"))

    generator = _generator(
        FakeRetrievalClient([_chunk(0.8)]),
        RecordingBrain(),
        ideas,
        transcripts=FakeTranscripts("the whole lesson"),
    )
    await generator.generate(session_id, uuid4(), **BOUNDS, whole_session=True)

    with pytest.raises(NoIdeaAvailable) as spent:
        await generator.generate(session_id, uuid4(), **BOUNDS)
    assert "already been used" in str(spent.value)


async def test_a_full_quiz_with_no_transcript_is_reported_as_nothing_to_work_from():
    generator = _generator(
        FakeRetrievalClient([_chunk(0.8)]),
        RecordingBrain(),
        LastIdeaStore(),
        transcripts=FakeTranscripts(""),
    )

    with pytest.raises(NoIdeaAvailable):
        await generator.generate(uuid4(), uuid4(), **BOUNDS, whole_session=True)


async def test_an_over_long_transcript_keeps_the_most_recent_part():
    """Neither half is good to lose, but the earliest material is the likeliest to have been
    covered by the quick tests taken along the way."""
    session_id = uuid4()
    brain = RecordingBrain()
    transcripts = FakeTranscripts("A" * 500 + "THE-END")
    generator = _generator(
        FakeRetrievalClient([_chunk(0.8)]),
        brain,
        LastIdeaStore(),
        transcripts=transcripts,
        full_max_chars=1000,
    )

    await generator.generate(session_id, uuid4(), **BOUNDS, whole_session=True)

    sent = brain.calls[0][0]
    assert len(sent) <= 1000
    assert sent.endswith("THE-END")


async def test_a_full_quiz_retrieves_more_material_than_a_quick_test():
    """A whole lesson spans more ground than one idea; one idea's worth of material would leave
    most of the quiz ungrounded."""
    session_id = uuid4()
    retrieval = FakeRetrievalClient([_chunk(0.8)])
    generator = _generator(
        retrieval,
        RecordingBrain(),
        LastIdeaStore(),
        transcripts=FakeTranscripts("the whole lesson"),
        full_top_k=12,
    )

    await generator.generate(session_id, uuid4(), **BOUNDS, whole_session=True)

    assert retrieval.calls[-1][2] == 12


async def test_an_unreadable_transcript_is_a_failure_not_a_silent_quick_test():
    """Falling back to the recent ideas would hand the teacher a five-minute quiz labelled as a
    whole-lesson one, and they would have no way to tell."""
    session_id = uuid4()
    ideas = LastIdeaStore()
    ideas.record(session_id, _idea("a cache miss is when an item is not in the cache"))

    generator = _generator(
        FakeRetrievalClient([_chunk(0.8)]),
        RecordingBrain(),
        ideas,
        transcripts=FakeTranscripts(error=RuntimeError("database down")),
    )

    with pytest.raises(QuizGenerationFailed):
        await generator.generate(session_id, uuid4(), **BOUNDS, whole_session=True)
