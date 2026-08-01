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
from app.application.services.last_idea_store import LastIdeaStore
from app.application.services.token_estimate import estimate_tokens
from app.domain.quiz.generated_quiz import GeneratedQuiz

logger = logging.getLogger("liveassistant.quiz")


class NoIdeaAvailable(RuntimeError):
    """The session has not produced an idea to quiz on yet.

    Distinct from a generation failure: nothing is broken, the lecture simply has not said enough.
    That difference matters to the teacher, who can fix this one by carrying on talking.
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
        *,
        top_k: int,
        min_score: float,
        min_idea_tokens: int,
    ) -> None:
        self._retrieval = retrieval
        self._brain = brain
        self._ideas = ideas
        self._top_k = top_k
        self._min_score = min_score
        self._min_idea_tokens = min_idea_tokens

    async def generate(
        self,
        session_id: UUID,
        classroom_id: UUID,
        *,
        question_count: int,
        min_options: int,
        max_options: int,
    ) -> GeneratedQuiz:
        idea_text = self._recent_idea_text(session_id)
        if not idea_text:
            raise NoIdeaAvailable(
                "Nothing has been transcribed for this session yet, so there is no idea to build "
                "a quiz from."
            )

        chunks = await self._retrieve(classroom_id, idea_text)
        try:
            quiz = await self._brain.generate_quiz(
                idea_text,
                chunks,
                question_count=question_count,
                min_options=min_options,
                max_options=max_options,
            )
        except Exception as exc:  # noqa: BLE001 — reported to the teacher, not swallowed
            logger.warning("quiz_generation_failed", extra={"error_type": type(exc).__name__})
            raise QuizGenerationFailed("The assistant could not generate a quiz.") from exc

        if quiz is None or quiz.is_empty:
            raise QuizGenerationFailed(
                "The assistant could not turn that explanation into questions."
            )

        logger.info(
            "quiz_generated",
            extra={
                "questions": len(quiz.questions),
                "grounded": quiz.grounded,
                "citations": len(quiz.citations),
            },
        )
        return quiz

    def _recent_idea_text(self, session_id: UUID) -> str:
        """The newest finished idea, widened with earlier ones only if it is too thin.

        A boundary that fired on a pause can be a few seconds of speech — not enough to build
        several questions from. Reaching back keeps the quiz about what was just taught while
        giving the model enough to work with; the newest idea always leads.
        """
        recent = self._ideas.recent(session_id)
        if not recent:
            return ""

        chosen = [recent[-1]]
        # recent is oldest-first, so walk backwards from the one before the newest.
        for idea in reversed(recent[:-1]):
            if estimate_tokens(" ".join(i.text for i in chosen)) >= self._min_idea_tokens:
                break
            chosen.insert(0, idea)
        return " ".join(idea.text for idea in chosen).strip()

    async def _retrieve(self, classroom_id: UUID, idea_text: str):
        """Course material for the idea. Retrieval failure downgrades to ungrounded, never fatal —
        a quiz written from the teacher's own words is still worth offering."""
        try:
            chunks = await self._retrieval.retrieve(classroom_id, idea_text, self._top_k)
        except Exception as exc:  # noqa: BLE001
            logger.warning("quiz_retrieval_failed", extra={"error_type": type(exc).__name__})
            return []
        return [chunk for chunk in chunks if chunk.score >= self._min_score]
