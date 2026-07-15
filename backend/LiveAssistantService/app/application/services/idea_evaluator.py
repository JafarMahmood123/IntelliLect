"""Retrieval + evaluation on an idea boundary (LA-4).

For each ``CompletedIdea`` (from LA-3): retrieve the classroom's most relevant
material via ``RetrievalClient`` (KnowledgeService), then ask the ``BrainClient`` to
evaluate the idea against it — yielding either "no feedback" (the common case) or a
single grounded ``TeacherSuggestion``. This phase produces the OUTCOME only; it does
not deliver feedback (LA-5), wire a live session (LA-6), or rate-limit (LA-7).

Pure application logic: depends only on the ports + domain. Config arrives as
primitives (the DI factory unpacks ``Settings``), keeping this layer free of
``infrastructure`` imports. A failed evaluation NEVER breaks the loop — retrieval or
brain errors are logged and degrade to "no feedback".
"""

from __future__ import annotations

import logging
from collections.abc import AsyncIterator

from app.application.ports.brain_client import BrainClient
from app.application.ports.retrieval_client import RetrievalClient
from app.domain.entities.session_context import SessionContext
from app.domain.evaluation.evaluation_outcome import EvaluationOutcome
from app.domain.idea.completed_idea import CompletedIdea
from app.observability import metrics

logger = logging.getLogger("liveassistant.evaluator")


class IdeaEvaluator:
    """Orchestrates retrieve -> (short-circuit) -> evaluate for one idea."""

    def __init__(
        self,
        retrieval: RetrievalClient,
        brain: BrainClient,
        *,
        top_k: int,
        min_score: float,
    ) -> None:
        self._retrieval = retrieval
        self._brain = brain
        self._top_k = top_k
        self._min_score = min_score

    async def evaluate(
        self, idea: CompletedIdea, session: SessionContext
    ) -> EvaluationOutcome:
        try:
            with metrics.stage_timer("retrieval"):
                chunks = await self._retrieval.retrieve(
                    session.classroom_id, idea.text, self._top_k
                )
            relevant = [c for c in chunks if c.score >= self._min_score]

            # No-results short-circuit: nothing relevant -> no feedback, brain NOT called.
            if not relevant:
                logger.debug("No relevant material for idea; skipping brain.")
                return EvaluationOutcome.none()

            with metrics.stage_timer("evaluation"):
                return await self._brain.evaluate(idea, relevant)
        except Exception as exc:  # noqa: BLE001 — a failed evaluation must not break the loop
            # Log the error TYPE only (privacy): degrade to no feedback.
            logger.warning("evaluation_failed", extra={"error_type": type(exc).__name__})
            return EvaluationOutcome.none()


class IdeaEvaluationPipeline:
    """Thin connector: pipe a stream of CompletedIdeas through the IdeaEvaluator.

    Lets LA-3's ``BoundaryDetector.process(...)`` output be evaluated idea-by-idea
    into a stream of ``EvaluationOutcome``s. Injectable; NOT connected to a live
    session (that is LA-6).
    """

    def __init__(self, evaluator: IdeaEvaluator) -> None:
        self._evaluator = evaluator

    async def process(
        self, ideas: AsyncIterator[CompletedIdea], session: SessionContext
    ) -> AsyncIterator[EvaluationOutcome]:
        async for idea in ideas:
            yield await self._evaluator.evaluate(idea, session)
