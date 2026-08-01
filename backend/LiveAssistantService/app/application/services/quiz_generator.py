"""Generate a quiz about the idea the teacher has just finished explaining.

The teacher-facing counterpart of ``IdeaEvaluator``: same two inputs (the idea, plus the
classroom's material retrieved for it), same ``BrainClient``, but pulled by a teacher pressing a
button rather than pushed by a boundary firing.

Two deliberate differences from evaluation:

1. **No silence.** ``IdeaEvaluator`` short-circuits to "no feedback" when retrieval finds nothing
   relevant, because unsolicited feedback should bias to silence. Here a teacher explicitly asked
   for a quiz, so an empty result is a failure to report, not a decision. Retrieval finding nothing
   downgrades the quiz to ungrounded — it does not cancel it.
2. **Failures are raised, not swallowed.** A failed evaluation must never break the live loop; a
   failed generation must reach the teacher, who is standing in front of a class waiting for it.

Pure application logic: depends only on ports + domain, with config arriving as primitives.
"""

from __future__ import annotations

import logging
from uuid import UUID

from app.application.ports.brain_client import BrainClient
from app.application.ports.retrieval_client import RetrievalClient
from app.application.ports.transcript_repository import TranscriptRepository
from app.application.services.last_idea_store import LastIdeaStore
from app.application.services.token_estimate import estimate_tokens
from app.domain.idea.completed_idea import CompletedIdea
from app.domain.quiz.generated_quiz import GeneratedQuestion, GeneratedQuiz

logger = logging.getLogger("liveassistant.quiz")


class NoIdeaAvailable(RuntimeError):
    """The session has no fresh idea to quiz on.

    Distinct from a generation failure: nothing is broken, the lecture simply has not said enough
    that has not already been asked about. That difference matters to the teacher, who can fix this
    one by carrying on talking.
    """


class QuizGenerationFailed(RuntimeError):
    """Retrieval or the brain could not produce a usable quiz."""


class QuizGenerator:
    """Assembles idea + material -> brain -> validated quiz proposal."""

    def __init__(
        self,
        retrieval: RetrievalClient,
        brain: BrainClient,
        ideas: LastIdeaStore,
        transcripts: TranscriptRepository,
        *,
        top_k: int,
        min_score: float,
        min_idea_tokens: int,
        full_max_chars: int = 24_000,
        full_top_k: int | None = None,
    ) -> None:
        self._retrieval = retrieval
        self._brain = brain
        self._ideas = ideas
        self._transcripts = transcripts
        self._top_k = top_k
        self._min_score = min_score
        self._min_idea_tokens = min_idea_tokens
        self._full_max_chars = max(1000, full_max_chars)
        self._full_top_k = full_top_k or top_k

    async def generate(
        self,
        session_id: UUID,
        classroom_id: UUID,
        *,
        question_count: int,
        min_options: int,
        max_options: int,
        avoid: list[str] | None = None,
        whole_session: bool = False,
    ) -> GeneratedQuiz:
        """A quick test on what was just taught, or a full quiz on the whole lesson.

        The two differ in what they read, not in how they are generated. A quick test reads the
        IDEAS the boundary detector has just finished — recent, bounded, and spent once used. A
        full quiz reads the session TRANSCRIPT, which is durable and still holds the start of a
        lecture long after the idea history has evicted it.
        """
        if whole_session:
            idea_text = await self._require_transcript(session_id)
            ideas = self._ideas.recent(session_id)
            top_k = self._full_top_k
        else:
            # Only what has not been quizzed yet: pressing Generate a second time should ask about
            # what the teacher has said SINCE the first quiz, not produce a second quiz on the same
            # explanation with the questions reworded.
            ideas = self._require_ideas(session_id, unused_only=True)
            idea_text = " ".join(idea.text for idea in ideas).strip()
            top_k = self._top_k

        chunks = await self._retrieve(classroom_id, idea_text, top_k=top_k)
        try:
            quiz = await self._brain.generate_quiz(
                idea_text,
                chunks,
                question_count=question_count,
                min_options=min_options,
                max_options=max_options,
                avoid=avoid,
                whole_session=whole_session,
            )
        except Exception as exc:  # noqa: BLE001 — reported to the teacher, not swallowed
            logger.warning("quiz_generation_failed", extra={"error_type": type(exc).__name__})
            raise QuizGenerationFailed("The assistant could not generate a quiz.") from exc

        if quiz is None or quiz.is_empty:
            raise QuizGenerationFailed(
                "The assistant could not turn that explanation into questions."
            )

        # Marked only once a usable quiz exists. Spending the ideas before the brain answered would
        # mean a failed generation quietly cost the teacher the explanation they wanted to quiz.
        #
        # A full quiz spends EVERY retained idea, because it has just asked about all of them. A
        # quick test straight afterwards should say "nothing new" rather than re-ask what the full
        # quiz already covered.
        self._ideas.mark_used(session_id, ideas)

        logger.info(
            "quiz_generated",
            extra={
                "questions": len(quiz.questions),
                "grounded": quiz.grounded,
                "citations": len(quiz.citations),
                "corrections": len(quiz.corrections),
                "ideas_used": len(ideas),
                "whole_session": whole_session,
            },
        )
        return quiz

    async def generate_answers(
        self,
        session_id: UUID,
        classroom_id: UUID,
        question_text: str,
        *,
        min_options: int,
        max_options: int,
    ) -> GeneratedQuestion:
        """Write options for a question the teacher typed themselves.

        Retrieval is keyed on the QUESTION as well as the idea: the teacher may have written it
        about a detail the idea only touches on, and material matching the question is what keeps
        the answers correct rather than merely plausible.
        """
        # Every retained idea, used or not. The teacher is composing ONE quiz here and has written
        # the question themselves; the explanation is context for answering it, not a topic being
        # spent, so an idea already quizzed is still the right thing to answer from.
        ideas = self._require_ideas(session_id, unused_only=False)
        idea_text = " ".join(idea.text for idea in ideas).strip()

        chunks = await self._retrieve(classroom_id, f"{question_text}\n{idea_text}")
        try:
            question = await self._brain.generate_answers(
                question_text,
                idea_text,
                chunks,
                min_options=min_options,
                max_options=max_options,
            )
        except Exception as exc:  # noqa: BLE001 — reported to the teacher, not swallowed
            logger.warning("answers_generation_failed", extra={"error_type": type(exc).__name__})
            raise QuizGenerationFailed("The assistant could not write answers.") from exc

        if question is None:
            raise QuizGenerationFailed(
                "The assistant could not write usable answers for that question."
            )

        logger.info("answers_generated", extra={"options": len(question.options)})
        return question

    async def _require_transcript(self, session_id: UUID) -> str:
        """The whole lesson, as recorded. Capped, because a two-hour lecture would not fit.

        When the cap bites the TAIL is kept. Neither half is a good thing to lose, but the earliest
        material is the likeliest to have been covered by the quick tests taken along the way,
        whereas nothing has yet asked about what was said in the last stretch. The teacher is told
        which lesson the quiz covers by its questions, so a silently narrower quiz is a
        disappointment rather than a fault — but it is still logged.
        """
        try:
            text = (await self._transcripts.assemble_text(session_id)).strip()
        except Exception as exc:  # noqa: BLE001
            logger.warning(
                "quiz_transcript_read_failed", extra={"error_type": type(exc).__name__}
            )
            raise QuizGenerationFailed(
                "The assistant could not read this session's transcript."
            ) from exc

        if not text:
            raise NoIdeaAvailable(
                "Nothing has been transcribed for this session yet, so there is no lesson to "
                "build a quiz from."
            )

        if len(text) > self._full_max_chars:
            logger.info(
                "quiz_transcript_truncated",
                extra={"length": len(text), "kept": self._full_max_chars},
            )
            text = text[-self._full_max_chars :]
        return text

    def _require_ideas(self, session_id: UUID, *, unused_only: bool) -> list[CompletedIdea]:
        ideas = self._recent_ideas(session_id, unused_only=unused_only)
        if ideas:
            return ideas

        # Two different situations wearing the same status code, so they get different words. One
        # is fixed by talking; the other is fixed by talking about something NEW, and a teacher who
        # has just been told "nothing transcribed yet" mid-lecture would reasonably think the
        # assistant was broken.
        if unused_only and self._ideas.recent(session_id):
            raise NoIdeaAvailable(
                "Everything said since the last quiz has already been used. Carry on teaching and "
                "generate again once you have explained something new."
            )
        raise NoIdeaAvailable(
            "Nothing has been transcribed for this session yet, so there is no idea to build "
            "a quiz from."
        )

    def _recent_ideas(self, session_id: UUID, *, unused_only: bool) -> list[CompletedIdea]:
        """The newest finished idea, widened with earlier ones only if it is too thin.

        A boundary that fired on a pause can be a few seconds of speech — not enough to build
        several questions from. Reaching back keeps the quiz about what was just taught while
        giving the model enough to work with; the newest idea always leads.
        """
        recent = self._ideas.recent(session_id, include_used=not unused_only)
        if not recent:
            return []

        chosen = [recent[-1]]
        # recent is oldest-first, so walk backwards from the one before the newest.
        for idea in reversed(recent[:-1]):
            if estimate_tokens(" ".join(i.text for i in chosen)) >= self._min_idea_tokens:
                break
            chosen.insert(0, idea)
        return chosen

    async def _retrieve(self, classroom_id: UUID, idea_text: str, *, top_k: int | None = None):
        """Course material for the idea. Retrieval failure downgrades to ungrounded, never fatal —
        a quiz written from the teacher's own words is still worth offering."""
        try:
            chunks = await self._retrieval.retrieve(
                classroom_id, idea_text, top_k or self._top_k
            )
        except Exception as exc:  # noqa: BLE001
            logger.warning("quiz_retrieval_failed", extra={"error_type": type(exc).__name__})
            return []
        return [chunk for chunk in chunks if chunk.score >= self._min_score]
